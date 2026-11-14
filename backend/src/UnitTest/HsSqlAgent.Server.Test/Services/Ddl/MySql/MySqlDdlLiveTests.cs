using System.Data.Common;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using Moq;
using MySql.Data.MySqlClient;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>G5 RED: MySQL atomic DDL is not user-rollbackable DDL.</summary>
public sealed class MySqlDdlLiveTests
{
    private sealed class Connections : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new MySqlConnection(connectionString);
    }

    private sealed class Approver(
        Func<DdlApprovalRequest, CancellationToken, ValueTask<DdlApprovalDecision>> callback)
        : IDdlApprovalProvider
    {
        public ValueTask<DdlApprovalDecision> RequestAsync(
            DdlApprovalRequest request, CancellationToken ct = default) => callback(request, ct);
    }

    private static ISqlProvider Provider()
    {
        var mock = new Mock<ISqlProvider>(MockBehavior.Strict);
        mock.SetupGet(x => x.Type).Returns(SqlAgentToolType.MySQL);
        mock.SetupGet(x => x.Connections).Returns(new Connections());
        return mock.Object;
    }

    private static MySqlDdlRuntime Runtime(string db) => new(new MySqlDdlExecutionOptions
    {
        Enabled = true,
        AllowCreateTable = true,
        AllowedDatabases = new HashSet<string>(StringComparer.Ordinal) { db },
        PolicyRevision = "mysql84-live-g5"
    });

    private static IDdlApprovalProvider Decision(DdlApprovalState state) =>
        new Approver((r, _) => ValueTask.FromResult(
            new DdlApprovalDecision(state, r.RequestId, r.ApprovalFingerprint,
                "human:independent-reviewer")));

    [Fact]
    public async Task DefaultDisabledAndUnprovenGrammarNeverOpenDatabase()
    {
        var strict = new Mock<ISqlProvider>(MockBehavior.Strict);
        var actor = new DdlActorContext("mcp-key:11", "db-management:23", "test");
        var disabled = await new MySqlDdlRuntime().RunAsync(
            strict.Object, "unused", "CREATE TABLE G5_T(ID INT)", actor,
            false, null, TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, disabled.Status);
        strict.VerifyNoOtherCalls();

        strict.SetupGet(x => x.Type).Returns(SqlAgentToolType.MySQL);
        foreach (var sql in new[]
        {
            "DROP TABLE G5_T",
            "CREATE TEMPORARY TABLE G5_T(ID INT)",
            "CREATE TABLE G5_T(ID INT); DROP TABLE G5_T",
            "CREATE TABLE G5_T(ID INT PRIMARY KEY)",
            "CREATE TABLE G5_T AS SELECT 1",
            "ALTER TABLE G5_T ADD OTHER INT",
            "CREATE INDEX IX_G5 ON G5_T(ID)",
            "CREATE TABLE G5_T(ID INT, NAME VARCHAR(20))",
            "CREATE TABLE other_db.G5_T(ID INT)"
        })
        {
            var result = await Runtime("test").RunAsync(
                strict.Object, "unused", sql, actor, true, null,
                TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, result.Status);
        }
        strict.VerifyGet(x => x.Connections, Times.Never);
    }

    [Fact]
    public async Task LivePreviewApprovalCommitAndExternalSchemaDrift()
    {
        var source = Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(source))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MySQL Host live test requires TEST_MYSQL_CONNECTION.");
            Assert.Skip("Requires real MySQL 8.4.");
        }
        var token = TestContext.Current.CancellationToken;
        await using var admin = new MySqlConnection(source);
        await admin.OpenAsync(token);
        var dbName = "hsql_g5_" + Guid.NewGuid().ToString("N")[..16];
        await ExecuteAsync(admin, "CREATE DATABASE " + dbName, token);
        try
        {
            var cs = new MySqlConnectionStringBuilder(source)
            {
                Database = dbName,
                Pooling = false
            }.ConnectionString;
            var actor = new DdlActorContext("mcp-key:11", "db-management:23", dbName);
            var provider = Provider();
            var runtime = Runtime(dbName);
            var table = "G5_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var drift = "G5_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            var ddl = "CREATE TABLE " + table + "(ID INT NOT NULL)";
            var preview = await runtime.RunAsync(provider, cs, ddl, actor, true, null, token);
            Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
            Assert.False(preview.CommitConfirmed);
            Assert.Equal(0, await CountAsync(cs, table, token));

            var refused = await runtime.RunAsync(provider, cs, ddl, actor,
                false, Decision(DdlApprovalState.Rejected), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, refused.Status);
            var forged = await runtime.RunAsync(provider, cs, ddl, actor, false,
                new Approver((r, _) => ValueTask.FromResult(new DdlApprovalDecision(
                    DdlApprovalState.Approved, r.RequestId, new string('0', 64),
                    "human:independent-reviewer"))), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, forged.Status);
            Assert.Equal(0, await CountAsync(cs, table, token));
            var pending = await runtime.RunAsync(provider, cs, ddl,
                actor, false, Decision(DdlApprovalState.Pending), token);
            Assert.Equal(DdlRuntimeStatus.Pending, pending.Status);

            var committed = await runtime.RunAsync(provider, cs, ddl,
                actor, false, Decision(DdlApprovalState.Approved), token);
            Assert.Equal(DdlRuntimeStatus.Committed, committed.Status);
            Assert.True(committed.CommitConfirmed);
            Assert.Equal(1, await CountAsync(cs, table, token));
            var duplicate = await runtime.RunAsync(provider, cs, ddl,
                actor, false, Decision(DdlApprovalState.Approved), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, duplicate.Status);

            var stale = await runtime.RunAsync(provider, cs,
                "CREATE TABLE " + drift + "(ID INT NOT NULL)", actor, false,
                new Approver(async (r, ct) =>
                {
                    await using var outsider = new MySqlConnection(cs);
                    await outsider.OpenAsync(ct);
                    await ExecuteAsync(outsider, "CREATE TABLE " + drift + "(OTHER INT)", ct);
                    return new DdlApprovalDecision(DdlApprovalState.Approved,
                        r.RequestId, r.ApprovalFingerprint, "human:independent-reviewer");
                }), token);
            Assert.Equal(DdlRuntimeStatus.Rejected, stale.Status);
            Assert.Equal("ddl.schema_drift", stale.ErrorCode);
            Assert.Equal(1, await CountAsync(cs, drift, token));

            // Prove server semantics: CREATE TABLE commits a preceding INSERT.
            await using var db = new MySqlConnection(cs);
            await db.OpenAsync(token);
            await ExecuteAsync(db, "CREATE TABLE G5_LEDGER(ID INT) ENGINE=InnoDB", token);
            await ExecuteAsync(db, "START TRANSACTION", token);
            await ExecuteAsync(db, "INSERT INTO G5_LEDGER(ID) VALUES (42)", token);
            await ExecuteAsync(db, "CREATE TABLE G5_MARKER(ID INT)", token);
            await ExecuteAsync(db, "ROLLBACK", token);
            await using var check = new MySqlCommand("SELECT COUNT(*) FROM G5_LEDGER", db);
            Assert.Equal(1L, Convert.ToInt64(await check.ExecuteScalarAsync(token)));
        }
        finally
        {
            await ExecuteAsync(admin, "DROP DATABASE " + dbName, CancellationToken.None);
        }
    }

    private static async Task<int> CountAsync(string cs, string table, CancellationToken token)
    {
        await using var db = new MySqlConnection(cs);
        await db.OpenAsync(token);
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.TABLES "
            + "WHERE TABLE_SCHEMA=DATABASE() AND BINARY TABLE_NAME=BINARY @name", db);
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(token));
    }

    private static async Task ExecuteAsync(MySqlConnection db, string sql, CancellationToken token)
    {
        await using var cmd = new MySqlCommand(sql, db);
        await cmd.ExecuteNonQueryAsync(token);
    }
}
