using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.Provider.Abstractions;
using SqlAgent.Service.Core.Execution;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using Moq;
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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ActualUnfilteredUniqueIntIndexIsPinnedAndRevocationFailsClosed(
        bool primaryKey, bool bigInt)
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
            var type = bigInt ? "BIGINT" : "INT";
            await Exec("CREATE TABLE dbo.[" + target + "] (id " + type
                + " NOT NULL PRIMARY KEY, label VARCHAR(30) NOT NULL)");
            await Exec("CREATE TABLE dbo.[" + source + "] (id " + type + " NOT NULL" +
                       (primaryKey ? " PRIMARY KEY" : "") +
                       ", label VARCHAR(30) NOT NULL)");
            if (!primaryKey)
                await Exec("CREATE UNIQUE INDEX [" + index + "] ON dbo.[" + source + "] (id)");

            await Exec("INSERT INTO dbo.[" + source + "] (id,label) VALUES ("
                + (bigInt ? "2147483649" : "1") + ",'source')");
            var snapshot = Snapshot(target, source);
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLive(
                db, snapshot, "dbo." + target, "dbo." + source, "id");
            Assert.Equal(snapshot.Sha256, proof.SchemaSha256);
            Assert.Equal("dbo." + target, proof.TargetTable);
            Assert.Equal("dbo." + source, proof.SourceTable);
            Assert.Equal("id", proof.SourceKeyColumn);
            Assert.Contains("sqlserver-live-exact-key-v7", proof.ProofIdentity);
            Assert.Contains("source-key-types=" + type, proof.ProofIdentity);
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
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task LiveTargetAndSourceIntKey_CompilesAndCommitsStrictCteMutation(
        bool update, bool projectedAlias, bool bigInt)
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
            var sqlType = bigInt ? "BIGINT" : "INT";
            var firstKey = bigInt ? 2147483649L : 1L;
            var otherKey = firstKey + 1;
            await Exec("CREATE TABLE dbo.[" + target + "] (id " + sqlType
                + " NOT NULL PRIMARY KEY, label VARCHAR(30))");
            await Exec("CREATE TABLE dbo.[" + source + "] (id " + sqlType
                + " NOT NULL PRIMARY KEY, label VARCHAR(30))");
            await Exec("INSERT INTO dbo.[" + target +
                $"] (id, label) VALUES ({firstKey}, 'old'), ({otherKey}, 'untouched')");
            await Exec("INSERT INTO dbo.[" + source +
                $"] (id, label) VALUES ({firstKey}, 'one')");

            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, Snapshot(target, source), "dbo." + target, "id", "dbo." + source, "id");
            Assert.Equal("id", proof.TargetKeyColumn);
            Assert.Contains("source-key-types=" + sqlType, proof.ProofIdentity);
            Assert.Contains("target-key-types=" + sqlType, proof.ProofIdentity);
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

            // Use the production CLR Host plan factory, not a test-created
            // ValidatedDmlPlan: physical target resolution, CTE lexical scope,
            // match query, proof propagation and compiler fingerprint must
            // all be preserved across the actual assembly boundary.
            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("metadata-test",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("metadata-test", "dbo",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([target]);
            metadata.Setup(x => x.GetColumnsAsync("metadata-test", "dbo", target,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new DatabaseColumnMetadata("dbo", target, "id", sqlType, true, 1),
                    new DatabaseColumnMetadata("dbo", target, "label", "varchar", false)
                ]);
            var plan = await new DmlPlanFactory(metadata.Object).CreateAsync(
                "metadata-test", parsed, SqlAgentToolType.MsSqlServer,
                context, new DmlCompilationPolicy(),
                DmlRowIdentityAssurance.Strict, maxAffectedRows: 5,
                targetProfile: profile, cancellationToken: token);
            Assert.Same(proof, plan.SqlServerCteUniqueKeyAssurance);
            Assert.NotNull(plan.MatchQueryCommand);
            Assert.StartsWith("WITH ", plan.MatchQueryCommand.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("WITH ", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(compiled.Sql, plan.MutationCommand.Sql);
            DmlCoordinator.ValidatePlan(plan);
            Assert.Throws<InvalidOperationException>(() =>
                DmlCoordinator.ValidatePlan(
                    plan with { SqlServerCteUniqueKeyAssurance = legacySourceOnlyProof }));

            // The production Host receives an open metadata connection,
            // but no caller-created live proof. It must discover, mint and
            // bind the exact physical integer key proof from the parsed CTE.
            var autoPlan = await new DmlPlanFactory(metadata.Object)
                .CreateWithMetadataConnectionAsync(
                    db, "metadata-test", parsed, SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext(policy, allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 5, cancellationToken: token,
                    targetProfile: profile);
            var automaticallyMinted = Assert.IsType<SqlServerCteUniqueKeyAssurance>(
                autoPlan.SqlServerCteUniqueKeyAssurance);
            Assert.Equal("dbo." + target, automaticallyMinted.TargetTable);
            Assert.Equal("dbo." + source, automaticallyMinted.SourceTable);
            Assert.Equal("id", automaticallyMinted.TargetKeyColumn);
            Assert.Equal("id", automaticallyMinted.SourceKeyColumn);
            Assert.Equal(plan.MutationCommand.Sql, autoPlan.MutationCommand.Sql);
            DmlCoordinator.ValidatePlan(autoPlan);

            // No preopened physical metadata connection: no live proof.
            await Assert.ThrowsAsync<SqlCompilationException>(() =>
                new DmlPlanFactory(metadata.Object).CreateAsync(
                    "metadata-test", parsed, SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext(policy, allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 5, cancellationToken: token,
                    targetProfile: profile));
            // No positive row cap: automatic evidence may not authorize an
            // unbounded Host write, even if the index itself is unique.
            await Assert.ThrowsAsync<SqlCompilationException>(() =>
                new DmlPlanFactory(metadata.Object).CreateWithMetadataConnectionAsync(
                    db, "metadata-test", parsed, SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext(policy, allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 0, cancellationToken: token,
                    targetProfile: profile));
            plan = autoPlan;

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
                Assert.Equal(firstKey, bigInt ? reader.GetInt64(0) : reader.GetInt32(0));
                Assert.Equal("changed", reader.GetString(1));
            }
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(otherKey, bigInt ? reader.GetInt64(0) : reader.GetInt32(0));
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

            // Even with an open physical metadata connection, the Host
            // must not turn an unverified CTE graph into a write authority.
            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("metadata-test",
                    It.IsAny<CancellationToken>())).ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("metadata-test", "dbo",
                    It.IsAny<CancellationToken>())).ReturnsAsync([target]);
            metadata.Setup(x => x.GetColumnsAsync("metadata-test", "dbo", target,
                    It.IsAny<CancellationToken>())).ReturnsAsync([
                    new DatabaseColumnMetadata("dbo", target, "id", "int", true, 1),
                    new DatabaseColumnMetadata("dbo", target, "label", "varchar", false)
                ]);
            var autoError = await Record.ExceptionAsync(async () =>
            {
                var parsedForAuto = CoreSqlTextParser.ParseDml(
                    sql, SqlAgentToolType.MsSqlServer, profile);
                await new DmlPlanFactory(metadata.Object)
                    .CreateWithMetadataConnectionAsync(
                        db, "metadata-test", parsedForAuto, SqlAgentToolType.MsSqlServer,
                        new SqlPlanValidationContext("sqlserver-auto-negative",
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { "dbo." + target, "dbo." + source }),
                        new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                        maxAffectedRows: 5, cancellationToken: ct,
                        targetProfile: profile);
            });
            Assert.NotNull(autoError);
            Assert.True(autoError is SqlCompilationException or SqlParseException
                or InvalidOperationException or UnauthorizedAccessException,
                $"Unexpected Host rejection category: {autoError.GetType().Name}: {autoError.Message}");
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


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveProof_BindsServerDatabaseGuid_AndPinsDdlEvenForEmptySource(bool emptySource)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server CTE physical DB fence requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "dbf_" + suffix + "_s";
        var target = "dbf_" + suffix + "_t";
        var index = "ux_dbf_" + suffix;
        await using var db = new SqlConnection(cs);
        await db.OpenAsync(ct);
        await using var ddl = new SqlConnection(cs);
        await ddl.OpenAsync(ct);

        async Task Execute(SqlConnection connection, string sql)
        {
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await Execute(db, "CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30))");
            await Execute(db, "CREATE TABLE dbo.[" + source + "] (id INT NOT NULL, label VARCHAR(30))");
            await Execute(db, "CREATE UNIQUE INDEX [" + index + "] ON dbo.[" + source + "](id)");
            if (!emptySource)
                await Execute(db, "INSERT INTO dbo.[" + source + "] (id, label) VALUES (1, 'one')");
            var snapshot = Snapshot(target, source);
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, snapshot, "dbo." + target, "id", "dbo." + source, "id");

            await using (var identityCommand = new SqlCommand(
                "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')), "
                + "DB_NAME(), database_guid FROM sys.database_recovery_status WHERE database_id = DB_ID()", db))
            await using (var identityReader = await identityCommand.ExecuteReaderAsync(ct))
            {
                Assert.True(await identityReader.ReadAsync(ct));
                Assert.Equal(identityReader.GetString(0), proof.ServerInstanceName);
                Assert.Equal(identityReader.GetString(1), proof.DatabaseName);
                Assert.Equal(identityReader.GetGuid(2), proof.DatabaseGuid);
                Assert.Contains(proof.DatabaseGuid.ToString("D"),
                    proof.ProofIdentity, StringComparison.OrdinalIgnoreCase);
                Assert.False(await identityReader.ReadAsync(ct));
            }

            await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(ct))
            {
                Assert.True(proof.IsCurrent(db, tx));
                // DDL must be blocked *even for an empty source table*. A
                // TOP(1) read must not accidentally grant a metadata fence
                // without holding a physical lock through the transaction.
                await using var drop = new SqlCommand(
                    "SET LOCK_TIMEOUT 1000; DROP INDEX [" + index + "] ON dbo.[" + source + "]", ddl)
                {
                    CommandTimeout = 5
                };
                var blocked = await Assert.ThrowsAsync<SqlException>(
                    () => drop.ExecuteNonQueryAsync(ct));
                Assert.Equal(1222, blocked.Number);
                await tx.RollbackAsync(ct);
            }
            await Execute(ddl, "DROP INDEX [" + index + "] ON dbo.[" + source + "]");
            await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(ct))
            {
                Assert.False(proof.IsCurrent(db, tx));
                await tx.RollbackAsync(ct);
            }
        }
        finally
        {
            await Execute(db, "DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Execute(db, "DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

    [Fact]
    public async Task LiveProof_RejectsSameTableAndIndexLayoutInDifferentPhysicalDatabase()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server physical database parity requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server required.");
        }

        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var extraDatabase = "cteproof_" + suffix;
        var source = "xdb_" + suffix + "_s";
        var target = "xdb_" + suffix + "_t";
        await using var original = new SqlConnection(cs);
        await original.OpenAsync(ct);

        static async Task Run(SqlConnection db, string sql, CancellationToken token)
        {
            await using var command = new SqlCommand(sql, db);
            await command.ExecuteNonQueryAsync(token);
        }

        await Run(original, "CREATE DATABASE [" + extraDatabase + "]", ct);
        try
        {
            var createTables =
                "CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30));"
                + "CREATE TABLE dbo.[" + source + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30))";
            await Run(original, createTables, ct);
            var first = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                original, Snapshot(target, source), "dbo." + target, "id",
                "dbo." + source, "id");

            var otherCs = new SqlConnectionStringBuilder(cs)
            {
                InitialCatalog = extraDatabase
            }.ConnectionString;
            await using (var other = new SqlConnection(otherCs))
            {
                await other.OpenAsync(ct);
                await Run(other, createTables, ct);
                var second = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    other, Snapshot(target, source), "dbo." + target, "id",
                    "dbo." + source, "id");

                Assert.Equal(first.ServerInstanceName, second.ServerInstanceName);
                Assert.NotEqual(first.DatabaseName, second.DatabaseName);
                Assert.NotEqual(first.DatabaseGuid, second.DatabaseGuid);
                Assert.NotEqual(first.ProofIdentity, second.ProofIdentity);

                await using var tx = (SqlTransaction)await other.BeginTransactionAsync(ct);
                Assert.True(second.IsCurrent(other, tx));
                Assert.False(first.IsCurrent(other, tx));
                await tx.RollbackAsync(ct);
            }
        }
        finally
        {
            await Run(original, "DROP TABLE IF EXISTS dbo.[" + source + "];"
                + "DROP TABLE IF EXISTS dbo.[" + target + "]", ct);
            await Run(original, "ALTER DATABASE [" + extraDatabase
                + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;"
                + "DROP DATABASE [" + extraDatabase + "]", ct);
        }
    }


    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, false, 2)]
    [InlineData(true, true, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 3)]
    [InlineData(false, false, 3)]
    [InlineData(true, true, 3)]
    [InlineData(false, true, 3)]
    public async Task MultiCteDirectIntKeyLineage_AutoProofAndApprovedLiveMutation(
        bool update, bool cteColumnAliases, int depth)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server chained key-lineage parity requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server required.");
        }

        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "chain_" + suffix + "_s";
        var target = "chain_" + suffix + "_t";
        await using var db = new SqlConnection(cs);
        await db.OpenAsync(ct);
        async Task Exec(string sql)
        {
            await using var command = new SqlCommand(sql, db);
            await command.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await Exec("CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30));"
                + "CREATE TABLE dbo.[" + source + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30));"
                + "INSERT INTO dbo.[" + target + "] (id, label) VALUES (1, 'old'), (2, 'untouched');"
                + "INSERT INTO dbo.[" + source + "] (id, label) VALUES (1, 'one');");

            var root = cteColumnAliases
                ? "picked(root_id) AS (SELECT id FROM dbo.[" + source + "])"
                : "picked AS (SELECT id AS root_id FROM dbo.[" + source + "])";
            var middle = cteColumnAliases
                ? "forwarded(lookup_id) AS (SELECT root_id FROM picked)"
                : "forwarded AS (SELECT root_id AS lookup_id FROM picked)";
            var tail = cteColumnAliases
                ? "finished(final_id) AS (SELECT lookup_id FROM forwarded)"
                : "finished AS (SELECT lookup_id AS final_id FROM forwarded)";
            var cteName = depth == 2 ? "forwarded" : "finished";
            var finalKey = depth == 2 ? "lookup_id" : "final_id";
            var withCte = "WITH " + root + ", " + middle
                + (depth == 3 ? ", " + tail : "") + " ";
            var sql = withCte + (update
                ? "UPDATE dbo.[" + target + "] SET label = 'changed' FROM " + cteName
                  + " WHERE dbo." + target + ".id = " + cteName + "." + finalKey
                : "DELETE FROM dbo.[" + target + "] FROM " + cteName
                  + " WHERE dbo." + target + ".id = " + cteName + "." + finalKey);
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var parsed = CoreSqlTextParser.ParseDml(
                sql, SqlAgentToolType.MsSqlServer, profile);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "dbo." + target, "dbo." + source };
            var policy = "sqlserver-chained-cte-v1";
            // No live source/target proof: the closed F# compiler must
            // reject the same complete chain under an identical profile.
            Assert.Throws<SqlCompilationException>(() =>
                SqlCoreFacade.CompileDml(
                    parsed, SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext(policy, allowed),
                    new DmlCompilationPolicy(), profile, null));
            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("metadata-test",
                    It.IsAny<CancellationToken>())).ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("metadata-test", "dbo",
                    It.IsAny<CancellationToken>())).ReturnsAsync([target]);
            metadata.Setup(x => x.GetColumnsAsync("metadata-test", "dbo", target,
                    It.IsAny<CancellationToken>())).ReturnsAsync([
                    new DatabaseColumnMetadata("dbo", target, "id", "int", true, 1),
                    new DatabaseColumnMetadata("dbo", target, "label", "varchar", false)
                ]);

            // RED: no fake caller proof; the Host must derive the full
            // renamed key chain before minting live uniqueness evidence.
            var plan = await new DmlPlanFactory(metadata.Object)
                .CreateWithMetadataConnectionAsync(
                    db, "metadata-test", parsed, SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext(policy, allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 5, cancellationToken: ct,
                    targetProfile: profile);
            var proof = Assert.IsType<SqlServerCteUniqueKeyAssurance>(
                plan.SqlServerCteUniqueKeyAssurance);
            Assert.Equal("dbo." + source, proof.SourceTable);
            Assert.Equal("dbo." + target, proof.TargetTable);
            Assert.Equal("id", proof.SourceKeyColumn);
            Assert.Equal("id", proof.TargetKeyColumn);
            Assert.StartsWith("WITH ", plan.MutationCommand.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("WITH ", plan.MatchQueryCommand!.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(cteName, plan.MatchQueryCommand.Sql, StringComparison.OrdinalIgnoreCase);
            DmlCoordinator.ValidatePlan(plan);

            var coordinator = new DmlCoordinator(new LiveSqlServerConnectionFactory());
            var version = db.ServerVersion.Trim();
            var preview = await coordinator.PreviewAsync(
                cs, plan, "sqlserver-chained-cte", ct, version);
            Assert.Equal(1, preview.AffectedRows);
            var result = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, "sqlserver-chained-cte", ct, version);
            Assert.True(result.Committed);
            await using var read = new SqlCommand(
                "SELECT id, label FROM dbo.[" + target + "] ORDER BY id", db);
            await using var rows = await read.ExecuteReaderAsync(ct);
            if (update)
            {
                Assert.True(await rows.ReadAsync(ct));
                Assert.Equal(1, rows.GetInt32(0));
                Assert.Equal("changed", rows.GetString(1));
            }
            Assert.True(await rows.ReadAsync(ct));
            Assert.Equal(2, rows.GetInt32(0));
            Assert.Equal("untouched", rows.GetString(1));
            Assert.False(await rows.ReadAsync(ct));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "];"
                + "DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

    [Theory]
    [InlineData("WITH picked AS (SELECT id FROM {source}), forwarded AS (SELECT id+1 AS key_id FROM picked) UPDATE {target} SET label = 'x' FROM forwarded WHERE {target}.id = forwarded.key_id")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), forwarded AS (SELECT id AS key_id FROM picked UNION ALL SELECT id FROM picked) UPDATE {target} SET label = 'x' FROM forwarded WHERE {target}.id = forwarded.key_id")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), forwarded AS (SELECT a.id AS key_id FROM picked a JOIN picked b ON a.id = b.id) UPDATE {target} SET label = 'x' FROM forwarded WHERE {target}.id = forwarded.key_id")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), forwarded AS (SELECT id AS key_id FROM picked) UPDATE {target} SET label = 'x' FROM forwarded WHERE {target}.id = forwarded.key_id OR {target}.id = 1")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), forwarded AS (SELECT id AS key_id FROM picked) UPDATE {target} SET label = 'x' FROM forwarded WHERE {target}.id = CAST(forwarded.key_id AS INT)")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), forwarded AS (SELECT id AS key_id FROM picked) UPDATE {target} SET label = 'x' FROM picked WHERE {target}.id = forwarded.key_id")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), hop2 AS (SELECT id FROM picked), hop3 AS (SELECT id FROM hop2), hop4 AS (SELECT id FROM hop3) UPDATE {target} SET label = 'x' FROM hop4 WHERE {target}.id = hop4.id")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), discarded AS (SELECT id FROM picked WHERE id > 0), hop3 AS (SELECT id FROM picked) UPDATE {target} SET label = 'x' FROM hop3 WHERE {target}.id = hop3.id")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), picked AS (SELECT id FROM picked) UPDATE {target} SET label = 'x' FROM picked WHERE {target}.id = picked.id")]
    [InlineData("WITH picked AS (SELECT id FROM {source}), forwarded AS (SELECT id AS key_id FROM picked) UPDATE {target} SET label = 'x' FROM forwarded WHERE {target}.id = picked.id")]
    public async Task ChainedCteUnsafeLineageCannotMintLiveWriteAuthority(string template)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server chained negative cases require TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("SQL Server required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var source = "chnneg_" + suffix + "_s";
        var target = "chnneg_" + suffix + "_t";
        await using var db = new SqlConnection(cs);
        await db.OpenAsync(ct);
        try
        {
            await using (var setup = new SqlCommand(
                "CREATE TABLE dbo.[" + target + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30));"
                + "CREATE TABLE dbo.[" + source + "] (id INT NOT NULL PRIMARY KEY, label VARCHAR(30));", db))
                await setup.ExecuteNonQueryAsync(ct);
            var sql = template.Replace("{source}", "dbo.[" + source + "]")
                .Replace("{target}", "dbo.[" + target + "]");
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("metadata-test",
                    It.IsAny<CancellationToken>())).ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("metadata-test", "dbo",
                    It.IsAny<CancellationToken>())).ReturnsAsync([target]);
            metadata.Setup(x => x.GetColumnsAsync("metadata-test", "dbo", target,
                    It.IsAny<CancellationToken>())).ReturnsAsync([
                    new DatabaseColumnMetadata("dbo", target, "id", "int", true, 1),
                    new DatabaseColumnMetadata("dbo", target, "label", "varchar", false)
                ]);
            var error = await Record.ExceptionAsync(async () =>
            {
                var parsed = CoreSqlTextParser.ParseDml(
                    sql, SqlAgentToolType.MsSqlServer, profile);
                await new DmlPlanFactory(metadata.Object)
                    .CreateWithMetadataConnectionAsync(
                        db, "metadata-test", parsed, SqlAgentToolType.MsSqlServer,
                        new SqlPlanValidationContext("sqlserver-chained-negative",
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                { "dbo." + target, "dbo." + source }),
                        new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                        maxAffectedRows: 5, cancellationToken: ct,
                        targetProfile: profile);
            });
            Assert.NotNull(error);
            Assert.True(error is SqlCompilationException or SqlParseException
                or InvalidOperationException or UnauthorizedAccessException,
                $"Unexpected chained rejection: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            await using var cleanup = new SqlCommand(
                "DROP TABLE IF EXISTS dbo.[" + source + "];"
                + "DROP TABLE IF EXISTS dbo.[" + target + "]", db);
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
