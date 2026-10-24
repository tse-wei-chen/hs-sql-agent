using Admin.Service.Data;
using HsSqlAgent.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HsSqlAgent.Server.Test.Authorization;

public sealed class ManagementApiKeyPermissionTests
{
    [Fact]
    public void Catalog_DeniesIdentitySecurityAndManagementKeyAdministration()
    {
        Assert.DoesNotContain(ManagementApiKeyPermissions.Grantable, p =>
            p.StartsWith("/auth/", StringComparison.Ordinal) ||
            p.StartsWith("/runtime/security.", StringComparison.Ordinal) ||
            p.Contains("management-api-keys", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => ManagementApiKeyPermissions.Validate(
            ["/auth/user.edit"]));
        Assert.Throws<ArgumentException>(() => ManagementApiKeyPermissions.Validate([]));
        Assert.Equal(["/runtime/custom-tools.create"],
            ManagementApiKeyPermissions.Validate(
                ["/runtime/custom-tools.create", "/runtime/custom-tools.create"]));
    }

    [Fact]
    public async Task Lifecycle_UsesIndependentHashedExpiringRevocableCredentials()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options;
        await using var context = new AdminContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var service = new ManagementApiKeyService(context);
        var ct = TestContext.Current.CancellationToken;
        var issued = await service.IssueAsync(new ManagementApiKeyRequest(
            "provisioner", DateTime.UtcNow.AddDays(1),
            ["/runtime/custom-tools.create", "/runtime/mcp-keys.create"]), "admin:1", ct);

        Assert.StartsWith("hsa_mgmt_", issued.PlaintextKey, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.PlaintextKey,
            (await service.ListAsync(ct)).Single().KeyPrefix, StringComparison.Ordinal);
        Assert.NotEqual(issued.PlaintextKey, (await context.ManagementApiKeys.SingleAsync(ct)).KeyHash);
        Assert.NotNull(await service.ValidateAsync(issued.PlaintextKey, ct));
        Assert.Null(await service.ValidateAsync("hsql_mcp_" + issued.PlaintextKey, ct));
        Assert.Null(await service.ValidateAsync(issued.PlaintextKey + "x", ct));

        var rotated = await service.RotateAsync(issued.Key.Id, "admin:1", ct);
        Assert.NotNull(rotated);
        Assert.NotEqual(issued.PlaintextKey, rotated!.PlaintextKey);
        Assert.Null(await service.ValidateAsync(issued.PlaintextKey, ct));
        Assert.NotNull(await service.ValidateAsync(rotated.PlaintextKey, ct));
        Assert.True(await service.RevokeAsync(rotated.Key.Id, "admin:1", ct));
        Assert.Null(await service.ValidateAsync(rotated.PlaintextKey, ct));
    }

    [Fact]
    public async Task Issue_RejectsMissingAndUnsafePermissionsAndExpiry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var context = new AdminContext(
            new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var service = new ManagementApiKeyService(context);
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<ArgumentException>(() => service.IssueAsync(
            new("invalid", DateTime.UtcNow.AddDays(1), ["/auth/role.edit"]), null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.IssueAsync(
            new("expired", DateTime.UtcNow.AddDays(-1), ["/runtime/mcp-keys.create"]), null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.IssueAsync(
            new("long lived", DateTime.UtcNow.AddDays(400), ["/runtime/mcp-keys.create"]), null, ct));
    }
}
