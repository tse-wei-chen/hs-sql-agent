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
            Assert.True(preview.Status == DdlRuntimeStatus.Preview,
                "Expected Preview, got " + preview.Status + " (" + preview.ErrorCode + "): " + preview.Message
                + " | DIAG: " + await ReadOnlyOracleProofDiagnostics(db, ct));
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
            Assert.True(committed.Status == DdlRuntimeStatus.Committed,
                "Expected Committed, got " + committed.Status + " (" + committed.ErrorCode + "): " + committed.Message);
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

            // Oracle implicitly commits prior DML only on the SAME session.
            // A Host DDL on a separate connection must never be mistaken for
            // having committed an unrelated caller's DML transaction.
            var implicitName = "G5O_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            var native = "G5O_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            var hostCreated = "G5O_" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
            try
            {
                await Exec(db, "CREATE TABLE " + implicitName + "(ID NUMBER)", ct);
                await Exec(db, "INSERT INTO " + implicitName + " VALUES (7)", ct);
                // This direct DDL is the engine differential oracle, NOT a Host
                // executable fallback. It runs on db, the same INSERT session.
                await Exec(db, "CREATE TABLE " + native + "(ID NUMBER)", ct);
                await Exec(db, "ROLLBACK", ct);
                Assert.Equal(1, Convert.ToInt32(await Scalar(db,
                    "SELECT COUNT(*) FROM " + implicitName + " WHERE ID=7", ct)));

                var host = await runtime.RunAsync(provider, cs,
                    "CREATE TABLE " + hostCreated + "(ID INTEGER)", actor, false,
                    Decision(DdlApprovalState.Approved), ct);
                Assert.Equal(DdlRuntimeStatus.Committed, host.Status);
                // Oracle Host executes using its own fresh connection.
                await Exec(db, "ROLLBACK", ct);
                Assert.Equal(1, Convert.ToInt32(await Scalar(db,
                    "SELECT COUNT(*) FROM USER_OBJECTS WHERE OBJECT_NAME = '" + hostCreated + "'", ct)));
            }
            finally
            {
                foreach (var nameToDrop in new[] { hostCreated, native, implicitName })
                    if (Convert.ToInt32(await Scalar(db,
                        "SELECT COUNT(*) FROM USER_OBJECTS WHERE OBJECT_NAME = '" + nameToDrop + "'",
                        CancellationToken.None)) > 0)
                        await Exec(db, "DROP TABLE " + nameToDrop + " PURGE", CancellationToken.None);
            }
        }
        finally
        {
            foreach (var table in new[] { name, drift })
                if (Convert.ToInt32(await Scalar(db,
                    "SELECT COUNT(*) FROM USER_OBJECTS WHERE OBJECT_NAME = '" + table + "'", CancellationToken.None)) > 0)
                    await Exec(db, "DROP TABLE " + table + " PURGE", CancellationToken.None);
        }
    }

    // Test-only, read-only probes: report stage and Oracle error codes, never DBID,
    // GUID, server address, principal, SQL source or credentials to CI logs.
    private static async Task<string> ReadOnlyOracleProofDiagnostics(
        OracleConnection db, CancellationToken ct)
    {
        var probes = new (string Name, string Sql)[]
        {
            ("context", "SELECT SYS_CONTEXT('USERENV','DB_NAME'), " +
                "SYS_CONTEXT('USERENV','DB_UNIQUE_NAME'), " +
                "SYS_CONTEXT('USERENV','CON_NAME'), " +
                "SYS_CONTEXT('USERENV','SESSION_USER'), " +
                "SYS_CONTEXT('USERENV','CURRENT_SCHEMA'), " +
                "SYS_CONTEXT('USERENV','DATABASE_ROLE'), " +
                "SYS_CONTEXT('USERENV','SERVICE_NAME') FROM DUAL"),
            ("version", "SELECT VERSION_FULL FROM PRODUCT_COMPONENT_VERSION " +
                "WHERE (PRODUCT LIKE 'Oracle Database%' " +
                "OR PRODUCT LIKE 'Oracle AI Database%') AND VERSION_FULL IS NOT NULL"),
            ("database", "SELECT TO_CHAR(DBID) FROM V$DATABASE"),
            ("pdb", "SELECT TO_CHAR(DBID), RAWTOHEX(GUID), OPEN_MODE FROM V$PDBS " +
                "WHERE CON_ID=TO_NUMBER(SYS_CONTEXT('USERENV','CON_ID'))"),
            ("objects", "SELECT COUNT(*) FROM USER_OBJECTS WHERE OBJECT_NAME = 'G5_PROBE_NONEXISTENT'"),
            ("triggers", "SELECT COUNT(*) FROM DBA_TRIGGERS WHERE STATUS = 'ENABLED' " +
                "AND (BASE_OBJECT_TYPE IN ('DATABASE','PLUGGABLE DATABASE') " +
                "OR (BASE_OBJECT_TYPE = 'SCHEMA' AND OWNER = 'SQLCORE'))")
        };
        var results = new List<string>();
        foreach (var (name, sql) in probes)
        {
            try
            {
                await using var command = new OracleCommand(sql, db);
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    results.Add(name + ":empty");
                    continue;
                }
                var markers = Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? "null" : "value");
                var stage = name switch
                {
                    "context" => "role-primary=" + (reader.GetValue(5)?.ToString() == "PRIMARY")
                        + ";schema-owner=" + (reader.GetValue(3)?.ToString() == reader.GetValue(4)?.ToString())
                        + ";pdb-user=" + (reader.GetValue(2)?.ToString() == "FREEPDB1"),
                    "version" => "accepted=" +
                        OracleDdlRuntime.IsProvenOracle26Ai(reader.GetValue(0)?.ToString()),
                    "pdb" => "guid32=" + (reader.GetValue(1)?.ToString()?.Trim().Length == 32)
                        + ";read-write=" + (reader.GetValue(2)?.ToString() == "READ WRITE"),
                    "triggers" => "zero=" + (Convert.ToInt32(reader.GetValue(0)) == 0),
                    _ => ""
                };
                results.Add(name + ":ok[" + string.Join(",", markers) + "]:" + stage);
            }
            catch (OracleException ex)
            {
                results.Add(name + ":ora-" + ex.Number);
            }
            catch (Exception ex)
            {
                results.Add(name + ":" + ex.GetType().Name);
            }
        }
        // Probe the pure F# phase separately from Oracle's physical metadata.
        // Emit only the stage/error type; never SQL, DB identity, or credentials.
        try
        {
            var profile = new HsSqlAgent.SqlCore.Models.SqlProviderCapabilityProfile(
                SqlAgentToolType.Oracle, new Version(23, 26));
            var options = new HsSqlAgent.SqlCore.Ddl.DdlCompilationOptions(
                SqlAgentToolType.Oracle, SqlAgentToolType.Oracle, profile, profile);
            HsSqlAgent.SqlCore.SqlCoreFacade.CompileDdl(
                "CREATE TABLE G5_PROBE(ID INTEGER NOT NULL)", options);
            results.Add("compile:ok");
            HsSqlAgent.SqlCore.SqlCoreFacade.PreviewDdl(
                "CREATE TABLE G5_PROBE(ID INTEGER NOT NULL)", options);
            results.Add("static-preview:ok");
        }
        catch (Exception ex)
        {
            results.Add("core:" + ex.GetType().Name);
        }
        return string.Join(";", results);
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
