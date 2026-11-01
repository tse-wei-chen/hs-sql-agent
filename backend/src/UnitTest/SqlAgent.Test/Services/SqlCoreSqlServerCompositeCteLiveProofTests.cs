using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
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
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task OrderedPhysicalKeyTupleRequiresAllEqualitiesAndFreshTransactionProof(
        bool update, bool primaryKey, bool chained)
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
                "] (k1 INT NOT NULL, k2 INT NOT NULL, label VARCHAR(30), PRIMARY KEY (k1,k2))");
            await Exec("CREATE TABLE dbo.[" + src +
                "] (k1 INT NOT NULL, k2 INT NOT NULL, payload INT NOT NULL" +
                (primaryKey ? ", PRIMARY KEY (k1,k2)" : "") + ")");
            if (!primaryKey)
                await Exec("CREATE UNIQUE INDEX [" + idx + "] ON dbo.[" + src + "] (k1,k2)");
            await Exec("INSERT INTO dbo.[" + dst +
                "] (k1,k2,label) VALUES (1,1,'old'),(1,2,'old'),(2,1,'old')");
            await Exec("INSERT INTO dbo.[" + src +
                "] (k1,k2,payload) VALUES (1,1,11),(1,2,12),(2,1,21)");

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

            var cte = "WITH seed AS (SELECT k1 AS a, k2 AS b FROM dbo.[" + src + "]) ";
            if (chained)
                cte += ", picked AS (SELECT a, b FROM seed) ";
            var keyAlias = chained ? "picked" : "seed";
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

            await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(ct))
            {
                Assert.True(proof.IsCurrent(db, tx));
                await tx.RollbackAsync(ct);
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
            else
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
