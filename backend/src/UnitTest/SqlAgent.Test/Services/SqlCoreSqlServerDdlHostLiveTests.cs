using System.Data.Common;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using Moq;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G4 RED: SQL Server DDL Host must prove a single CREATE TABLE against an
/// independent physical SQL Server 2022 database, not a virtual catalog.
/// </summary>
public sealed class SqlCoreSqlServerDdlHostLiveTests
{
    private sealed class Connections : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new SqlConnection(connectionString);
    }

    private sealed class Approver(
        Func<DdlApprovalRequest, CancellationToken, ValueTask<DdlApprovalDecision>> callback)
        : IDdlApprovalProvider
    {
        public ValueTask<DdlApprovalDecision> RequestAsync(
            DdlApprovalRequest request, CancellationToken token = default) => callback(request, token);
    }

    private static IDdlApprovalProvider Approve() =>
        new Approver((request, _) => ValueTask.FromResult(
            new DdlApprovalDecision(DdlApprovalState.Approved,
                request.RequestId, request.ApprovalFingerprint, "human:independent-reviewer")));

    private static ISqlProvider Provider()
    {
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.MsSqlServer);
        provider.SetupGet(x => x.Connections).Returns(new Connections());
        return provider.Object;
    }

    private static SqlServerDdlRuntime Runtime() => new(new SqlServerDdlExecutionOptions
    {
        Enabled = true,
        AllowedSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo" },
        AllowCreateTable = true,
        PolicyRevision = "sqlserver-g4-test"
    });

    [Fact]
    public async Task DisabledByDefault_AndUnsafeStatementCannotOpenConnection()
    {
        var options = new SqlServerDdlExecutionOptions();
        Assert.False(options.Enabled);
        Assert.False(options.AllowCreateTable);
        Assert.Empty(options.AllowedSchemas);
        var strict = new Mock<ISqlProvider>(MockBehavior.Strict);
        var actor = new DdlActorContext("mcp-key:1", "db-management:2", "master");
        var denied = await new SqlServerDdlRuntime().RunAsync(
            strict.Object, "unused", "CREATE TABLE dbo.hsql_test (id INT)",
            actor, false, null, TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, denied.Status);
        strict.VerifyNoOtherCalls();

        strict.SetupGet(x => x.Type).Returns(SqlAgentToolType.MsSqlServer);
        foreach (var sql in new[]
        {
            "CREATE TABLE hsql_test (id INT)",
            "DROP TABLE dbo.hsql_test",
            "CREATE TABLE dbo.hsql_test (id INT); DROP TABLE dbo.hsql_test",
            "CREATE TABLE dbo.hsql_test (id INT PRIMARY KEY)",
            "ALTER TABLE dbo.hsql_test ADD nick INT",
            "CREATE INDEX idx ON dbo.hsql_test(id)"
        })
        {
            var result = await Runtime().RunAsync(strict.Object, "unused", sql,
                actor, true, null, TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
        }
        strict.VerifyGet(x => x.Connections, Times.Never);
    }

    [Fact]
    public async Task ReadOnlyPreview_ApprovalCommit_AndPhysicalSchemaDrift()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQL Server DDL live test requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Real SQL Server 2022 required.");
        }

        var token = TestContext.Current.CancellationToken;
        var database = "hssql_g4_" + Guid.NewGuid().ToString("N")[..18];
        await using var admin = new SqlConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await admin.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        await ExecuteAsync(admin, "CREATE DATABASE [" + database + "]", token);
        try
        {
            var target = new SqlConnectionStringBuilder(cs)
            {
                InitialCatalog = database, Pooling = false
            }.ConnectionString;
            var actor = new DdlActorContext("mcp-key:1", "db-management:2", database);
            var provider = Provider();
            var runtime = Runtime();
            var sql = "CREATE TABLE dbo.hsql_g4_widgets (id INT NOT NULL)";
            var preview = await runtime.RunAsync(provider, target, sql,
                actor, true, null, token);
            Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
            Assert.False(preview.CommitConfirmed);
            await using (var verify = new SqlConnection(target))
            {
                await verify.OpenAsync(token);
                Assert.False(await TableExistsAsync(verify, "hsql_g4_widgets", token));
            }

            var denied = await runtime.RunAsync(provider, target, sql,
                actor, false, new Approver((r, _) => ValueTask.FromResult(
                    new DdlApprovalDecision(DdlApprovalState.Rejected,
                        r.RequestId, r.ApprovalFingerprint, "human:reviewer"))), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, denied.Status);

            var forged = await runtime.RunAsync(provider, target, sql,
                actor, false, new Approver((r, _) => ValueTask.FromResult(
                    new DdlApprovalDecision(DdlApprovalState.Approved,
                        r.RequestId, new string('0', 64), "human:reviewer"))), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, forged.Status);

            var committed = await runtime.RunAsync(provider, target, sql,
                actor, false, Approve(), token);
            Assert.Equal(DdlRuntimeStatus.Committed, committed.Status);
            Assert.True(committed.CommitConfirmed);
            await using (var verify = new SqlConnection(target))
            {
                await verify.OpenAsync(token);
                Assert.True(await TableExistsAsync(verify, "hsql_g4_widgets", token));
                await using var columns = new SqlCommand(
                    "SELECT c.name,t.name,c.is_nullable FROM sys.columns c " +
                    "JOIN sys.types t ON t.user_type_id=c.user_type_id " +
                    "WHERE c.object_id=OBJECT_ID(N'dbo.hsql_g4_widgets', 'U')", verify);
                await using var reader = await columns.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));
                Assert.Equal("id", reader.GetString(0), ignoreCase: true);
                Assert.Equal("int", reader.GetString(1));
                Assert.False(reader.GetBoolean(2));
                Assert.False(await reader.ReadAsync(token));
            }

            var repeated = await runtime.RunAsync(provider, target, sql,
                actor, false, Approve(), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, repeated.Status);
            Assert.False(repeated.CommitConfirmed);
        }
        finally
        {
            await ExecuteAsync(admin, "ALTER DATABASE [" + database + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
                CancellationToken.None);
            await ExecuteAsync(admin, "DROP DATABASE [" + database + "]", CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken token)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> TableExistsAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        await using var cmd = new SqlCommand(
            "SELECT CASE WHEN OBJECT_ID(@name,'U') IS NOT NULL THEN 1 ELSE 0 END", connection);
        cmd.Parameters.AddWithValue("name", "dbo." + table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token)) == 1;
    }
}
