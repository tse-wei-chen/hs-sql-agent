using System.Data.Common;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore.Enums;
using Moq;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>G5 RED: Oracle 26ai implicit-commit CREATE TABLE, live physical safety proof.</summary>
public sealed class OracleDdlLiveTests
{
    private sealed class Connections : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new OracleConnection(connectionString);
    }

    private sealed class Approver(
        Func<DdlApprovalRequest, CancellationToken, ValueTask<DdlApprovalDecision>> respond)
        : IDdlApprovalProvider
    {
        public ValueTask<DdlApprovalDecision> RequestAsync(
            DdlApprovalRequest request, CancellationToken ct = default) => respond(request, ct);
    }

    private static ISqlProvider Provider()
    {
        var mock = new Mock<ISqlProvider>(MockBehavior.Strict);
        mock.SetupGet(x => x.Type).Returns(SqlAgentToolType.Oracle);
        mock.SetupGet(x => x.Connections).Returns(new Connections());
        return mock.Object;
    }

    private static OracleDdlRuntime Runtime() => new(new OracleDdlExecutionOptions
    {
        Enabled = true,
        AllowCreateTable = true,
        AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { "SQLCORE" },
        AllowedPdbNames = new HashSet<string>(StringComparer.Ordinal) { "FREEPDB1" },
        PolicyRevision = "oracle26ai-g5-test"
    });

    private static IDdlApprovalProvider Decision(DdlApprovalState state) =>
        new Approver((r, _) => ValueTask.FromResult(new DdlApprovalDecision(
            state, r.RequestId, r.ApprovalFingerprint, "human:independent-reviewer")));

    [Fact]
    public async Task DisabledAndUnsafeGrammarNeverOpenConnection()
    {
        var strict = new Mock<ISqlProvider>(MockBehavior.Strict);
        var actor = new DdlActorContext("mcp-key:14", "db-management:27", "SQLCORE");
        var disabled = await new OracleDdlRuntime().RunAsync(strict.Object, "unused",
            "CREATE TABLE G5_TEST(ID INTEGER)", actor, false, null,
            TestContext.Current.CancellationToken);
        Assert.Equal(DdlRuntimeStatus.Rejected, disabled.Status);
        strict.VerifyNoOtherCalls();
        strict.SetupGet(x => x.Type).Returns(SqlAgentToolType.Oracle);
        foreach (var sql in new[]
        {
            "DROP TABLE G5_TEST",
            "CREATE TABLE G5_TEST(ID INTEGER); DROP TABLE G5_TEST",
            "CREATE TABLE G5_TEST(ID INTEGER PRIMARY KEY)",
            "CREATE TABLE G5_TEST AS SELECT 1 FROM DUAL",
            "ALTER TABLE G5_TEST ADD (X INTEGER)",
            "CREATE INDEX G5_IX ON G5_TEST(ID)",
            "CREATE TABLE G5_TEST(ID INTEGER, NAME VARCHAR2(20))",
            "CREATE TABLE OTHER.G5_TEST(ID INTEGER)"
        })
        {
            var denied = await Runtime().RunAsync(strict.Object, "unused", sql,
                actor, true, null, TestContext.Current.CancellationToken);
            Assert.Equal(DdlRuntimeStatus.Rejected, denied.Status);
        }
        strict.VerifyGet(x => x.Connections, Times.Never);
    }

    [Fact]
    public async Task RealOraclePreviewApprovalImplicitCommitAndExternalSchemaDrift()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_ORACLE_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("G5 Oracle live test requires TEST_ORACLE_CONNECTION.");
            Assert.Skip("Oracle 26ai PDB required.");
        }

        var ct = TestContext.Current.CancellationToken;
        var adminCs = Environment.GetEnvironmentVariable("TEST_ORACLE_ADMIN_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminCs))
            throw new InvalidOperationException(
                "Oracle G5 acceptance requires a privileged test-only metadata grant connection.");
        // Exact dictionary visibility is mandatory: ALL_TRIGGERS cannot prove
        // that the target PDB has no DDL triggers created by other principals.
        await using (var admin = new OracleConnection(adminCs))
        {
            await admin.OpenAsync(ct);
            foreach (var grant in new[]
            {
                "GRANT SELECT ON SYS.V_$DATABASE TO SQLCORE",
                "GRANT SELECT ON SYS.V_$PDBS TO SQLCORE",
                "GRANT SELECT ON SYS.DBA_TRIGGERS TO SQLCORE",
                "GRANT EXECUTE ON SYS.DBMS_LOCK TO SQLCORE"
            })
                await Exec(admin, grant, ct);
        }
        await using var db = new OracleConnection(cs);
        await db.OpenAsync(ct);
        var schema = (await Scalar(db, "SELECT SYS_CONTEXT('USERENV','SESSION_USER') FROM DUAL", ct))?.ToString();
        Assert.Equal("SQLCORE", schema);
        var pdb = (await Scalar(db, "SELECT SYS_CONTEXT('USERENV','CON_NAME') FROM DUAL", ct))?.ToString();
        Assert.Equal("FREEPDB1", pdb);
        var actor = new DdlActorContext("mcp-key:14", "db-management:27", pdb!);
        var name = "G5O_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var drift = "G5O_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var before = "CREATE TABLE " + name + "(ID INTEGER NOT NULL)";
        var provider = Provider();
        var runtime = Runtime();
        try
        {
            var preview = await runtime.RunAsync(provider, cs, before, actor, true, null, ct);
            Assert.Equal(DdlRuntimeStatus.Preview, preview.Status);
            Assert.False(preview.CommitConfirmed);
            Assert.Equal(0, Convert.ToInt32(await Scalar(db,
                "SELECT COUNT(*) FROM USER_OBJECTS WHERE OBJECT_NAME = '" + name + "'", ct)));

            var reject = await runtime.RunAsync(provider, cs, before, actor,
                false, Decision(DdlApprovalState.Rejected), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, reject.Status);

            var forged = await runtime.RunAsync(provider, cs, before, actor,
                false, new Approver((r, _) => ValueTask.FromResult(new DdlApprovalDecision(
                    DdlApprovalState.Approved, r.RequestId, new string('0', 64),
                    "human:reviewer"))), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, forged.Status);

            var committed = await runtime.RunAsync(provider, cs, before, actor,
                false, Decision(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Committed, committed.Status);
            Assert.True(committed.CommitConfirmed);
            Assert.Equal(1, Convert.ToInt32(await Scalar(db,
                "SELECT COUNT(*) FROM USER_OBJECTS WHERE OBJECT_NAME = '" + name + "'", ct)));

            var repeated = await runtime.RunAsync(provider, cs, before, actor,
                false, Decision(DdlApprovalState.Approved), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, repeated.Status);

            // Real independent writer changes physical object during human approval.
            var stale = await runtime.RunAsync(provider, cs,
                "CREATE TABLE " + drift + "(ID INTEGER NOT NULL)", actor, false,
                new Approver(async (r, token) =>
                {
                    await using var outsider = new OracleConnection(cs);
                    await outsider.OpenAsync(token);
                    await Exec(outsider, "CREATE TABLE " + drift + "(OTHER NUMBER)", token);
                    return new DdlApprovalDecision(DdlApprovalState.Approved,
                        r.RequestId, r.ApprovalFingerprint, "human:independent-reviewer");
                }), ct);
            Assert.Equal(DdlRuntimeStatus.Rejected, stale.Status);
            Assert.Equal("ddl.schema_drift", stale.ErrorCode);

            // Oracle DDL is committed independently of any subsequent rollback.
            var implicitName = "G5O_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            try
            {
                await Exec(db, "CREATE TABLE " + implicitName + "(ID NUMBER)", ct);
                await Exec(db, "INSERT INTO " + implicitName + " VALUES (7)", ct);
                var other = "G5O_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
                try
                {
                    var created = await runtime.RunAsync(provider, cs,
                        "CREATE TABLE " + other + "(ID INTEGER)", actor, false,
                        Decision(DdlApprovalState.Approved), ct);
                    Assert.Equal(DdlRuntimeStatus.Committed, created.Status);
                    await Exec(db, "ROLLBACK", ct);
                    Assert.Equal(1, Convert.ToInt32(await Scalar(db,
                        "SELECT COUNT(*) FROM " + implicitName + " WHERE ID=7", ct)));
                }
                finally { await Exec(db, "DROP TABLE " + other + " PURGE", CancellationToken.None); }
            }
            finally { await Exec(db, "DROP TABLE " + implicitName + " PURGE", CancellationToken.None); }
        }
        finally
        {
            foreach (var table in new[] { name, drift })
                if (Convert.ToInt32(await Scalar(db,
                    "SELECT COUNT(*) FROM USER_OBJECTS WHERE OBJECT_NAME = '" + table + "'", CancellationToken.None)) > 0)
                    await Exec(db, "DROP TABLE " + table + " PURGE", CancellationToken.None);
        }
    }

    private static async Task<object?> Scalar(OracleConnection c, string sql, CancellationToken token)
    {
        await using var cmd = new OracleCommand(sql, c);
        return await cmd.ExecuteScalarAsync(token);
    }

    private static async Task Exec(OracleConnection c, string sql, CancellationToken token)
    {
        await using var cmd = new OracleCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(token);
    }
}
