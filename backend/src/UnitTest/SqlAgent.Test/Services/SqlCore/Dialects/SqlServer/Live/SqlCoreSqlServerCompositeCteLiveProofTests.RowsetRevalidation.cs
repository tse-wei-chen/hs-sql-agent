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

public sealed partial class SqlCoreSqlServerCompositeCteLiveProofTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleBigIntPhysicalJoinKeepsRealPreviewApprovalAndCommit(bool update)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("M07 BIGINT JOIN requires SQL Server.");
            Assert.Skip("SQL Server required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N")[..12];
        var source = "m07_s_" + tag;
        var joined = "m07_j_" + tag;
        var target = "m07_t_" + tag;
        await using var db = new SqlConnection(cs);
        for (var attempt = 0; ; attempt++)
        {
            try { await db.OpenAsync(ct); break; }
            catch (SqlException) when (attempt < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
        async Task Exec(string statement)
        {
            await using var cmd = new SqlCommand(statement, db);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await Exec("CREATE TABLE dbo.[" + target +
                "] (id BIGINT NOT NULL PRIMARY KEY, label VARCHAR(20))");
            await Exec("CREATE TABLE dbo.[" + source +
                "] (id BIGINT NOT NULL PRIMARY KEY)");
            await Exec("CREATE TABLE dbo.[" + joined +
                "] (external_id BIGINT NOT NULL UNIQUE)");
            await Exec("INSERT INTO dbo.[" + source +
                "] (id) VALUES (2147483649),(2147483650)");
            await Exec("INSERT INTO dbo.[" + joined +
                "] (external_id) VALUES (2147483649),(2147483650)");
            await Exec("INSERT INTO dbo.[" + target +
                "] (id,label) VALUES (2147483649,'old'),(2147483650,'old')");
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var sql = "WITH picked AS (SELECT s.id AS key_id FROM dbo.[" +
                source + "] AS s INNER JOIN dbo.[" + joined +
                "] AS j ON s.id = j.external_id) " +
                (update
                    ? "UPDATE dbo.[" + target + "] SET label = 'new' FROM picked"
                    : "DELETE FROM dbo.[" + target + "] FROM picked") +
                " WHERE dbo." + target + ".id = picked.key_id";
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, profile);
            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("m07-single", It.IsAny<CancellationToken>()))
                .ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("m07-single", "dbo",
                    It.IsAny<CancellationToken>())).ReturnsAsync([target]);
            metadata.Setup(x => x.GetColumnsAsync("m07-single", "dbo", target,
                    It.IsAny<CancellationToken>())).ReturnsAsync([
                        new DatabaseColumnMetadata("dbo", target, "id", "bigint", true, 1),
                        new DatabaseColumnMetadata("dbo", target, "label", "varchar", false)
                    ]);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "dbo." + source, "dbo." + joined, "dbo." + target };
            var plan = await new DmlPlanFactory(metadata.Object)
                .CreateWithMetadataConnectionAsync(db, "m07-single", parsed,
                    SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext("m07-single", allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 4, cancellationToken: ct, targetProfile: profile);
            var proof = Assert.IsType<SqlServerCteUniqueKeyAssurance>(
                plan.SqlServerCteUniqueKeyAssurance);
            Assert.Equal(new[] { "id" }, proof.SourceKeyColumns);
            Assert.Equal(new[] { "external_id" }, proof.JoinedSourceKeyColumns);
            Assert.Contains("BIGINT", proof.ProofIdentity, StringComparison.Ordinal);
            DmlCoordinator.ValidatePlan(plan);
            var coordinator = new DmlCoordinator(new SqlServerConnectionFactory());
            var version = db.ServerVersion.Trim();
            var preview = await coordinator.PreviewAsync(cs, plan, "m07-single-approver", ct, version);
            Assert.Equal(2, preview.AffectedRows);
            var commit = await coordinator.CommitAsync(
                cs, plan, preview.Challenge, "m07-single-approver", ct, version);
            Assert.True(commit.Committed);
            await using var count = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.[" + target + "]", db);
            Assert.Equal(update ? 2 : 0, Convert.ToInt32(await count.ExecuteScalarAsync(ct)));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + joined + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

    [Theory]
    [InlineData(true, false, 1, false)]
    [InlineData(false, false, 2, false)]
    [InlineData(true, false, 3, false)]
    [InlineData(false, true, 1, false)]
    [InlineData(true, true, 2, false)]
    [InlineData(false, true, 3, false)]
    [InlineData(true, false, 2, true)]
    [InlineData(false, true, 3, true)]
    public async Task CorrelatedExistsAntiJoinPreservesLiveUniqueKeyRowSet(
        bool update, bool antiJoin, int hops, bool revokeBeforeCommit)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("M07 EXISTS requires a real SQL Server.");
            Assert.Skip("SQL Server 2022 required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N")[..12];
        var src = "m07_exists_src_" + tag;
        var dst = "m07_exists_dst_" + tag;
        var uniqueIndex = "m07_exists_uq_" + tag;
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
                "] (k1 INT NOT NULL, k2 BIGINT NOT NULL, label VARCHAR(30), PRIMARY KEY(k1,k2))");
            await Exec("CREATE TABLE dbo.[" + src +
                "] (k1 INT NOT NULL, k2 BIGINT NOT NULL, payload INT NOT NULL)");
            await Exec("CREATE UNIQUE INDEX [" + uniqueIndex +
                "] ON dbo.[" + src + "] (k1,k2)");
            await Exec("INSERT INTO dbo.[" + dst +
                "] (k1,k2,label) VALUES (1,2147483649,'old'),(1,2147483650,'old'),(2,2147483649,'old')");
            await Exec("INSERT INTO dbo.[" + src +
                "] (k1,k2,payload) VALUES (1,2147483649,11),(1,2147483650,12),(2,2147483649,21)");
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "dbo." + src, "dbo." + dst };
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
                    "m07-exists", [
                        SqlSchemaTable.WithTypes("dbo." + dst, [
                            new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                            new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)]),
                        SqlSchemaTable.WithTypes("dbo." + src, [
                            new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                            new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)])
                    ]), "dbo." + dst, new[] { "k1", "k2" },
                    "dbo." + src, new[] { "k1", "k2" });
            var context = new SqlPlanValidationContext("m07-exists", allowed)
                .WithSqlServerCteUniqueKeyAssurance(proof);
            var inner = "SELECT 1 FROM dbo.[" + src +
                "] AS f WHERE f.k1 = s.k1 AND f.k2 = s.k2 AND f.payload = 11";
            var prefix = "WITH seed AS (SELECT s.k1 AS a, s.k2 AS b " +
                "FROM dbo.[" + src + "] AS s WHERE " +
                (antiJoin ? "NOT EXISTS (" : "EXISTS (") + inner + ") ) ";
            if (hops >= 2) prefix += ", picked AS (SELECT a, b FROM seed) ";
            if (hops >= 3) prefix += ", matched AS (SELECT a, b FROM picked) ";
            var alias = hops switch { 1 => "seed", 2 => "picked", 3 => "matched",
                _ => throw new ArgumentOutOfRangeException(nameof(hops)) };
            var sql = prefix + (update
                ? "UPDATE dbo.[" + dst + "] SET label = 'changed' FROM " + alias
                : "DELETE FROM dbo.[" + dst + "] FROM " + alias)
                + " WHERE dbo." + dst + ".k1 = " + alias + ".a"
                + " AND dbo." + dst + ".k2 = " + alias + ".b";
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, profile);
            var compiled = SqlCoreFacade.CompileDml(parsed,
                SqlAgentToolType.MsSqlServer, context,
                new DmlCompilationPolicy(), profile, null);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            // Preserve the previous non-subquery Boolean grammar, including
            // a plain NOT filter. Only nested query-bearing predicates gain
            // the stricter semi-/anti-join proof requirements.
            var plainNot = sql.Replace(
                (antiJoin ? "NOT EXISTS (" : "EXISTS (") + inner + ")",
                "NOT (s.payload = 0)", StringComparison.Ordinal);
            Assert.StartsWith("WITH ",
                SqlCoreFacade.CompileDml(
                    CoreSqlTextParser.ParseDml(
                        plainNot, SqlAgentToolType.MsSqlServer, profile),
                    SqlAgentToolType.MsSqlServer, context,
                    new DmlCompilationPolicy(), profile, null).Sql,
                StringComparison.OrdinalIgnoreCase);


            // Correlated source must have a complete mandatory ordinary
            // key equality graph; an OR, computed key or other physical
            // table cannot borrow the source's live index assurance.
            foreach (var bad in new[]
            {
                inner.Replace(" AND f.k2 = s.k2", "", StringComparison.Ordinal),
                inner.Replace(" AND f.k2 = s.k2", " OR f.k2 = s.k2", StringComparison.Ordinal),
                inner.Replace("f.k2 = s.k2", "f.k2 + 0 = s.k2", StringComparison.Ordinal),
                inner.Replace("f.k2 = s.k2", "f.k1 = s.k2", StringComparison.Ordinal),
                inner.Replace(" AND f.payload = 11", " OR f.payload = 11", StringComparison.Ordinal),
                inner.Replace(" AS f", " AS s", StringComparison.Ordinal),
                inner.Replace("SELECT 1", "SELECT DISTINCT 1", StringComparison.Ordinal)
            })
            {
                Assert.Throws<SqlCompilationException>(() =>
                    SqlCoreFacade.CompileDml(
                        CoreSqlTextParser.ParseDml(
                            sql.Replace(inner, bad, StringComparison.Ordinal),
                            SqlAgentToolType.MsSqlServer, profile),
                        SqlAgentToolType.MsSqlServer, context,
                        new DmlCompilationPolicy(), profile, null));
            }

            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("m07-exists-meta", It.IsAny<CancellationToken>()))
                .ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("m07-exists-meta", "dbo", It.IsAny<CancellationToken>()))
                .ReturnsAsync([dst]);
            metadata.Setup(x => x.GetColumnsAsync("m07-exists-meta", "dbo", dst,
                    It.IsAny<CancellationToken>())).ReturnsAsync([
                        new DatabaseColumnMetadata("dbo", dst, "k1", "int", true, 1),
                        new DatabaseColumnMetadata("dbo", dst, "k2", "bigint", true, 2),
                        new DatabaseColumnMetadata("dbo", dst, "label", "varchar", false)
                    ]);
            var plan = await new DmlPlanFactory(metadata.Object)
                .CreateWithMetadataConnectionAsync(db, "m07-exists-meta", parsed,
                    SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext("m07-exists", allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 4, cancellationToken: ct, targetProfile: profile);
            var auto = Assert.IsType<SqlServerCteUniqueKeyAssurance>(
                plan.SqlServerCteUniqueKeyAssurance);
            Assert.Equal(proof.DatabaseGuid, auto.DatabaseGuid);
            DmlCoordinator.ValidatePlan(plan);
            var coordinator = new DmlCoordinator(new SqlServerConnectionFactory());
            var version = db.ServerVersion.Trim();
            var preview = await coordinator.PreviewAsync(
                cs, plan, "m07-exists-approver", ct, version);
            Assert.Equal(antiJoin ? 2 : 1, preview.AffectedRows);
            if (revokeBeforeCommit)
            {
                // An approved semi-/anti-join must not outlive its physical
                // source UNIQUE index. The target remains unchanged.
                await Exec("DROP INDEX [" + uniqueIndex + "] ON dbo.[" + src + "]");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    coordinator.CommitAsync(
                        cs, plan, preview.Challenge, "m07-exists-approver", ct, version));
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
                Assert.False(proof.IsCurrent(db, tx));
                await tx.RollbackAsync(ct);
            }
            else
            {
                var commit = await coordinator.CommitAsync(
                    cs, plan, preview.Challenge, "m07-exists-approver", ct, version);
                Assert.True(commit.Committed);
            }
            await using var count = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.[" + dst + "] WHERE label = 'old'", db);
            Assert.Equal(revokeBeforeCommit ? 3 : (antiJoin ? 1 : 2),
                Convert.ToInt32(await count.ExecuteScalarAsync(ct)));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + src + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + dst + "]");
        }
    }

}
