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
    [Theory]
    [InlineData(true, false, 1, false, "INT", "BIGINT", false)]
    [InlineData(false, false, 1, false, "INT", "BIGINT", false)]
    [InlineData(true, true, 2, false, "INT", "BIGINT", false)]
    [InlineData(false, true, 3, false, "INT", "BIGINT", false)]
    [InlineData(true, false, 2, true, "INT", "BIGINT", false)]
    [InlineData(false, true, 1, true, "INT", "BIGINT", false)]
    [InlineData(true, false, 1, false, "SMALLINT", "TINYINT", false)]
    [InlineData(false, true, 2, false, "TINYINT", "SMALLINT", false)]
    [InlineData(true, true, 3, true, "SMALLINT", "INT", false)]
    [InlineData(true, false, 1, false, "DECIMAL(12,2)", "DECIMAL(18,4)", false)]
    [InlineData(false, true, 2, false, "NUMERIC(12,2)", "DECIMAL(18,4)", false)]
    [InlineData(true, true, 3, true, "DECIMAL(12,2)", "DECIMAL(20,0)", false)]
    [InlineData(true, false, 1, false, "UNIQUEIDENTIFIER", "INT", false)]
    [InlineData(false, true, 2, false, "SMALLINT", "UNIQUEIDENTIFIER", false)]
    [InlineData(true, true, 3, true, "UNIQUEIDENTIFIER", "DECIMAL(12,2)", false)]
    [InlineData(true, false, 1, false, "VARCHAR(24)", "INT", false)]
    [InlineData(false, true, 2, false, "NVARCHAR(32)", "VARCHAR(24)", false)]
    [InlineData(true, true, 3, true, "NVARCHAR(32)", "DECIMAL(12,2)", false)]
    [InlineData(true, false, 1, false, "VARCHAR(24) COLLATE Latin1_General_100_CI_AS", "INT", false)]
    [InlineData(false, true, 2, false, "NVARCHAR(32) COLLATE Latin1_General_100_CI_AI", "VARCHAR(24) COLLATE Latin1_General_100_CI_AI", false)]
    [InlineData(true, true, 3, true, "NVARCHAR(32) COLLATE Latin1_General_100_CS_AS", "DECIMAL(12,2)", false)]
    // M08: second independently UNIQUE physical source is a native
    // UPDATE FROM / DELETE FROM input, not hidden inside seed's SELECT.
    [InlineData(true, false, 1, false, "INT", "BIGINT", true)]
    [InlineData(false, false, 3, false, "DECIMAL(12,2)", "UNIQUEIDENTIFIER", true)]
    [InlineData(true, false, 2, true, "NVARCHAR(32)", "INT", true)]
    [InlineData(false, false, 1, true, "SMALLINT", "TINYINT", true)]
    public async Task CrossPhysicalUniqueJoinRequiresBothLiveIndexesAndCommitFreshness(
        bool update, bool leftJoin, int hops, bool revokeJoinedIndex,
        string k1Type, string k2Type, bool separateSources)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("M07 cross-table proof requires a real SQL Server.");
            Assert.Skip("SQL Server 2022 is required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..12];
        var source = "m07_src_" + id;
        var joined = "m07_join_" + id;
        var target = "m07_dst_" + id;
        var index = "m07_uq_" + id;
        await using var db = new SqlConnection(cs);
        for (var attempt = 0; ; attempt++)
        {
            try { await db.OpenAsync(ct); break; }
            catch (SqlException) when (attempt < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
        async Task Exec(string sql)
        {
            await using var cmd = new SqlCommand(sql, db);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        try
        {
            static bool IsText(string type) =>
                type.StartsWith("VARCHAR(", StringComparison.Ordinal)
                || type.StartsWith("NVARCHAR(", StringComparison.Ordinal);
            string PhysicalType(string type) => IsText(type)
                ? (type.Contains(" COLLATE ", StringComparison.Ordinal) ? type
                    : type + " COLLATE Latin1_General_100_BIN2")
                : type;
            await Exec("CREATE TABLE dbo.[" + target +
                "] (k1 " + PhysicalType(k1Type) + " NOT NULL, k2 " + PhysicalType(k2Type) + " NOT NULL, label VARCHAR(30), PRIMARY KEY (k1,k2))");
            await Exec("CREATE TABLE dbo.[" + source +
                "] (k1 " + PhysicalType(k1Type) + " NOT NULL, k2 " + PhysicalType(k2Type) + " NOT NULL, payload INT, PRIMARY KEY (k1,k2))");
            await Exec("CREATE TABLE dbo.[" + joined +
                "] (j1 " + PhysicalType(k1Type) + " NOT NULL, j2 " + PhysicalType(k2Type) + " NOT NULL, extra INT)");
            await Exec("CREATE UNIQUE INDEX [" + index + "] ON dbo.[" + joined + "] (j1,j2)");
            // Keep 64-bit sample values for BIGINT, but fit exact
            // SMALLINT/TINYINT ranges without implicit conversion.
            decimal first = k1Type == "BIGINT" ? 2147483649m
                : (k1Type.StartsWith("DECIMAL(", StringComparison.Ordinal)
                   || k1Type.StartsWith("NUMERIC(", StringComparison.Ordinal)) && !k1Type.EndsWith(",0)", StringComparison.Ordinal)
                    ? 1.25m : 1m;
            decimal second = k2Type == "BIGINT" ? 2147483649m
                : k2Type.StartsWith("DECIMAL(", StringComparison.Ordinal) && !k2Type.EndsWith(",0)", StringComparison.Ordinal)
                    ? 101.125m : 1m;
            string KeySql(string type, decimal numeric, int ordinal)
            {
                if (IsText(type))
                    return (type.StartsWith("NVARCHAR", StringComparison.Ordinal) ? "N" : "")
                        + "'Key" + ordinal.ToString("D4") + "'";
                if (type != "UNIQUEIDENTIFIER")
                    return numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // SQL literal is explicitly typed by the physical UNIQUEIDENTIFIER
                // column; no conversion is part of the key equality predicate.
                return "'00000000-0000-0000-0000-" + ordinal.ToString("D12") + "'";
            }
            var key1 = KeySql(k1Type, first, 1);
            var key2 = KeySql(k2Type, second, 11);
            var key1Next = KeySql(k1Type, first + 1, 2);
            var key2Next = KeySql(k2Type, second + 1, 12);

            await Exec("INSERT INTO dbo.[" + target +
                $"] (k1,k2,label) VALUES ({key1},{key2},'old'),({key1},{key2Next},'old'),({key1Next},{key2},'old')");
            await Exec("INSERT INTO dbo.[" + source +
                $"] (k1,k2,payload) VALUES ({key1},{key2},11),({key1},{key2Next},12),({key1Next},{key2},21)");
            await Exec("INSERT INTO dbo.[" + joined +
                $"] (j1,j2,extra) VALUES ({key1},{key2},5),({key1},{key2Next},6),({key1Next},{key2},7)");

            var profile = new SqlProviderCapabilityProfile(SqlAgentToolType.MsSqlServer, new Version(16, 0));
            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
                "m07-cross-physical", [
                    SqlSchemaTable.WithTypes("dbo." + target, [
                        new SqlSchemaColumn("k1", k1Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k1Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric)),
                        new SqlSchemaColumn("k2", k2Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k2Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric))]),
                    SqlSchemaTable.WithTypes("dbo." + source, [
                        new SqlSchemaColumn("k1", k1Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k1Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric)),
                        new SqlSchemaColumn("k2", k2Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k2Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric))]),
                    SqlSchemaTable.WithTypes("dbo." + joined, [
                        new SqlSchemaColumn("j1", k1Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k1Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric)),
                        new SqlSchemaColumn("j2", k2Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k2Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric))])
                ]);
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForKeyPreservingJoin(
                db, snapshot, "dbo." + target, new[] { "k1", "k2" },
                "dbo." + source, new[] { "k1", "k2" },
                "dbo." + joined, new[] { "j1", "j2" });
            Assert.Equal("dbo." + joined, proof.JoinedSourceTable);
            Assert.Contains("m07-joined-source=", proof.ProofIdentity, StringComparison.Ordinal);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "dbo." + source, "dbo." + joined, "dbo." + target };
            var context = new SqlPlanValidationContext("m07-cross-live", allowed)
                .WithSqlServerCteUniqueKeyAssurance(proof);

            var prefix = separateSources
                ? "WITH seed AS (SELECT s.k1 AS a, s.k2 AS b FROM dbo.[" +
                  source + "] AS s) "
                : "WITH seed AS (SELECT s.k1 AS a, s.k2 AS b FROM dbo.[" +
                  source + "] AS s " + (leftJoin ? "LEFT" : "INNER") +
                  " JOIN dbo.[" + joined + "] AS j ON s.k1 = j.j1 AND s.k2 = j.j2) ";
            if (hops >= 2) prefix += ", picked AS (SELECT a, b FROM seed) ";
            if (hops >= 3) prefix += ", final AS (SELECT a, b FROM picked) ";
            var cte = hops switch { 1 => "seed", 2 => "picked", 3 => "final", _ => "?" };
            var sql = prefix + (update
                ? "UPDATE dbo.[" + target + "] SET label = 'updated' FROM " + cte
                : "DELETE FROM dbo.[" + target + "] FROM " + cte)
                + (separateSources ? ", dbo.[" + joined + "] AS j" : "")
                + " WHERE dbo." + target + ".k1 = " + cte + ".a"
                + " AND dbo." + target + ".k2 = " + cte + ".b"
                + (separateSources
                    ? " AND " + cte + ".a = j.j1 AND " + cte + ".b = j.j2"
                      + " AND j.extra = 5"
                    : "");
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, profile);
            var compiled = SqlCoreFacade.CompileDml(parsed, SqlAgentToolType.MsSqlServer,
                context, new DmlCompilationPolicy(), profile, null);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);

            if (separateSources)
            {
                var secondKey = " AND " + cte + ".a = j.j1 AND " + cte + ".b = j.j2";
                foreach (var invalid in new[]
                {
                    sql.Replace(secondKey, "", StringComparison.Ordinal),
                    sql.Replace(secondKey, " AND " + cte + ".a = j.j1", StringComparison.Ordinal),
                    sql.Replace(secondKey, " AND " + cte + ".a = j.j1 OR " + cte + ".b = j.j2", StringComparison.Ordinal),
                    sql.Replace(cte + ".b = j.j2", cte + ".b + 0 = j.j2", StringComparison.Ordinal),
                    sql.Replace(cte + ".a = j.j1", cte + ".a = j.j2", StringComparison.Ordinal),
                    sql.Replace("dbo.[" + joined + "] AS j", "dbo.[" + target + "] AS j", StringComparison.Ordinal),
                    sql.Replace("j.j2", "CAST(j.j2 AS BIGINT)", StringComparison.Ordinal),
                    sql.Replace(" AND j.extra = 5", " AND j.extra + 0 = 5", StringComparison.Ordinal),
                    sql.Replace(" AND j.extra = 5",
                        " AND EXISTS (SELECT 1 FROM dbo.[" + joined +
                        "] AS q WHERE q.j1 = j.j1)", StringComparison.Ordinal),
                    sql.Replace(" AND j.extra = 5",
                        " AND dbo." + target + ".label = 'old'", StringComparison.Ordinal)
                })
                {
                    Assert.NotEqual(sql, invalid);
                    Assert.ThrowsAny<Exception>(() => SqlCoreFacade.CompileDml(
                        CoreSqlTextParser.ParseDml(invalid, SqlAgentToolType.MsSqlServer, profile),
                        SqlAgentToolType.MsSqlServer, context,
                        new DmlCompilationPolicy(), profile, null));
                }
            }
            // Full ordinary key-to-key equality is mandatory in JOIN ON.
            if (!separateSources)
            foreach (var invalid in new[]
            {
                sql.Replace(" AND s.k2 = j.j2", "", StringComparison.Ordinal),
                sql.Replace(" AND s.k2 = j.j2", " OR s.k2 = j.j2", StringComparison.Ordinal),
                sql.Replace("s.k2 = j.j2", "s.k2 + 0 = j.j2", StringComparison.Ordinal),
                sql.Replace("s.k2 = j.j2", "s.k1 = j.j2", StringComparison.Ordinal),
                sql.Replace("j.j2", "CAST(j.j2 AS BIGINT)", StringComparison.Ordinal)
            })
                Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(
                    CoreSqlTextParser.ParseDml(invalid, SqlAgentToolType.MsSqlServer, profile),
                    SqlAgentToolType.MsSqlServer, context,
                    new DmlCompilationPolicy(), profile, null));

            // Unverified second table cannot borrow primary table's key proof.
            var noSecondary = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, snapshot, "dbo." + target, new[] { "k1", "k2" },
                "dbo." + source, new[] { "k1", "k2" });
            Assert.Throws<SqlCompilationException>(() => SqlCoreFacade.CompileDml(
                parsed, SqlAgentToolType.MsSqlServer,
                new SqlPlanValidationContext("m07-no-join", allowed)
                    .WithSqlServerCteUniqueKeyAssurance(noSecondary),
                new DmlCompilationPolicy(), profile, null));

            var metadata = new Mock<IProviderMetadataReader>();
            metadata.Setup(x => x.GetSchemasAsync("m07-metadata", It.IsAny<CancellationToken>()))
                .ReturnsAsync(["dbo"]);
            metadata.Setup(x => x.GetTablesAsync("m07-metadata", "dbo",
                    It.IsAny<CancellationToken>())).ReturnsAsync([target]);
            metadata.Setup(x => x.GetColumnsAsync("m07-metadata", "dbo", target,
                    It.IsAny<CancellationToken>())).ReturnsAsync([
                        new DatabaseColumnMetadata("dbo", target, "k1", k1Type, true, 1),
                        new DatabaseColumnMetadata("dbo", target, "k2", k2Type, true, 2),
                        new DatabaseColumnMetadata("dbo", target, "label", "varchar", false)
                    ]);
            var plan = await new DmlPlanFactory(metadata.Object)
                .CreateWithMetadataConnectionAsync(db, "m07-metadata", parsed,
                    SqlAgentToolType.MsSqlServer,
                    new SqlPlanValidationContext("m07-cross-live", allowed),
                    new DmlCompilationPolicy(), DmlRowIdentityAssurance.Strict,
                    maxAffectedRows: 5, cancellationToken: ct, targetProfile: profile);
            var auto = Assert.IsType<SqlServerCteUniqueKeyAssurance>(
                plan.SqlServerCteUniqueKeyAssurance);
            Assert.Equal("dbo." + joined, auto.JoinedSourceTable);
            DmlCoordinator.ValidatePlan(plan);
            var coordinator = new DmlCoordinator(new SqlServerConnectionFactory());
            var version = db.ServerVersion.Trim();
            var preview = await coordinator.PreviewAsync(cs, plan, "m07-approver", ct, version);
            // Only one of the three target keys survives the verified
            // SECOND source's extra filter; this tests real deterministic
            // matching rather than a redundant syntactic row source.
            var expectedAffected = separateSources ? 1 : 3;
            Assert.Equal(expectedAffected, preview.AffectedRows);
            if (revokeJoinedIndex)
            {
                await Exec("DROP INDEX [" + index + "] ON dbo.[" + joined + "]");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    coordinator.CommitAsync(cs, plan, preview.Challenge,
                        "m07-approver", ct, version));
                await using var unchanged = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.[" + target + "] WHERE label = 'old'", db);
                Assert.Equal(3, Convert.ToInt32(await unchanged.ExecuteScalarAsync(ct)));
                await using var tx = (SqlTransaction)await db.BeginTransactionAsync(ct);
                Assert.False(proof.IsCurrent(db, tx));
                await tx.RollbackAsync(ct);
            }
            else
            {
                var result = await coordinator.CommitAsync(
                    cs, plan, preview.Challenge, "m07-approver", ct, version);
                Assert.True(result.Committed);
                await using var count = new SqlCommand(
                    "SELECT COUNT(*) FROM dbo.[" + target + "]", db);
                Assert.Equal(update ? 3 : 3 - expectedAffected,
                    Convert.ToInt32(await count.ExecuteScalarAsync(ct)));
                if (separateSources && update)
                {
                    await using var changed = new SqlCommand(
                        "SELECT COUNT(*) FROM dbo.[" + target + "] " +
                        "WHERE label = 'updated'", db);
                    Assert.Equal(1, Convert.ToInt32(await changed.ExecuteScalarAsync(ct)));
                }
            }
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + joined + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }


    [Theory]
    [InlineData("missing")]
    [InlineData("nonunique")]
    [InlineData("filtered")]
    [InlineData("disabled")]
    [InlineData("nullable-filtered")]
    [InlineData("nullable-nonunique")]
    [InlineData("nullable-disabled")]
    [InlineData("type-mismatch")]
    [InlineData("reversed")]
    [InlineData("computed")]
    public async Task CrossPhysicalUniqueProofRejectsUnverifiedSecondaryCatalog(string variant)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("M07 physical catalog tests require SQL Server.");
            Assert.Skip("Real SQL Server required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N")[..12];
        var target = "m07_dest_" + tag;
        var source = "m07_origin_" + tag;
        var joined = "m07_extra_" + tag;
        var index = "m07_idx_" + tag;
        await using var db = new SqlConnection(cs);
        for (var attempt = 0; ; attempt++)
        {
            try { await db.OpenAsync(ct); break; }
            catch (SqlException) when (attempt < 24 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        }
        async Task Exec(string statement)
        {
            await using var command = new SqlCommand(statement, db);
            await command.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await Exec("CREATE TABLE dbo.[" + target +
                "] (k1 INT NOT NULL, k2 BIGINT NOT NULL)");
            await Exec("CREATE TABLE dbo.[" + source +
                "] (k1 INT NOT NULL, k2 BIGINT NOT NULL, PRIMARY KEY (k1,k2))");
            var j2 = variant switch
            {
                "nullable-filtered" or "nullable-nonunique" or "nullable-disabled"
                    => "j2 BIGINT NULL",
                "type-mismatch" => "j2 INT NOT NULL",
                "computed" => "j2 AS CAST(j1 AS BIGINT) PERSISTED",
                _ => "j2 BIGINT NOT NULL"
            };
            await Exec("CREATE TABLE dbo.[" + joined + "] (j1 INT NOT NULL, " + j2 + ")");
            if (variant != "missing")
            {
                var unique = variant is "nonunique" or "nullable-nonunique" ? "" : "UNIQUE ";
                var columns = variant == "reversed" ? "(j2,j1)" : "(j1,j2)";
                var filter = variant switch {
                    "filtered" => " WHERE j2 > 0",
                    "nullable-filtered" => " WHERE j2 IS NOT NULL",
                    _ => ""
                };
                await Exec("CREATE " + unique + "INDEX [" + index +
                    "] ON dbo.[" + joined + "] " + columns + filter);
            }
            if (variant is "disabled" or "nullable-disabled")
                await Exec("ALTER INDEX [" + index + "] ON dbo.[" + joined + "] DISABLE");

            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer, "m07-negative", [
                SqlSchemaTable.WithTypes("dbo." + target, [
                    new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                    new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)]),
                SqlSchemaTable.WithTypes("dbo." + source, [
                    new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                    new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)]),
                SqlSchemaTable.WithTypes("dbo." + joined, [
                    new SqlSchemaColumn("j1", SqlSchemaValueKind.Numeric),
                    new SqlSchemaColumn("j2", SqlSchemaValueKind.Numeric)])
            ]);
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLiveForKeyPreservingJoin(
                    db, snapshot, "dbo." + target, new[] { "k1", "k2" },
                    "dbo." + source, new[] { "k1", "k2" },
                    "dbo." + joined, new[] { "j1", "j2" }));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + joined + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

}
