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
    // G9/M05: an unfiltered native SQL Server UNIQUE index attests
    // non-multiplying ordinary key equality even when the source key is
    // nullable. NULL never joins through the required ordinary '=' graph.
    [Theory]
    [InlineData(true, 1, false, "BIGINT")]
    [InlineData(false, 2, false, "UNIQUEIDENTIFIER")]
    [InlineData(true, 3, true, "VARCHAR(24)")]
    [InlineData(false, 2, true, "DECIMAL(12,2)")]
    public async Task NullableSourceUniqueKeyUsesOrdinaryAnsiEqualityAndCommitProof(
        bool update, int hops, bool revokeAfterPreview, string keyType)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("G9/M05 nullable UNIQUE needs real SQL Server 2022.");
            Assert.Skip("Real SQL Server required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N")[..12];
        var src = "m05_null_src_" + tag;
        var dst = "m05_null_dst_" + tag;
        var index = "m05_null_uq_" + tag;
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
        var physicalType = keyType.StartsWith("VARCHAR(", StringComparison.Ordinal)
            ? keyType + " COLLATE Latin1_General_100_BIN2" : keyType;
        var kind = keyType == "UNIQUEIDENTIFIER" ? SqlSchemaValueKind.Uuid
            : keyType.StartsWith("VARCHAR(", StringComparison.Ordinal)
                ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric;
        string Value(int n) => keyType switch
        {
            "UNIQUEIDENTIFIER" => "'00000000-0000-0000-0000-" + n.ToString("D12") + "'",
            "VARCHAR(24)" => "'Key" + n.ToString("D4") + "'",
            "DECIMAL(12,2)" => (n + 0.25m).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            _ => (2147483649L + n).ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        };
        try
        {
            await Exec("CREATE TABLE dbo.[" + dst + "] (k1 INT NOT NULL, k2 " +
                physicalType + " NOT NULL, label VARCHAR(20), PRIMARY KEY(k1,k2))");
            await Exec("CREATE TABLE dbo.[" + src + "] (k1 INT NOT NULL, k2 " +
                physicalType + " NULL, payload INT NOT NULL)");
            await Exec("CREATE UNIQUE INDEX [" + index + "] ON dbo.[" + src + "] (k1,k2)");
            await Exec("INSERT INTO dbo.[" + dst +
                $"] (k1,k2,label) VALUES (1,{Value(1)},'old'),(2,{Value(2)},'old'),(3,{Value(3)},'old')");
            await Exec("INSERT INTO dbo.[" + src +
                $"] (k1,k2,payload) VALUES (1,{Value(1)},11),(2,{Value(2)},22),(3,NULL,33)");
            await using (var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.[" + src + "] WHERE k2 IS NULL", db))
                Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)));

            var source = "dbo." + src;
            var target = "dbo." + dst;
            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
                "m05-nullable-unfiltered-unique", [
                    SqlSchemaTable.WithTypes(target, [
                        new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn("k2", kind)]),
                    SqlSchemaTable.WithTypes(source, [
                        new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                        new SqlSchemaColumn("k2", kind)])
                ]);
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, snapshot, target, new[] { "k1", "k2" },
                source, new[] { "k1", "k2" });
            Assert.Contains("source-key-null-semantics=ordinary-ansi-on", proof.ProofIdentity);
            var profile = new SqlProviderCapabilityProfile(
                SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { source, target };
            var context = new SqlPlanValidationContext("m05-nullable", allowed)
                .WithSqlServerCteUniqueKeyAssurance(proof);
            var prefix = "WITH seed AS (SELECT k1 AS a, k2 AS b FROM dbo.[" + src + "]) ";
            if (hops >= 2) prefix += ", picked AS (SELECT a,b FROM seed) ";
            if (hops >= 3) prefix += ", matched AS (SELECT a,b FROM picked) ";
            var alias = hops switch { 1 => "seed", 2 => "picked", 3 => "matched", _ => "?" };
            var where = " WHERE dbo." + dst + ".k1 = " + alias + ".a"
                + " AND dbo." + dst + ".k2 = " + alias + ".b";
            var sql = prefix + (update
                ? "UPDATE dbo.[" + dst + "] SET label = 'changed' FROM " + alias
                : "DELETE FROM dbo.[" + dst + "] FROM " + alias) + where;
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, profile);
            Assert.StartsWith("WITH ", SqlCoreFacade.CompileDml(parsed,
                SqlAgentToolType.MsSqlServer, context, new DmlCompilationPolicy(),
                profile, null).Sql, StringComparison.OrdinalIgnoreCase);
            foreach (var rejected in new[]
            {
                sql.Replace(" AND dbo." + dst + ".k2 = " + alias + ".b", ""),
                sql.Replace(" AND dbo." + dst + ".k2 = " + alias + ".b",
                    " OR dbo." + dst + ".k2 = " + alias + ".b",
                    StringComparison.Ordinal),
                sql.Replace(alias + ".b", "COALESCE(" + alias + ".b, " +
                    alias + ".b)", StringComparison.Ordinal)
            })
                Assert.ThrowsAny<Exception>(() => SqlCoreFacade.CompileDml(
                    CoreSqlTextParser.ParseDml(rejected,
                        SqlAgentToolType.MsSqlServer, profile),
                    SqlAgentToolType.MsSqlServer, context,
                    new DmlCompilationPolicy(), profile, null));

            await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(ct))
            {
                Assert.True(proof.IsCurrent(db, tx));
                await tx.RollbackAsync(ct);
            }
            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("m05-meta", It.IsAny<CancellationToken>()))
                .ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("m05-meta", "dbo",
                It.IsAny<CancellationToken>())).ReturnsAsync([dst]);
            metadata.Setup(x => x.GetColumnsAsync("m05-meta", "dbo", dst,
                It.IsAny<CancellationToken>())).ReturnsAsync([
                    new DatabaseColumnMetadata("dbo", dst, "k1", "int", true, 1),
                    new DatabaseColumnMetadata("dbo", dst, "k2", keyType, true, 2),
                    new DatabaseColumnMetadata("dbo", dst, "label", "varchar", false)
                ]);
            var plan = await new DmlPlanFactory(metadata.Object)
                .CreateWithMetadataConnectionAsync(db, "m05-meta", parsed,
                    SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext("m05-nullable", allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 3, cancellationToken: ct, targetProfile: profile);
            var auto = Assert.IsType<SqlServerCteUniqueKeyAssurance>(
                plan.SqlServerCteUniqueKeyAssurance);
            Assert.Contains("source-key-null-semantics=ordinary-ansi-on", auto.ProofIdentity);
            DmlCoordinator.ValidatePlan(plan);
            var coordinator = new DmlCoordinator(new SqlServerConnectionFactory());
            var version = db.ServerVersion.Trim();
            var preview = await coordinator.PreviewAsync(
                cs, plan, "m05-approver", ct, version);
            Assert.Equal(2, preview.AffectedRows);
            if (revokeAfterPreview)
            {
                await Exec("DROP INDEX [" + index + "] ON dbo.[" + src + "]");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    coordinator.CommitAsync(cs, plan, preview.Challenge,
                        "m05-approver", ct, version));
                await using var unchanged = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.[" + dst + "] WHERE label = 'old'", db);
                Assert.Equal(3, Convert.ToInt32(await unchanged.ExecuteScalarAsync(ct)));
            }
            else
            {
                var committed = await coordinator.CommitAsync(
                    cs, plan, preview.Challenge, "m05-approver", ct, version);
                Assert.True(committed.Committed);
                await using var verify = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.[" + dst + "] WHERE label = 'old'", db);
                Assert.Equal(1, Convert.ToInt32(await verify.ExecuteScalarAsync(ct)));
            }
            if (!revokeAfterPreview)
                await Exec("DROP INDEX [" + index + "] ON dbo.[" + src + "]");
            await using var staleTx = (SqlTransaction)await db.BeginTransactionAsync(ct);
            Assert.False(proof.IsCurrent(db, staleTx));
            await staleTx.RollbackAsync(ct);
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

}
