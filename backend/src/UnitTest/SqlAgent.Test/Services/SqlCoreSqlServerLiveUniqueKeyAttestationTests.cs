using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.Provider.Abstractions;
using SqlAgent.Service.Core.Execution;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// SQL Server index uniqueness is only trusted after real sys.* catalog
/// inspection. CompileDml must remain fail-closed until this proof is
/// integrated into its validation AND execution transaction boundary.
/// </summary>
public sealed class SqlCoreSqlServerLiveUniqueKeyAttestationTests
{
    private static SqlSchemaSnapshot Snapshot(string target, string source) =>
        new(SqlAgentToolType.MsSqlServer, "sqlserver-cte-live-key-test-v1",
        [
            SqlSchemaTable.WithTypes("dbo." + target,
                [new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                 new SqlSchemaColumn("label", SqlSchemaValueKind.Text)]),
            SqlSchemaTable.WithTypes("dbo." + source,
                [new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric),
                 new SqlSchemaColumn("label", SqlSchemaValueKind.Text)])
        ]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualUnfilteredUniqueIntIndexIsPinnedAndRevocationFailsClosed(bool primaryKey)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server live unique-key attestation requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server required for real catalog metadata assurance.");
        }

        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "lkcte_" + suffix + "_s";
        var target = "lkcte_" + suffix + "_t";
        var index = "ux_lkcte_" + suffix;
        await using var db = new SqlConnection(cs);
        for (int retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }

        async Task Exec(string sql)
        {
            await using var cmd = new SqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync(token);
        }

        try
        {
            await Exec("CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30) NOT NULL)");
            await Exec("CREATE TABLE dbo.[" + source + "] (id INT NOT NULL" +
                       (primaryKey ? " PRIMARY KEY" : "") +
                       ", label VARCHAR(30) NOT NULL)");
            if (!primaryKey)
                await Exec("CREATE UNIQUE INDEX [" + index + "] ON dbo.[" + source + "] (id)");

            var snapshot = Snapshot(target, source);
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLive(
                db, snapshot, "dbo." + target, "dbo." + source, "id");
            Assert.Equal(snapshot.Sha256, proof.SchemaSha256);
            Assert.Equal("dbo." + target, proof.TargetTable);
            Assert.Equal("dbo." + source, proof.SourceTable);
            Assert.Equal("id", proof.SourceKeyColumn);
            Assert.Contains("sqlserver-live-unique-int", proof.ProofIdentity);
            Assert.False(string.IsNullOrWhiteSpace(proof.ProofIdentity));

            await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(token))
            {
                Assert.True(proof.IsCurrent(db, tx));
                await tx.CommitAsync(token);
            }
            if (primaryKey)
            {
                await Exec("ALTER TABLE dbo.[" + source + "] DROP CONSTRAINT [" +
                    await PrimaryKeyName(db, source, token) + "]");
            }
            else
            {
                await Exec("DROP INDEX [" + index + "] ON dbo.[" + source + "]");
            }

            await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(token))
            {
                Assert.False(proof.IsCurrent(db, tx));
                await tx.RollbackAsync(token);
            }

            Assert.Throws<InvalidOperationException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLive(db, snapshot,
                    "dbo." + target, "dbo." + source, "id"));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

    [Fact]
    public async Task FilteredNonUniqueAndWrongSnapshotCannotMintProof()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server live key tests require TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server required.");
        }
        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "lkcte_" + suffix + "_s";
        var target = "lkcte_" + suffix + "_t";
        await using var db = new SqlConnection(cs);
        for (int retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        async Task Exec(string sql)
        {
            await using var cmd = new SqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync(token);
        }
        try
        {
            await Exec("CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30))");
            await Exec("CREATE TABLE dbo.[" + source + "] (id INT NOT NULL, label VARCHAR(30))");
            var snapshot = Snapshot(target, source);
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLive(
                    db, snapshot, "dbo." + target, "dbo." + source, "id"));
            await Exec("CREATE UNIQUE INDEX [ux_" + suffix + "] ON dbo.[" + source + "] (id) WHERE id > 0");
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLive(
                    db, snapshot, "dbo." + target, "dbo." + source, "id"));
            Assert.Throws<ArgumentException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLive(
                    db, snapshot, "dbo." + target, "dbo." + source, "id;DROP TABLE x"));
            await Exec("DROP INDEX [ux_" + suffix + "] ON dbo.[" + source + "]");
            await Exec("CREATE UNIQUE INDEX [ux_" + suffix + "] ON dbo.[" + source + "] (id)");
            Assert.Throws<ArgumentException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLive(
                    db, new SqlSchemaSnapshot(SqlAgentToolType.Postgres, "x", snapshot.Tables),
                    "dbo." + target, "dbo." + source, "id"));
            Assert.Throws<ArgumentException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLive(
                    db, snapshot, "dbo." + source, "dbo." + source, "id"));
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLive(
                db, snapshot, "dbo." + target, "dbo." + source, "id");
            await using var other = new SqlConnection(cs);
            await other.OpenAsync(token);
            await using var tx = (SqlTransaction)await db.BeginTransactionAsync(token);
            Assert.False(proof.IsCurrent(other, tx));
            await tx.RollbackAsync(token);
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SqlServerLiveCteKeyProof_BindsApprovalAndChecksActualCommitTransaction(
        bool atomic, bool revokeIndex)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server live CTE commit gate requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server required for live CTE approval gate.");
        }

        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "apcte_" + suffix + "_s";
        var target = "apcte_" + suffix + "_t";
        var index = "ux_apcte_" + suffix;
        await using var db = new SqlConnection(cs);
        for (int retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }
        async Task Exec(string sql)
        {
            await using var cmd = new SqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync(token);
        }

        try
        {
            await Exec("CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30) NOT NULL)");
            await Exec("CREATE TABLE dbo.[" + source + "] (id INT NOT NULL, label VARCHAR(30))");
            await Exec("CREATE UNIQUE INDEX [" + index + "] ON dbo.[" + source + "] (id)");
            await Exec("INSERT INTO dbo.[" + target + "] (id, label) VALUES (1, 'original')");
            await Exec("INSERT INTO dbo.[" + source + "] (id, label) VALUES (1, 'joined')");

            var proof = SqlServerCteUniqueKeyAssurance.VerifyLive(
                db, Snapshot(target, source), "dbo." + target, "dbo." + source, "id");
            const string policy = "sqlserver-cte-approval-gate-v1";
            var context = new SqlPlanValidationContext(policy);
            var mutation = SqlCoreFacade.CompileDml(
                "UPDATE dbo.[" + target + "] SET label = 'changed' WHERE id = 1",
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer, context);
            var match = SqlCoreFacade.CompileQuery(
                "SELECT id FROM dbo.[" + target + "] WHERE id = 1",
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                context, new SqlExecutionPlanPolicy());
            var plan = new ValidatedDmlPlan(
                DmlOperation.Update, "dbo." + target,
                mutation, match, ImmutableArray.Create("id"),
                DmlRowIdentityAssurance.Strict,
                DmlFingerprintService.ComputePlanFingerprint(mutation, policy),
                policy, TimeSpan.FromMinutes(5), MaxAffectedRows: 5)
            {
                SqlServerCteUniqueKeyAssurance = proof
            };

            Assert.NotEqual(DmlApprovalPlanBinding.Compute(plan),
                DmlApprovalPlanBinding.Compute(plan with { SqlServerCteUniqueKeyAssurance = null }));
            Assert.Throws<InvalidOperationException>(() =>
                DmlCoordinator.ValidatePlan(plan with { TableName = "dbo.wrong_target" }));

            var factory = new LiveSqlServerConnectionFactory();
            var coordinator = new DmlCoordinator(factory);
            var identity = db.ServerVersion.Trim();
            var preview = await coordinator.PreviewAsync(
                cs, plan, "sqlserver-cte-gate", token, identity);
            Assert.Equal(1, preview.AffectedRows);

            // A different approved plan (even with identical compiled SQL)
            // cannot strip its minted live evidence after preview.
            if (atomic)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new DmlAtomicTransactionCoordinator(factory).CommitAsync(
                        cs, [plan with { SqlServerCteUniqueKeyAssurance = null }],
                        [preview], token, identity));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    coordinator.CommitAsync(
                        cs, plan with { SqlServerCteUniqueKeyAssurance = null },
                        preview.Challenge, "sqlserver-cte-gate", token, identity));
            }

            if (revokeIndex)
                await Exec("DROP INDEX [" + index + "] ON dbo.[" + source + "]");

            if (atomic)
            {
                var batch = new DmlAtomicTransactionCoordinator(factory);
                if (revokeIndex)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        batch.CommitAsync(cs, [plan], [preview], token, identity));
                }
                else
                {
                    var commit = await batch.CommitAsync(cs, [plan], [preview], token, identity);
                    Assert.True(commit.Committed);
                }
            }
            else
            {
                if (revokeIndex)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        coordinator.CommitAsync(cs, plan, preview.Challenge,
                            "sqlserver-cte-gate", token, identity));
                }
                else
                {
                    var commit = await coordinator.CommitAsync(cs, plan,
                        preview.Challenge, "sqlserver-cte-gate", token, identity);
                    Assert.True(commit.Committed);
                }
            }

            await using var read = new SqlCommand(
                "SELECT label FROM dbo.[" + target + "] WHERE id = 1", db);
            Assert.Equal(revokeIndex ? "original" : "changed",
                (string?)await read.ExecuteScalarAsync(token));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

    private sealed class LiveSqlServerConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new SqlConnection(connectionString);
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LiveTargetAndSourceIntKey_CompilesAndCommitsStrictCteMutation(
        bool update, bool projectedAlias)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server CTE compiler parity requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server required.");
        }

        var token = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "ctel_" + suffix + "_s";
        var target = "ctel_" + suffix + "_t";
        await using var db = new SqlConnection(cs);
        for (int retry = 0; ; retry++)
        {
            try { await db.OpenAsync(token); break; }
            catch (SqlException) when (retry < 24 && !token.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), token); }
        }

        async Task Exec(string sql)
        {
            await using var cmd = new SqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync(token);
        }
        try
        {
            await Exec("CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30))");
            await Exec("CREATE TABLE dbo.[" + source + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30))");
            await Exec("INSERT INTO dbo.[" + target + "] (id, label) VALUES (1, 'old'), (2, 'untouched')");
            await Exec("INSERT INTO dbo.[" + source + "] (id, label) VALUES (1, 'one')");

            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, Snapshot(target, source), "dbo." + target, "id", "dbo." + source, "id");
            Assert.Equal("id", proof.TargetKeyColumn);
            var legacySourceOnlyProof = SqlServerCteUniqueKeyAssurance.VerifyLive(
                db, Snapshot(target, source), "dbo." + target, "dbo." + source, "id");
            Assert.Null(legacySourceOnlyProof.TargetKeyColumn);
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            const string policy = "sqlserver-live-cte-compiler-v1";
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "dbo." + target, "dbo." + source };
            var context = new SqlPlanValidationContext(policy, allowed)
                .WithSqlServerCteUniqueKeyAssurance(proof);
            var output = projectedAlias ? "lookup_id" : "id";
            var select = projectedAlias ? "id AS lookup_id" : "id";
            var cte = "WITH picked AS (SELECT " + select + " FROM dbo.[" + source + "]) ";
            var mutation = cte + (update
                ? "UPDATE dbo.[" + target + "] SET label = 'changed' FROM picked WHERE dbo." + target + ".id = picked." + output
                : "DELETE FROM dbo.[" + target + "] FROM picked WHERE dbo." + target + ".id = picked." + output);
            var parsed = CoreSqlTextParser.ParseDml(mutation, SqlAgentToolType.MsSqlServer, profile);
            Assert.Throws<SqlCompilationException>(() =>
                SqlCoreFacade.CompileDml(
                    parsed, SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext(policy, allowed)
                        .WithSqlServerCteUniqueKeyAssurance(legacySourceOnlyProof),
                    new DmlCompilationPolicy(), profile, null));
            var withoutProof = SqlCoreFacade.TryCompileDml(
                mutation, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext(policy, allowed));
            Assert.False(withoutProof.Success);

            var compiled = SqlCoreFacade.CompileDml(
                parsed, SqlAgentToolType.MsSqlServer, context,
                new DmlCompilationPolicy(), profile, null);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("picked", compiled.Sql, StringComparison.OrdinalIgnoreCase);

            var match = SqlCoreFacade.CompileQuery(
                cte + "SELECT dbo.[" + target + "].id FROM dbo.[" + target + "] CROSS JOIN picked WHERE dbo."
                    + target + ".id = picked." + output,
                SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext(policy, allowed),
                new SqlExecutionPlanPolicy(), profile, profile);
            var fingerprintPolicy = policy + "|live-sqlserver-cte-int=" + proof.ProofIdentity;
            var plan = new ValidatedDmlPlan(
                update ? DmlOperation.Update : DmlOperation.Delete,
                "dbo." + target, compiled, match,
                ImmutableArray.Create("id"), DmlRowIdentityAssurance.Strict,
                DmlFingerprintService.ComputePlanFingerprint(compiled, fingerprintPolicy),
                policy, TimeSpan.FromMinutes(5), MaxAffectedRows: 5)
            {
                SqlServerCteUniqueKeyAssurance = proof
            };
            var coordinator = new DmlCoordinator(new LiveSqlServerConnectionFactory());
            var version = db.ServerVersion.Trim();
            var preview = await coordinator.PreviewAsync(
                cs, plan, "sqlserver-live-cte-execution", token, version);
            Assert.Equal(1, preview.AffectedRows);
            var committed = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, "sqlserver-live-cte-execution", token, version);
            Assert.True(committed.Committed);
            await using var verify = new SqlCommand(
                "SELECT id, label FROM dbo.[" + target + "] ORDER BY id", db);
            await using var reader = await verify.ExecuteReaderAsync(token);
            if (update)
            {
                Assert.True(await reader.ReadAsync(token));
                Assert.Equal(1, reader.GetInt32(0));
                Assert.Equal("changed", reader.GetString(1));
            }
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal("untouched", reader.GetString(1));
            Assert.False(await reader.ReadAsync(token));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT id + 1 AS id FROM {source}) UPDATE {target} SET label = 'changed' FROM picked WHERE {target}.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM {source}) UPDATE {target} SET label = 'changed' FROM picked WHERE {target}.id = 1")]
    [InlineData("WITH picked AS (SELECT id FROM {source}) UPDATE {target} SET label = 'changed' FROM picked WHERE {target}.id = picked.id OR {target}.id = 2")]
    [InlineData("WITH picked AS (SELECT id FROM {source}) UPDATE {target} SET label = 'changed' FROM picked WHERE {target}.id = CAST(picked.id AS BIGINT)")]
    [InlineData("WITH picked AS (SELECT label AS id FROM {source}) UPDATE {target} SET label = 'changed' FROM picked WHERE {target}.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM {target}) UPDATE {target} SET label = 'changed' FROM picked WHERE {target}.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM {source} UNION ALL SELECT id FROM {source}) DELETE FROM {target} FROM picked WHERE {target}.id = picked.id")]
    public async Task LiveKeyEvidenceCannotAuthorizeUnprovenCteGraph(string template)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server required.");
            Assert.Skip("SQL Server required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "negcte_" + suffix + "_s";
        var target = "negcte_" + suffix + "_t";
        await using var db = new SqlConnection(cs);
        await db.OpenAsync(ct);
        try
        {
            await using (var setup = new SqlCommand(
                "CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30));"
                + "CREATE TABLE dbo.[" + source + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30));", db))
                await setup.ExecuteNonQueryAsync(ct);
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, Snapshot(target, source), "dbo." + target, "id", "dbo." + source, "id");
            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var sql = template.Replace("{source}", "dbo.[" + source + "]")
                .Replace("{target}", "dbo.[" + target + "]");
            var context = new SqlPlanValidationContext("sqlserver-live-cte-negative",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "dbo." + target, "dbo." + source })
                .WithSqlServerCteUniqueKeyAssurance(proof);
            // Use the real, explicit SQL Server source AND target profile.
            // Otherwise every negative would trivially fail on the missing
            // version profile, not on its unsafe CTE lineage.
            var error = Record.Exception(() =>
            {
                var parsed = CoreSqlTextParser.ParseDml(
                    sql, SqlAgentToolType.MsSqlServer, profile);
                SqlCoreFacade.CompileDml(
                    parsed, SqlAgentToolType.MsSqlServer, context,
                    new DmlCompilationPolicy(), profile, null);
            });
            Assert.NotNull(error);
            Assert.True(error is SqlCompilationException or SqlParseException or UnauthorizedAccessException,
                $"Unexpected failure category: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            await using var cleanup = new SqlCommand(
                "DROP TABLE IF EXISTS dbo.[" + source + "]; DROP TABLE IF EXISTS dbo.[" + target + "]", db);
            await cleanup.ExecuteNonQueryAsync(ct);
        }
    }

    [Fact]
    public async Task LiveJoinedProofRejectsNonIntTargetComparisonColumn()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server required.");
            Assert.Skip("SQL Server required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "flcte_" + suffix + "_s";
        var target = "flcte_" + suffix + "_t";
        await using var db = new SqlConnection(cs);
        await db.OpenAsync(ct);
        try
        {
            await using (var setup = new SqlCommand(
                "CREATE TABLE dbo.[" + target + "] (id REAL NOT NULL, label VARCHAR(30));"
                + "CREATE TABLE dbo.[" + source + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30));", db))
                await setup.ExecuteNonQueryAsync(ct);
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    db, Snapshot(target, source), "dbo." + target, "id", "dbo." + source, "id"));
        }
        finally
        {
            await using var cleanup = new SqlCommand(
                "DROP TABLE IF EXISTS dbo.[" + source + "]; DROP TABLE IF EXISTS dbo.[" + target + "]", db);
            await cleanup.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<string> PrimaryKeyName(SqlConnection db, string table, CancellationToken token)
    {
        await using var cmd = new SqlCommand(
            "SELECT kc.name FROM sys.key_constraints kc " +
            "WHERE kc.parent_object_id = OBJECT_ID(@name) AND kc.type = 'PK'", db);
        cmd.Parameters.AddWithValue("@name", "dbo." + table);
        return (string)(await cmd.ExecuteScalarAsync(token) ??
            throw new InvalidOperationException("Missing test PK."));
    }
}
