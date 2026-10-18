using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;
using Moq.EntityFrameworkCore;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Authorization is intentionally re-read from the persistence model; these
/// checks must reject changed state even while HTTP Items are unchanged.
/// </summary>
public sealed class McpDmlCommitAuthorizationVerifierTests
{
    private sealed class Fixture
    {
        internal const string ConnectionString = "Database=orders;Host=localhost";
        internal readonly McpAccessKey Key = new()
        {
            Id = 7, Name = "approved", KeyHash = "stored-hash", KeyPrefix = "key-test",
            IsActive = true, DbManagementId = 42, AllowedTools = "execute_dml_sql",
            TableWhitelist = "public.orders"
        };
        internal readonly DbManagement Database = new()
        {
            Id = 42, Name = "orders", Database = "orders", Host = "localhost",
            SqlProvider = "Postgres", PasswordHash = "encrypted", UpdatedAt = DateTime.UtcNow
        };
        internal readonly CustomSqlTool CustomTool = new()
        {
            Id = 50, Name = "archive_order", Type = "DML", Status = "Published",
            DbManagementId = 42, PublishedRevisionId = 9
        };
        internal readonly McpDmlCommitAuthorizationVerifier Verifier;

        internal Fixture()
        {
            var context = new Mock<IAdminContext>();
            context.Setup(x => x.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey> { Key });
            context.Setup(x => x.DbManagement).ReturnsDbSet(new List<DbManagement> { Database });
            context.Setup(x => x.CustomSqlTools).ReturnsDbSet(new List<CustomSqlTool> { CustomTool });
            var dbSetter = new Mock<IDbSetterService>();
            dbSetter.Setup(x => x.BuildDbConnectionAsync(
                It.IsAny<BuildDbConnectionModel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((BuildDbConnectionModel model, CancellationToken _) =>
                    $"Database={model.Database};Host={model.Host}");
            var crypto = new Mock<ICryptoService>();
            crypto.Setup(x => x.DecryptText(It.IsAny<string>(), It.IsAny<byte[]>()))
                .Returns("secret");
            Verifier = new McpDmlCommitAuthorizationVerifier(
                context.Object, dbSetter.Object, crypto.Object,
                Options.Create(new McpKeySettings
                {
                    HmacSecretKey = "0123456789abcdef0123456789abcdef"
                }));
        }

        internal static McpSqlExecutionScope Scope(string tool = "execute_dml_sql")
        {
            var context = new DefaultHttpContext();
            context.Items[McpContextItemKeys.AllowedTools] = "execute_dml_sql,archive_order";
            context.Items[McpContextItemKeys.TableWhitelist] = "public.orders";
            context.Items[McpContextItemKeys.AllowedQueryColumns] = null;
            context.Items[McpContextItemKeys.AccessKeyId] = 7;
            context.Items[McpContextItemKeys.DbManagementId] = 42;
            context.Items[McpContextItemKeys.DatabaseName] = "orders";
            context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
            return McpSqlExecutionScope.Capture(context, tool, SqlAgentToolType.Postgres);
        }

        internal Task<McpDmlCommitAuthorizationSnapshot> Capture(
            McpSqlExecutionScope? scope = null, int? toolId = null, int? revision = null) =>
            Verifier.CaptureAsync(scope ?? Scope(), ConnectionString, toolId, revision,
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnchangedStoredAuthorization_RemainsValidAfterHumanApproval()
    {
        var fixture = new Fixture();
        var first = await fixture.Capture();
        var latest = await fixture.Capture();
        first.EnsureUnchanged(latest);
        Assert.Contains("public.orders", latest.AllowedTables!);
    }

    [Fact]
    public async Task RevokedKey_DoesNotInheritValidOriginalHttpContext()
    {
        var fixture = new Fixture();
        var originalScope = Fixture.Scope();
        await fixture.Capture(originalScope);
        fixture.Key.IsActive = false;
        fixture.Key.RevokedAt = DateTime.UtcNow;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Capture(originalScope));
    }

    [Fact]
    public async Task ExpiredOrColumnRestrictedKey_IsRejectedAfterApproval()
    {
        var fixture = new Fixture();
        var scope = Fixture.Scope();
        await fixture.Capture(scope);
        fixture.Key.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Capture(scope));
        fixture.Key.ExpiresAt = null;
        fixture.Key.AllowedQueryColumns = "id";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Capture(scope));
    }

    [Fact]
    public async Task ToolPermissionAndTargetBinding_AreFreshlyChecked()
    {
        var fixture = new Fixture();
        var scope = Fixture.Scope();
        await fixture.Capture(scope);
        fixture.Key.AllowedTools = "execute_query_sql";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Capture(scope));
        fixture.Key.AllowedTools = "execute_dml_sql";
        fixture.Key.DbManagementId = 43;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Capture(scope));
    }

    [Fact]
    public async Task ChangedTables_DenyEvenWhenRequestItemsHaveNotChanged()
    {
        var fixture = new Fixture();
        var scope = Fixture.Scope();
        await fixture.Capture(scope);
        fixture.Key.TableWhitelist = "public.orders,public.secrets";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Capture(scope));
    }

    [Fact]
    public async Task NewDatabaseCredentialsOrTarget_DenyPriorApproval()
    {
        var fixture = new Fixture();
        var scope = Fixture.Scope();
        await fixture.Capture(scope);
        fixture.Database.Host = "new-host";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Capture(scope));
        fixture.Database.Host = "localhost";
        fixture.Database.Database = "other";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Capture(scope));
    }

    [Fact]
    public async Task PublishedCustomDmlRevision_CannotChangeAcrossApproval()
    {
        var fixture = new Fixture();
        fixture.Key.AllowedTools = "archive_order";
        var scope = Fixture.Scope("archive_order");
        var initial = await fixture.Capture(scope, toolId: 50, revision: 9);
        fixture.CustomTool.PublishedRevisionId = 10;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Capture(scope, toolId: 50, revision: 9));
        fixture.CustomTool.PublishedRevisionId = 9;
        initial.EnsureUnchanged(await fixture.Capture(scope, toolId: 50, revision: 9));
    }

    [Fact]
    public async Task PermissionFingerprint_DetectsAnyChangedStoredApprovalContext()
    {
        var fixture = new Fixture();
        var scope = Fixture.Scope();
        var first = await fixture.Capture(scope);
        fixture.Key.AllowedTools = "execute_dml_sql,execute_query_sql";
        var second = await fixture.Capture(scope);
        Assert.Throws<UnauthorizedAccessException>(() => first.EnsureUnchanged(second));
    }

    [Fact]
    public void AuthenticationCache_MustNotReusePreColumnPolicyEntries()
    {
        Assert.StartsWith("mcp_auth_v5_", McpAccessKeyCacheKeys.ForStoredHash("hash"));
    }
}
