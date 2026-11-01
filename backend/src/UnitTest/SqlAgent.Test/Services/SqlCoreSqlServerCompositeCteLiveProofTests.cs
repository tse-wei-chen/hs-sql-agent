using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.Provider.Abstractions;
using SqlAgent.Service.Core.Execution;
using System.Data.Common;
using Moq;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// G7/M02: physical two-column INT UNIQUE proof must bind the exact ordered
/// index, both unchanged CTE projections and both mandatory target equalities.
/// Static Catalog cardinality is intentionally never an execution authority.
/// </summary>
public sealed class SqlCoreSqlServerCompositeCteLiveProofTests
{
    [Theory]
    [InlineData(true, true, 1, false, false, "NONE", "INT", "INT")]
    [InlineData(true, false, 1, false, false, "NONE", "INT", "INT")]
    [InlineData(false, true, 1, false, false, "NONE", "INT", "INT")]
    [InlineData(false, false, 1, false, false, "NONE", "INT", "INT")]
    [InlineData(true, true, 2, false, false, "NONE", "INT", "INT")]
    [InlineData(false, true, 2, false, false, "NONE", "INT", "INT")]
    [InlineData(true, false, 3, false, false, "NONE", "INT", "INT")]
    [InlineData(false, false, 3, false, false, "NONE", "INT", "INT")]
    [InlineData(true, false, 1, true, false, "NONE", "INT", "INT")]
    [InlineData(false, false, 3, true, false, "NONE", "INT", "INT")]
    [InlineData(true, true, 1, false, true, "NONE", "INT", "INT")]
    [InlineData(false, false, 1, false, true, "NONE", "INT", "INT")]
    [InlineData(true, false, 2, false, true, "NONE", "INT", "INT")]
    [InlineData(false, true, 2, false, true, "NONE", "INT", "INT")]
    [InlineData(true, true, 3, false, true, "NONE", "INT", "INT")]
    [InlineData(false, false, 3, false, true, "NONE", "INT", "INT")]
    [InlineData(true, false, 2, true, true, "NONE", "INT", "INT")]
    [InlineData(false, false, 3, true, true, "NONE", "INT", "INT")]
    [InlineData(true, true, 1, false, true, "CROSS", "INT", "INT")]
    [InlineData(false, false, 1, false, true, "CROSS", "INT", "INT")]
    [InlineData(true, false, 2, false, true, "INNER", "INT", "INT")]
    [InlineData(false, true, 2, false, true, "INNER", "INT", "INT")]
    [InlineData(true, true, 3, false, true, "LEFT", "INT", "INT")]
    [InlineData(false, false, 3, false, true, "LEFT", "INT", "INT")]
    [InlineData(true, false, 2, true, true, "CROSS", "INT", "INT")]
    [InlineData(false, false, 3, true, true, "INNER", "INT", "INT")]
    [InlineData(true, true, 1, false, true, "SELF", "INT", "INT")]
    [InlineData(false, false, 1, false, true, "SELF", "INT", "INT")]
    [InlineData(true, false, 2, false, true, "SELF_LEFT", "INT", "INT")]
    [InlineData(false, true, 3, false, true, "SELF_LEFT", "INT", "INT")]
    [InlineData(true, false, 2, true, true, "SELF", "INT", "INT")]
    [InlineData(false, false, 3, true, true, "SELF_LEFT", "INT", "INT")]
    // G9/M04: 64-bit exact SQL Server 2022 physical key types.
    [InlineData(true, true, 1, false, false, "NONE", "BIGINT", "BIGINT")]
    [InlineData(false, false, 2, false, true, "NONE", "BIGINT", "BIGINT")]
    [InlineData(true, false, 3, false, true, "SELF", "INT", "BIGINT")]
    [InlineData(false, true, 2, false, false, "SELF_LEFT", "BIGINT", "INT")]
    [InlineData(true, false, 2, false, true, "INNER", "BIGINT", "BIGINT")]
    [InlineData(false, false, 1, false, true, "CROSS", "BIGINT", "INT")]
    [InlineData(true, false, 3, true, false, "NONE", "BIGINT", "BIGINT")]
    [InlineData(false, false, 2, true, true, "SELF", "BIGINT", "INT")]
    public async Task OrderedPhysicalKeyTupleRequiresAllEqualitiesAndFreshTransactionProof(
        bool update, bool primaryKey, int hops, bool revokeBeforeCommit, bool payload,
        string joined, string k1Type, string k2Type)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("G7 composite SQL Server proof requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Real SQL Server 2022 is required.");
        }

        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N")[..12];
        var src = "g7_src_" + tag;
        var dst = "g7_dst_" + tag;
        var idx = "g7_uq_" + tag;
        await using var db = new SqlConnection(cs);
        for (var n = 0; ; n++)
        {
            try { await db.OpenAsync(ct); break; }
            catch (SqlException) when (n < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
        async Task Exec(string sql)
        {
            await using var command = new SqlCommand(sql, db);
            await command.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await Exec("CREATE TABLE dbo.[" + dst +
                "] (k1 " + k1Type + " NOT NULL, k2 " + k2Type
                + " NOT NULL, label VARCHAR(30), PRIMARY KEY (k1,k2))");
            await Exec("CREATE TABLE dbo.[" + src +
                "] (k1 " + k1Type + " NOT NULL, k2 " + k2Type
                + " NOT NULL, payload INT NOT NULL" +
                (primaryKey ? ", PRIMARY KEY (k1,k2)" : "") + ")");
            if (!primaryKey)
                await Exec("CREATE UNIQUE INDEX [" + idx + "] ON dbo.[" + src + "] (k1,k2)");
            // Actual 64-bit values distinguish BIGINT from ordinary INT.
            var first = k1Type == "BIGINT" ? 2147483649L : 1L;
            var second = k2Type == "BIGINT" ? 2147483649L : 1L;
            await Exec("INSERT INTO dbo.[" + dst +
                $"] (k1,k2,label) VALUES ({first},{second},'old'),({first},{second + 1},'old'),({first + 1},{second},'old')");
            await Exec("INSERT INTO dbo.[" + src +
                $"] (k1,k2,payload) VALUES ({first},{second},11),({first},{second + 1},12),({first + 1},{second},21)");

            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
                "g7-composite-live-v1", [
                    SqlSchemaTable.WithTypes("dbo." + dst, [
                        new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)]),
                    SqlSchemaTable.WithTypes("dbo." + src, [
                        new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)])
                ]);
            var target = "dbo." + dst;
            var source = "dbo." + src;
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, snapshot, target, new[] { "k1", "k2" },
                source, new[] { "k1", "k2" });
            Assert.Equal(new[] { "k1", "k2" }, proof.SourceKeyColumns);
            Assert.Equal(new[] { "k1", "k2" }, proof.TargetKeyColumns);
            Assert.Contains("k1", proof.ProofIdentity);
            Assert.Contains("k2", proof.ProofIdentity);

            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { target, source };
            var context = new SqlPlanValidationContext("g7-composite-live", allowed)
                .WithSqlServerCteUniqueKeyAssurance(proof);

            var firstProjection = payload
                ? "k1 AS a, payload AS extra, k2 AS b"
                : "k1 AS a, k2 AS b";
            var nextProjection = payload ? "a, extra, b" : "a, b";
            if (joined.StartsWith("SELF", StringComparison.Ordinal))
                firstProjection = "s.k1 AS a, s.payload AS extra, s.k2 AS b";
            var physicalSource = joined.StartsWith("SELF", StringComparison.Ordinal)
                ? "dbo.[" + src + "] AS s"
                : "dbo.[" + src + "]";
            var singletonJoin = joined switch
            {
                "CROSS" => " CROSS JOIN (SELECT 1 AS marker) AS once",
                "INNER" => " INNER JOIN (SELECT 1 AS marker) AS once ON 1 = 1",
                "LEFT" => " LEFT JOIN (SELECT 1 AS marker) AS once ON 1 = 1",
                "SELF" => " INNER JOIN dbo.[" + src +
                    "] AS j ON s.k1 = j.k1 AND s.k2 = j.k2",
                "SELF_LEFT" => " LEFT JOIN dbo.[" + src +
                    "] AS j ON s.k1 = j.k1 AND s.k2 = j.k2",
                "NONE" => "",
                _ => throw new ArgumentOutOfRangeException(nameof(joined))
            };
            var cte = "WITH seed AS (SELECT " + firstProjection +
                " FROM " + physicalSource + singletonJoin + ") ";
            if (hops >= 2)
                cte += ", picked AS (SELECT " + nextProjection + " FROM seed) ";
            if (hops >= 3)
                cte += ", matched AS (SELECT " + nextProjection + " FROM picked) ";
            var keyAlias = hops switch
            {
                1 => "seed",
                2 => "picked",
                3 => "matched",
                _ => throw new ArgumentOutOfRangeException(nameof(hops))
            };
            var sql = cte + (update
                ? "UPDATE dbo.[" + dst + "] SET label = 'changed' FROM " + keyAlias
                : "DELETE FROM dbo.[" + dst + "] FROM " + keyAlias)
                + " WHERE dbo." + dst + ".k1 = " + keyAlias + ".a"
                + " AND dbo." + dst + ".k2 = " + keyAlias + ".b";
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, profile);
            var compiled = SqlCoreFacade.CompileDml(parsed,
                SqlAgentToolType.MsSqlServer, context,
                new DmlCompilationPolicy(), profile, null);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(" AND ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            var missing = sql.Replace(
                " AND dbo." + dst + ".k2 = " + keyAlias + ".b", "");
            var missingParsed = CoreSqlTextParser.ParseDml(
                missing, SqlAgentToolType.MsSqlServer, profile);
            Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(
                missingParsed, SqlAgentToolType.MsSqlServer, context,
                new DmlCompilationPolicy(), profile, null));

            // A complete tuple never becomes a match through OR, duplicate
            // edges, implicit coercion or a shuffled physical-key projection.
            foreach (var rejected in new[]
            {
                sql.Replace(" AND dbo." + dst + ".k2 = " + keyAlias + ".b",
                    " OR dbo." + dst + ".k2 = " + keyAlias + ".b",
                    StringComparison.Ordinal),
                sql.Replace(" AND dbo." + dst + ".k2 = " + keyAlias + ".b",
                    " AND dbo." + dst + ".k1 = " + keyAlias + ".a" +
                    " AND dbo." + dst + ".k2 = " + keyAlias + ".b",
                    StringComparison.Ordinal),
                sql.Replace(keyAlias + ".b",
                    "CAST(" + keyAlias + ".b AS BIGINT)", StringComparison.Ordinal),
                sql.Replace(firstProjection,
                    payload ? "k2 AS a, payload AS extra, k1 AS b"
                            : "k2 AS a, k1 AS b", StringComparison.Ordinal),
                sql.Replace(firstProjection,
                    payload ? "k1 AS a, payload AS extra, k1 AS b"
                            : "k1 AS a, k1 AS b", StringComparison.Ordinal),
                // Payload must not be allowed to mask or overwrite a key.
                sql.Replace(firstProjection,
                    payload ? "k1 AS a, payload AS extra, payload AS b"
                            : "k1 AS a, k2 + 0 AS b", StringComparison.Ordinal),
                // Extra projection aliases cannot introduce ambiguity.
                sql.Replace(firstProjection,
                    payload ? "k1 AS a, payload AS a, k2 AS b"
                            : "k1 AS a, k2 AS a", StringComparison.Ordinal)
            })
            {
                // Use the same profile-verified parsed-statement compiler
                // that accepted the positive SQL above. TryCompileDml(text)
                // has no policy/profile overload by design.
                Assert.Throws<SqlCompilationException>(() =>
                    SqlCoreFacade.CompileDml(
                        CoreSqlTextParser.ParseDml(rejected,
                            SqlAgentToolType.MsSqlServer, profile),
                        SqlAgentToolType.MsSqlServer, context,
                        new DmlCompilationPolicy(), profile, null));
            }

            if (joined.StartsWith("SELF", StringComparison.Ordinal))
            {
                foreach (var invalid in new[]
                {
                    sql.Replace(" AND s.k2 = j.k2", "", StringComparison.Ordinal),
                    sql.Replace(" AND s.k2 = j.k2", " OR s.k2 = j.k2", StringComparison.Ordinal),
                    sql.Replace("s.k2 = j.k2", "s.k2 + 0 = j.k2", StringComparison.Ordinal),
                    sql.Replace("s.k2 = j.k2", "s.k1 = j.k2", StringComparison.Ordinal)
                })
                {
                    Assert.Throws<SqlCompilationException>(() =>
                        SqlCoreFacade.CompileDml(
                            CoreSqlTextParser.ParseDml(invalid,
                                SqlAgentToolType.MsSqlServer, profile),
                            SqlAgentToolType.MsSqlServer, context,
                            new DmlCompilationPolicy(), profile, null));
                }
            }
            if (joined is "CROSS" or "INNER" or "LEFT")
            {
                // A derived singleton is the ONLY acceptable extra join
                // source. A physical or derived table scan can multiply
                // source keys and must never be proof-bearing.
                foreach (var wrong in new[]
                {
                    sql.Replace("(SELECT 1 AS marker)", "(SELECT k1 AS marker FROM dbo.[" + src + "])", StringComparison.Ordinal),
                    sql.Replace("(SELECT 1 AS marker)", "(SELECT 1 AS marker UNION ALL SELECT 2 AS marker)", StringComparison.Ordinal),
                    sql.Replace("(SELECT 1 AS marker)", "(SELECT 1 AS marker FROM dbo.[" + src + "])", StringComparison.Ordinal)
                })
                {
                    Assert.ThrowsAny<Exception>(() =>
                        SqlCoreFacade.CompileDml(
                            CoreSqlTextParser.ParseDml(wrong, SqlAgentToolType.MsSqlServer, profile),
                            SqlAgentToolType.MsSqlServer, context,
                            new DmlCompilationPolicy(), profile, null));
                }
            }

            await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(ct))
            {
                Assert.True(proof.IsCurrent(db, tx));
                await tx.RollbackAsync(ct);
            }

            // Production Host path: the user supplies no proof. Discovery
            // extracts ordered CTE lineage, attests sys.* and binds the
            // resulting token to preview, approval and same-tx commit.
            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("g7-metadata", It.IsAny<CancellationToken>()))
                .ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("g7-metadata", "dbo", It.IsAny<CancellationToken>()))
                .ReturnsAsync([dst]);
            metadata.Setup(x => x.GetColumnsAsync("g7-metadata", "dbo", dst,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new DatabaseColumnMetadata("dbo", dst, "k1", k1Type, true, 1),
                    new DatabaseColumnMetadata("dbo", dst, "k2", k2Type, true, 2),
                    new DatabaseColumnMetadata("dbo", dst, "label", "varchar", false)
                ]);
            var plan = await new DmlPlanFactory(metadata.Object)
                .CreateWithMetadataConnectionAsync(db, "g7-metadata", parsed,
                    SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext("g7-composite-live", allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 5, cancellationToken: ct, targetProfile: profile);
            var auto = Assert.IsType<SqlServerCteUniqueKeyAssurance>(
                plan.SqlServerCteUniqueKeyAssurance);
            Assert.Equal(new[] { "k1", "k2" }, auto.SourceKeyColumns);
            // The manually declared snapshot and Host's bounded auto-
            // discovered snapshot have deliberately different identities.
            // Compare physical, ordered key facts instead of treating their
            // independent snapshot hashes as interchangeable proof tokens.
            Assert.NotEqual(proof.SchemaSha256, auto.SchemaSha256);
            Assert.Equal(proof.DatabaseGuid, auto.DatabaseGuid);
            Assert.Equal(proof.ServerInstanceName, auto.ServerInstanceName);
            Assert.Equal(proof.UniqueIndexName, auto.UniqueIndexName);
            Assert.Equal(new[] { "k1", "k2" }, auto.TargetKeyColumns);
            DmlCoordinator.ValidatePlan(plan);
            var coordinator = new DmlCoordinator(new SqlServerConnectionFactory());
            var serverVersion = db.ServerVersion.Trim();
            var preview = await coordinator.PreviewAsync(
                cs, plan, "g7-composite-approver", ct, serverVersion);
            Assert.Equal(3, preview.AffectedRows);
            if (revokeBeforeCommit)
            {
                // Preview/approval already happened. Removing physical
                // uniqueness must invalidate the exact same plan when the
                // commit transaction rechecks live index metadata.
                Assert.False(primaryKey);
                await Exec("DROP INDEX [" + idx + "] ON dbo.[" + src + "]");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    coordinator.CommitAsync(cs, plan, preview.Challenge,
                        "g7-composite-approver", ct, serverVersion));
                await using var unchanged = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.[" + dst + "] WHERE label = 'old'", db);
                Assert.Equal(3, Convert.ToInt32(await unchanged.ExecuteScalarAsync(ct)));
            }
            else
            {
                var committed = await coordinator.CommitAsync(
                    cs, plan, preview.Challenge, "g7-composite-approver", ct, serverVersion);
                Assert.True(committed.Committed);
                await using var count = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.[" + dst + "]", db);
                Assert.Equal(update ? 3 : 0, Convert.ToInt32(await count.ExecuteScalarAsync(ct)));
            }
            if (primaryKey)
            {
                await using var command = new SqlCommand(
                    "SELECT kc.name FROM sys.key_constraints kc WHERE kc.parent_object_id = OBJECT_ID(@t) AND kc.type = 'PK'", db);
                command.Parameters.AddWithValue("@t", source);
                var pk = (string?)await command.ExecuteScalarAsync(ct);
                Assert.False(string.IsNullOrWhiteSpace(pk));
                await Exec("ALTER TABLE dbo.[" + src + "] DROP CONSTRAINT [" + pk + "]");
            }
            else if (!revokeBeforeCommit)
                await Exec("DROP INDEX [" + idx + "] ON dbo.[" + src + "]");

            await using var recheck = (SqlTransaction)await db.BeginTransactionAsync(ct);
            Assert.False(proof.IsCurrent(db, recheck));
            await recheck.RollbackAsync(ct);
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + src + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + dst + "]");
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("nonunique")]
    [InlineData("filtered")]
    [InlineData("disabled")]
    [InlineData("nullable")]
    [InlineData("source-bigint")]
    [InlineData("target-bigint")]
    [InlineData("computed")]
    public async Task CompositeLiveMetadataRejectsUnprovenIndexAndOperandTypes(string shape)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("Composite SQL Server negative metadata tests require a real database.");
            Assert.Skip("Real SQL Server required.");
        }

        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..12];
        var src = "g7_negsrc_" + id;
        var dst = "g7_negdst_" + id;
        var idx = "g7_negidx_" + id;
        await using var db = new SqlConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(ct); break; }
            catch (SqlException) when (retry < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
        async Task Exec(string sql)
        {
            await using var command = new SqlCommand(sql, db);
            await command.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await Exec("CREATE TABLE dbo.[" + dst + "] (k1 INT NOT NULL, k2 " +
                (shape == "target-bigint" ? "BIGINT" : "INT") + " NOT NULL)");
            var sourceDefinition = shape switch
            {
                "nullable" => "k1 INT NOT NULL, k2 INT NULL",
                "source-bigint" => "k1 INT NOT NULL, k2 BIGINT NOT NULL",
                "computed" => "k1 INT NOT NULL, k2 AS (k1 + 1) PERSISTED",
                _ => "k1 INT NOT NULL, k2 INT NOT NULL"
            };
            await Exec("CREATE TABLE dbo.[" + src + "] (" + sourceDefinition + ")");
            if (shape != "missing")
            {
                var unique = shape == "nonunique" ? "" : "UNIQUE ";
                var predicate = shape == "filtered" ? " WHERE k2 > 0" : "";
                await Exec("CREATE " + unique + "INDEX [" + idx + "] ON dbo.[" +
                    src + "] (k1, k2)" + predicate);
            }
            if (shape == "disabled")
                await Exec("ALTER INDEX [" + idx + "] ON dbo.[" + src + "] DISABLE");

            // A seemingly matching caller-declared Numeric snapshot must
            // never override real sys.* index keys and physical data types.
            var snapshot = new SqlSchemaSnapshot(
                SqlAgentToolType.MsSqlServer, "g7-negative-metadata", [
                    SqlSchemaTable.WithTypes("dbo." + dst, [
                        new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)]),
                    SqlSchemaTable.WithTypes("dbo." + src, [
                        new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)])
                ]);
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    db, snapshot, "dbo." + dst, new[] { "k1", "k2" },
                    "dbo." + src, new[] { "k1", "k2" }));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + src + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + dst + "]");
        }
    }

    private sealed class SqlServerConnectionFactory : IDbConnectionFactory
    {
        public DbConnection Create(string connectionString) => new SqlConnection(connectionString);
    }

    [Fact]
    public async Task ReversedPhysicalIndexOrdinalCannotMintOrderedTuple()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("G7 composite SQL Server metadata test requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Real SQL Server 2022 required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..12];
        var src = "g7_rev_" + id;
        var dst = "g7_dst_" + id;
        await using var db = new SqlConnection(cs);
        for (var retry = 0; ; retry++)
        {
            try { await db.OpenAsync(ct); break; }
            catch (SqlException) when (retry < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
        async Task Exec(string sql)
        {
            await using var cmd = new SqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await Exec("CREATE TABLE dbo.[" + dst + "] (k1 INT NOT NULL, k2 INT NOT NULL)");
            await Exec("CREATE TABLE dbo.[" + src + "] (k1 INT NOT NULL, k2 INT NOT NULL, CONSTRAINT [pk_" + id + "] PRIMARY KEY(k2,k1))");
            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer, "g7-reversed", [
                SqlSchemaTable.WithTypes("dbo." + dst, [new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric), new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)]),
                SqlSchemaTable.WithTypes("dbo." + src, [new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric), new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)])]);
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    db, snapshot, "dbo." + dst, new[] { "k1", "k2" },
                    "dbo." + src, new[] { "k1", "k2" }));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + src + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + dst + "]");
        }
    }
}
