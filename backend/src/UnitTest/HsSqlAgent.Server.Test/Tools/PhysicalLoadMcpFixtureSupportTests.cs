using Common.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Fast characterization contracts for the test-only five-provider fixture.
/// The native database setup/teardown remains covered by mandatory live CI.
/// </summary>
public sealed class PhysicalLoadMcpFixtureSupportTests
{
    [Theory]
    [InlineData("Postgres", "physical-db", "load_1.items")]
    [InlineData("MySQL", "physical-db", "load_2.items")]
    [InlineData("MsSqlServer", "physical-db", "dbo.load_3")]
    [InlineData("Oracle", "physical-db", "SQLCORE.OL_LOAD")]
    [InlineData("Firebird", "physical-db", "FL_LOAD")]
    public void FiveNativeLoadFixtures_PreserveAccessKeyAndWhitelistMetadata(
        string provider, string database, string allowedTable)
    {
        var accessor = PhysicalLoadMcpFixtureSupport.CreateAccessor(
            provider, "native-test-connection", database, allowedTable, "load-trace");
        var context = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpContext>(
            accessor.HttpContext);

        Assert.Equal("load-trace", context.TraceIdentifier);
        Assert.Equal(provider, context.Items[McpContextItemKeys.SqlProvider]);
        Assert.Equal("native-test-connection",
            context.Items[McpContextItemKeys.SqlConnectionString]);
        Assert.Equal(database, context.Items[McpContextItemKeys.DatabaseName]);
        Assert.Equal(allowedTable, context.Items[McpContextItemKeys.TableWhitelist]);
        Assert.Equal("archive_order", context.Items[McpContextItemKeys.AllowedTools]);
        Assert.Equal(7, context.Items[McpContextItemKeys.AccessKeyId]);
        Assert.Equal(42, context.Items[McpContextItemKeys.DbManagementId]);
    }

    [Fact]
    public async Task AdminAuditFixture_UsesIndependentDurableSqliteDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "load-helper-audit-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            await using (var audit = await PhysicalLoadMcpFixtureSupport.CreateAuditContextAsync(
                path, TestContext.Current.CancellationToken))
            {
                Assert.True(await audit.Database.CanConnectAsync(
                    TestContext.Current.CancellationToken));
                Assert.True(File.Exists(path));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
