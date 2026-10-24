using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Controllers;

/// <summary>
/// Runs the real endpoint's HasPermission filter and the controller action.
/// Tests the security boundary, rather than bypassing it by calling a method
/// with a forged principal.
/// </summary>
public sealed class SecretMigrationAuthorizationTests
{
    private const string Hmac = "migration-api-test-hmac-at-least-32-bytes-long";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthorizedHuman_CannotStartMigration(bool authenticated)
    {
        await using var store = await Store.CreateAsync();
        var context = new DefaultHttpContext();
        if (authenticated)
            context.User = Human("ordinary-user");
        var (controller, filter) = Setup(store, context, authorized: false);

        var result = await AuthorizeAsync(filter, context);
        if (authenticated)
            Assert.IsType<ForbidResult>(result);
        else
            Assert.IsType<ChallengeResult>(result);

        // ASP.NET MVC does not invoke controller actions after an auth result.
        Assert.Equal(0, await store.Context.AuditLogs.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.Context.DbManagement.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MachineKey_CannotReachSecurityMigrationEvenWhenItClaimsSecurityEdit()
    {
        await using var store = await Store.CreateAsync();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = "mock-machine-token";
        var apiKeys = new Mock<IManagementApiKeyService>();
        apiKeys.Setup(x => x.ValidateAsync("mock-machine-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidManagementApiKey(
                17, "claimed-editor", ["/runtime/security.edit"]));
        var (_, filter) = Setup(store, context, authorized: true, apiKeys.Object);

        // /runtime/security.edit must never be grantable to machine keys.
        Assert.False(ManagementApiKeyPermissions.IsGrantable("/runtime/security.edit"));
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(await AuthorizeAsync(filter, context)).StatusCode);
        Assert.Equal(0, await store.Context.AuditLogs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidHumanEditor_CanInventoryWithoutHistoricalMigration()
    {
        await using var store = await Store.CreateAsync();
        var plaintext = "private-database-password";
        store.Context.DbManagement.Add(new DbManagement
        {
            Name = "primary",
            PasswordHash = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await store.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var context = new DefaultHttpContext { User = Human("security-editor") };
        var (controller, filter) = Setup(store, context, authorized: true);
        Assert.Null(await AuthorizeAsync(filter, context));

        var inventory = Assert.IsType<OkObjectResult>(
            await controller.Inventory(TestContext.Current.CancellationToken));
        var before = Assert.IsType<SecretMigrationInventory>(inventory.Value);
        Assert.Equal(1, before.LegacyDatabasePasswords);

        Assert.DoesNotContain(plaintext, System.Text.Json.JsonSerializer.Serialize(inventory.Value));
        Assert.Null(typeof(SecretMigrationController).GetMethod("MigrateDatabasePasswords"));
        Assert.Empty(await store.Context.AuditLogs.ToListAsync(TestContext.Current.CancellationToken));
    }

    private static ClaimsPrincipal Human(string subject) =>
        new(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, subject)], "test-human-jwt"));

    private static (SecretMigrationController Controller, IAsyncAuthorizationFilter Filter) Setup(
        Store store, HttpContext http, bool authorized,
        IManagementApiKeyService? apiKeyService = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHsSqlAgentPermissionAuthorizer>(
            new StubAuthorizer(authorized));
        services.AddSingleton<IManagementApiKeyService>(
            apiKeyService ?? new Mock<IManagementApiKeyService>().Object);
        var provider = services.BuildServiceProvider();
        http.RequestServices = provider;

        var controller = new SecretMigrationController(store.Migration)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = http
            }
        };
        var method = typeof(SecretMigrationController)
            .GetMethod(nameof(SecretMigrationController.Inventory))!;
        var attribute = Assert.Single(method.GetCustomAttributes(typeof(HasPermissionAttribute), true)
            .OfType<HasPermissionAttribute>());
        Assert.Equal("/runtime/security.edit", attribute.Permission);
        return (controller, (IAsyncAuthorizationFilter)attribute.CreateInstance(provider));
    }

    private static async Task<IActionResult?> AuthorizeAsync(
        IAsyncAuthorizationFilter filter, HttpContext context)
    {
        var action = new ActionContext(context, new RouteData(), new ControllerActionDescriptor());
        var auth = new AuthorizationFilterContext(action, new List<IFilterMetadata>());
        await filter.OnAuthorizationAsync(auth);
        return auth.Result;
    }

    private sealed class StubAuthorizer(bool allows) : IHsSqlAgentPermissionAuthorizer
    {
        public string? AuthenticationScheme => null;
        public ValueTask<bool> AuthorizeAsync(
            HttpContext context, IReadOnlyCollection<string> permissions,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(allows);
    }

    private sealed class WritePolicy : ISecretWriteRolloutPolicy
    {
        public bool EnableV2DatabasePasswordWrites => true;
        public bool EnableV2DurableDmlWrites => false;
    }

    private sealed class Store : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AdminContext Context { get; }
        public DatabaseSecretMigrationService Migration { get; }

        private Store(SqliteConnection connection, AdminContext context,
            DatabaseSecretMigrationService migration)
        {
            _connection = connection;
            Context = context;
            Migration = migration;
        }

        public static async Task<Store> CreateAsync(bool enabled = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AdminContext>()
                .UseSqlite(connection).Options;
            var context = new AdminContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var ring = new SecretKeyRing(new Dictionary<SecretPurpose, SecretKeySet>
            {
                [SecretPurpose.DatabasePassword] = new SecretKeySet
                {
                    ActiveKeyId = "db_key_1",
                    Keys = new Dictionary<string, byte[]>
                    {
                        ["db_key_1"] = Enumerable.Repeat((byte)0x45, 32).ToArray()
                    }
                }
            });
            var migrator = new DatabaseSecretMigrationService(
                context, new CryptoService(),
                Options.Create(new McpKeySettings { HmacSecretKey = Hmac }),
                new VersionedSecretProtector(ring, System.Text.Encoding.UTF8.GetBytes(Hmac)),
                ring, enabled ? new WritePolicy() : null);
            return new Store(connection, context, migrator);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
