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
    // G9/M04: distinct signed 16-bit and unsigned 8-bit physical SQL key kinds.
    // All existing approval, cardinality, index drift and CAST rejection
    // assertions apply to these exact key types too.
    [InlineData(true, true, 1, false, false, "NONE", "SMALLINT", "SMALLINT")]
    [InlineData(false, false, 2, false, true, "NONE", "TINYINT", "TINYINT")]
    [InlineData(true, false, 3, false, true, "SELF", "SMALLINT", "TINYINT")]
    [InlineData(false, true, 2, false, false, "SELF_LEFT", "TINYINT", "SMALLINT")]
    [InlineData(true, false, 1, true, false, "NONE", "TINYINT", "INT")]
    [InlineData(false, false, 3, false, true, "INNER", "SMALLINT", "BIGINT")]
    [InlineData(true, true, 2, false, false, "CROSS", "BIGINT", "TINYINT")]
    [InlineData(false, false, 1, false, true, "LEFT", "INT", "SMALLINT")]
    // G9/M04: fixed-point physical type equality requires both p and s.
    [InlineData(true, true, 1, false, false, "NONE", "DECIMAL(12,2)", "DECIMAL(18,4)")]
    [InlineData(false, false, 2, false, true, "NONE", "NUMERIC(12,2)", "DECIMAL(18,4)")]
    [InlineData(true, false, 3, false, true, "SELF", "SMALLINT", "DECIMAL(20,0)")]
    [InlineData(false, true, 2, false, false, "SELF_LEFT", "DECIMAL(18,4)", "BIGINT")]
    [InlineData(true, false, 1, true, true, "NONE", "DECIMAL(12,2)", "INT")]
    [InlineData(false, false, 3, false, true, "INNER", "DECIMAL(18,4)", "DECIMAL(12,2)")]
    [InlineData(true, false, 2, false, false, "CROSS", "DECIMAL(38,0)", "DECIMAL(38,18)")]
    // G9/M04: UUID belongs to an exact non-numeric physical key family.
    // Positive 1-3 CTE, self-join, 2-column mixed, approval and index drift.
    [InlineData(true, true, 1, false, false, "NONE", "UNIQUEIDENTIFIER", "UNIQUEIDENTIFIER")]
    [InlineData(false, false, 2, false, true, "NONE", "UNIQUEIDENTIFIER", "INT")]
    [InlineData(true, false, 3, false, true, "SELF", "INT", "UNIQUEIDENTIFIER")]
    [InlineData(false, false, 2, false, false, "SELF_LEFT", "DECIMAL(12,2)", "UNIQUEIDENTIFIER")]
    [InlineData(true, false, 1, true, false, "NONE", "UNIQUEIDENTIFIER", "BIGINT")]
    [InlineData(false, true, 3, false, true, "INNER", "UNIQUEIDENTIFIER", "SMALLINT")]
    [InlineData(true, true, 2, false, false, "CROSS", "UNIQUEIDENTIFIER", "UNIQUEIDENTIFIER")]
    // G9/M04 Text: fixed VARCHAR/NVARCHAR under verified BIN2 collation.
    [InlineData(true, true, 1, false, false, "NONE", "VARCHAR(24)", "VARCHAR(24)")]
    [InlineData(false, false, 2, false, true, "NONE", "NVARCHAR(32)", "NVARCHAR(32)")]
    [InlineData(true, false, 3, false, true, "SELF", "VARCHAR(32)", "INT")]
    [InlineData(false, true, 2, false, false, "SELF_LEFT", "UNIQUEIDENTIFIER", "NVARCHAR(32)")]
    [InlineData(true, false, 1, true, true, "NONE", "NVARCHAR(32)", "DECIMAL(12,2)")]
    [InlineData(false, false, 3, false, true, "INNER", "VARCHAR(24)", "NVARCHAR(32)")]
    [InlineData(true, true, 2, false, false, "CROSS", "NVARCHAR(32)", "SMALLINT")]
    // M05: linguistic equality must use the *same physical collation* as
    // the source UNIQUE index, not a case-sensitive binary approximation.
    [InlineData(true, true, 1, false, false, "NONE", "VARCHAR(24) COLLATE Latin1_General_100_CI_AS", "INT")]
    [InlineData(false, false, 2, false, true, "NONE", "NVARCHAR(32) COLLATE Latin1_General_100_CI_AI", "DECIMAL(12,2)")]
    [InlineData(true, false, 3, false, true, "SELF", "VARCHAR(24) COLLATE Latin1_General_100_CS_AS", "UNIQUEIDENTIFIER")]
    [InlineData(false, true, 2, false, false, "SELF_LEFT", "INT", "NVARCHAR(32) COLLATE Latin1_General_100_CI_AS")]
    [InlineData(true, false, 1, true, true, "NONE", "VARCHAR(24) COLLATE Latin1_General_100_CI_AI", "BIGINT")]
    [InlineData(false, false, 3, false, true, "INNER", "NVARCHAR(32) COLLATE Latin1_General_100_CS_AS", "VARCHAR(24) COLLATE Latin1_General_100_CI_AS")]
    [InlineData(true, true, 2, false, false, "CROSS", "NVARCHAR(32) COLLATE Latin1_General_100_CI_AI", "NVARCHAR(32) COLLATE Latin1_General_100_CI_AI")]
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
            static bool IsText(string type) =>
                type.StartsWith("VARCHAR(", StringComparison.Ordinal)
                || type.StartsWith("NVARCHAR(", StringComparison.Ordinal);
            string PhysicalType(string type) => IsText(type)
                ? (type.Contains(" COLLATE ", StringComparison.Ordinal) ? type
                    : type + " COLLATE Latin1_General_100_BIN2")
                : type;
            await Exec("CREATE TABLE dbo.[" + dst +
                "] (k1 " + PhysicalType(k1Type) + " NOT NULL, k2 " + PhysicalType(k2Type)
                + " NOT NULL, label VARCHAR(30), PRIMARY KEY (k1,k2))");
            await Exec("CREATE TABLE dbo.[" + src +
                "] (k1 " + PhysicalType(k1Type) + " NOT NULL, k2 " + PhysicalType(k2Type)
                + " NOT NULL, payload INT NOT NULL" +
                (primaryKey ? ", PRIMARY KEY (k1,k2)" : "") + ")");
            if (!primaryKey)
                await Exec("CREATE UNIQUE INDEX [" + idx + "] ON dbo.[" + src + "] (k1,k2)");
            // Actual 64-bit values distinguish BIGINT from ordinary INT.
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

            await Exec("INSERT INTO dbo.[" + dst +
                $"] (k1,k2,label) VALUES ({key1},{key2},'old'),({key1},{key2Next},'old'),({key1Next},{key2},'old')");
            await Exec("INSERT INTO dbo.[" + src +
                $"] (k1,k2,payload) VALUES ({key1},{key2},11),({key1},{key2Next},12),({key1Next},{key2},21)");

            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
                "g7-composite-live-v1", [
                    SqlSchemaTable.WithTypes("dbo." + dst, [
                        new SqlSchemaColumn("k1", k1Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k1Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric)),
                        new SqlSchemaColumn("k2", k2Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k2Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric))]),
                    SqlSchemaTable.WithTypes("dbo." + src, [
                        new SqlSchemaColumn("k1", k1Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k1Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric)),
                        new SqlSchemaColumn("k2", k2Type == "UNIQUEIDENTIFIER"
                            ? SqlSchemaValueKind.Uuid : (IsText(k2Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric))])
                ]);
            var target = "dbo." + dst;
            var source = "dbo." + src;
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, snapshot, target, new[] { "k1", "k2" },
                source, new[] { "k1", "k2" });
            Assert.Equal(new[] { "k1", "k2" }, proof.SourceKeyColumns);
            Assert.Equal(new[] { "k1", "k2" }, proof.TargetKeyColumns);
            if (k1Type == "UNIQUEIDENTIFIER" || k2Type == "UNIQUEIDENTIFIER")
            {
                // Numeric is not a UUID witness, even when every live
                // source UNIQUE key has otherwise been correctly attested.
                var wrongTypedSnapshot = new SqlSchemaSnapshot(
                    SqlAgentToolType.MsSqlServer, "g9-wrong-uuid-family", [
                        SqlSchemaTable.WithTypes(target, [
                            new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                            new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)]),
                        SqlSchemaTable.WithTypes(source, [
                            new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                            new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)])
                    ]);
                Assert.Throws<ArgumentException>(() =>
                    SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                        db, wrongTypedSnapshot, target, new[] { "k1", "k2" },
                        source, new[] { "k1", "k2" }));
                // Correct source UUID family is still not permission to
                // misdeclare the target UUID as Numeric.
                var wrongTargetSnapshot = new SqlSchemaSnapshot(
                    SqlAgentToolType.MsSqlServer, "g9-wrong-target-family", [
                        SqlSchemaTable.WithTypes(target, [
                            new SqlSchemaColumn("k1", SqlSchemaValueKind.Numeric),
                            new SqlSchemaColumn("k2", SqlSchemaValueKind.Numeric)]),
                        SqlSchemaTable.WithTypes(source, [
                            new SqlSchemaColumn("k1", k1Type == "UNIQUEIDENTIFIER"
                                ? SqlSchemaValueKind.Uuid : (IsText(k1Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric)),
                            new SqlSchemaColumn("k2", k2Type == "UNIQUEIDENTIFIER"
                                ? SqlSchemaValueKind.Uuid : (IsText(k2Type) ? SqlSchemaValueKind.Text : SqlSchemaValueKind.Numeric))])
                    ]);
                Assert.Throws<ArgumentException>(() =>
                    SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                        db, wrongTargetSnapshot, target, new[] { "k1", "k2" },
                        source, new[] { "k1", "k2" }));
            }
            Assert.Contains("k1", proof.ProofIdentity);
            Assert.Contains("k2", proof.ProofIdentity);
            var exactTypes = (k1Type + "," + k2Type)
                .Replace("NUMERIC(", "DECIMAL(", StringComparison.Ordinal)
                .Replace(" COLLATE ", "@", StringComparison.Ordinal);
            Assert.Contains("source-key-types=" + exactTypes,
                proof.ProofIdentity, StringComparison.Ordinal);
            Assert.Contains("target-key-types=" + exactTypes,
                proof.ProofIdentity, StringComparison.Ordinal);

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
            if (IsText(k1Type) || IsText(k2Type))
            {
                var forcedCollation = sql.Replace(
                    " WHERE dbo." + dst + ".k1 = " + keyAlias + ".a",
                    " WHERE dbo." + dst +
                        ".k1 COLLATE Latin1_General_100_BIN2 = " + keyAlias + ".a",
                    StringComparison.Ordinal);
                Assert.ThrowsAny<Exception>(() =>
                    SqlCoreFacade.CompileDml(
                        CoreSqlTextParser.ParseDml(forcedCollation,
                            SqlAgentToolType.MsSqlServer, profile),
                        SqlAgentToolType.MsSqlServer, context,
                        new DmlCompilationPolicy(), profile, null));
            }
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
    [InlineData("nullable-filtered")]
    [InlineData("nullable-nonunique")]
    [InlineData("nullable-disabled")]
    // Opposite-side BIGINT without exact counterpart stays denied.
    [InlineData("source-bigint")]
    [InlineData("target-bigint")]
    [InlineData("source-smallint")]
    [InlineData("source-tinyint")]
    [InlineData("source-decimal")]
    [InlineData("source-float")]
    [InlineData("source-uuid")]
    [InlineData("target-uuid")]
    [InlineData("decimal-precision-mismatch")]
    [InlineData("decimal-scale-mismatch")]
    [InlineData("numeric-vs-int")]
    [InlineData("decimal-vs-bigint")]
    [InlineData("uuid-vs-int")]
    [InlineData("uuid-vs-varchar")]
    [InlineData("uuid-vs-binary")]
    [InlineData("target-decimal")]
    [InlineData("target-smallint")]
    [InlineData("target-tinyint")]
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
                (shape switch
                {
                    "target-bigint" => "BIGINT",
                    "target-decimal" => "DECIMAL(20,0)",
                    "target-smallint" => "SMALLINT",
                    "target-tinyint" => "TINYINT",
                    "target-uuid" => "UNIQUEIDENTIFIER",
                    "decimal-precision-mismatch" => "DECIMAL(12,2)",
                    "decimal-scale-mismatch" => "DECIMAL(12,2)",
                    "decimal-vs-bigint" => "BIGINT",
                    "uuid-vs-int" => "INT",
                    "uuid-vs-varchar" => "VARCHAR(36)",
                    "uuid-vs-binary" => "BINARY(16)",
                    _ => "INT"
                }) + " NOT NULL)");
            var sourceDefinition = shape switch
            {
                "nullable-filtered" => "k1 INT NOT NULL, k2 INT NULL",
                "nullable-nonunique" => "k1 INT NOT NULL, k2 INT NULL",
                "nullable-disabled" => "k1 INT NOT NULL, k2 INT NULL",
                "source-bigint" => "k1 INT NOT NULL, k2 BIGINT NOT NULL",
                "source-smallint" => "k1 INT NOT NULL, k2 SMALLINT NOT NULL",
                "source-tinyint" => "k1 INT NOT NULL, k2 TINYINT NOT NULL",
                "source-decimal" => "k1 INT NOT NULL, k2 DECIMAL(20,0) NOT NULL",
                "decimal-precision-mismatch" => "k1 INT NOT NULL, k2 DECIMAL(13,2) NOT NULL",
                "decimal-scale-mismatch" => "k1 INT NOT NULL, k2 DECIMAL(12,3) NOT NULL",
                "numeric-vs-int" => "k1 INT NOT NULL, k2 NUMERIC(12,2) NOT NULL",
                "decimal-vs-bigint" => "k1 INT NOT NULL, k2 DECIMAL(20,0) NOT NULL",
                "source-float" => "k1 INT NOT NULL, k2 FLOAT NOT NULL",
                "source-uuid" => "k1 INT NOT NULL, k2 UNIQUEIDENTIFIER NOT NULL",
                "uuid-vs-int" => "k1 INT NOT NULL, k2 UNIQUEIDENTIFIER NOT NULL",
                "uuid-vs-varchar" => "k1 INT NOT NULL, k2 UNIQUEIDENTIFIER NOT NULL",
                "uuid-vs-binary" => "k1 INT NOT NULL, k2 UNIQUEIDENTIFIER NOT NULL",
                "computed" => "k1 INT NOT NULL, k2 AS (k1 + 1) PERSISTED",
                _ => "k1 INT NOT NULL, k2 INT NOT NULL"
            };
            await Exec("CREATE TABLE dbo.[" + src + "] (" + sourceDefinition + ")");
            if (shape != "missing")
            {
                var unique = shape is "nonunique" or "nullable-nonunique" ? "" : "UNIQUE ";
                var predicate = shape switch {
                    "filtered" => " WHERE k2 > 0",
                    "nullable-filtered" => " WHERE k2 IS NOT NULL",
                    _ => ""
                };
                await Exec("CREATE " + unique + "INDEX [" + idx + "] ON dbo.[" +
                    src + "] (k1, k2)" + predicate);
            }
            if (shape is "disabled" or "nullable-disabled")
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


    [Fact]
    public async Task UniqueIndexWithUnprovenAliasKeyCannotMasqueradeAsSingleColumnUnique()
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("SQL Server physical alias type proof requires a real database.");
            Assert.Skip("Real SQL Server required.");
        }

        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N")[..12];
        var target = "g9_aliasdst_" + tag;
        var source = "g9_aliassrc_" + tag;
        var alias = "g9_aliastype_" + tag;
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
            await Exec("CREATE TYPE dbo.[" + alias + "] FROM DECIMAL(12,2) NOT NULL");
            await Exec("CREATE TABLE dbo.[" + target + "] (id INT NOT NULL)");
            await Exec("CREATE TABLE dbo.[" + source +
                "] (id INT NOT NULL, extra dbo.[" + alias + "] NOT NULL)");
            // UNIQUE(id,extra) does NOT establish UNIQUE(id). Dropping the
            // unsupported alias column while collecting catalog rows would
            // falsely make a one-column proof from this composite index.
            await Exec("CREATE UNIQUE INDEX [uq_" + tag + "] ON dbo.[" +
                source + "] (id,extra)");
            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
                "g9-alias-index-completeness", [
                    SqlSchemaTable.WithTypes("dbo." + target,
                        [new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric)]),
                    SqlSchemaTable.WithTypes("dbo." + source,
                        [new SqlSchemaColumn("id", SqlSchemaValueKind.Numeric)])
                ]);
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    db, snapshot, "dbo." + target, "id", "dbo." + source, "id"));
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
            await Exec("DROP TYPE IF EXISTS dbo.[" + alias + "]");
        }
    }



    // Compare SQL Server's actual index conflicts against native column =
    // rather than assuming CI/AI suffixes describe the engine correctly.
    [Theory]
    [InlineData("Latin1_General_100_CI_AS", "VARCHAR(24)", true, false)]
    [InlineData("Latin1_General_100_CI_AI", "NVARCHAR(24)", true, true)]
    [InlineData("Latin1_General_100_CS_AS", "VARCHAR(24)", false, false)]
    [InlineData("Latin1_General_100_CS_AS", "NVARCHAR(24)", false, false)]
    public async Task LinguisticUniqueAndNativeEqualityMustAgree(
        string collation, string type, bool caseEqual, bool accentEqual)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("M05 collation comparison parity needs real SQL Server.");
            Assert.Skip("Real SQL Server 2022 required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..12];
        var source = "m05_cmp_src_" + id;
        var target = "m05_cmp_dst_" + id;
        var idx = "m05_cmp_uq_" + id;
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
        async Task<bool> IsDuplicate(string literal)
        {
            try
            {
                await Exec("INSERT INTO dbo.[" + source + "] (k) VALUES (" + literal + ")");
                return false;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                return true;
            }
        }
        try
        {
            await Exec("CREATE TABLE dbo.[" + source + "] (k " + type +
                " COLLATE " + collation + " NOT NULL)");
            await Exec("CREATE UNIQUE INDEX [" + idx + "] ON dbo.[" + source + "] (k)");
            await Exec("CREATE TABLE dbo.[" + target + "] (k " + type +
                " COLLATE " + collation + " NOT NULL)");
            await Exec("INSERT INTO dbo.[" + source + "] (k) VALUES (N'CaseA'),(N'Café')");
            Assert.Equal(caseEqual, await IsDuplicate("N'casea'"));
            if (!caseEqual) await Exec("DELETE FROM dbo.[" + source + "] WHERE k = N'casea'");
            Assert.Equal(accentEqual, await IsDuplicate("N'Cafe'"));
            if (!accentEqual) await Exec("DELETE FROM dbo.[" + source + "] WHERE k = N'Cafe'");
            await Exec("INSERT INTO dbo.[" + target + "] (k) VALUES (N'casea'),(N'Cafe')");
            await using (var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.[" + source + "] AS s INNER JOIN dbo.[" +
                target + "] AS t ON s.k = t.k", db))
            {
                var actual = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                Assert.Equal((caseEqual ? 1 : 0) + (accentEqual ? 1 : 0), actual);
            }

            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
                "m05-collation-comparison", [
                    SqlSchemaTable.WithTypes("dbo." + target,
                        [new SqlSchemaColumn("k", SqlSchemaValueKind.Text)]),
                    SqlSchemaTable.WithTypes("dbo." + source,
                        [new SqlSchemaColumn("k", SqlSchemaValueKind.Text)])
                ]);
            var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                db, snapshot, "dbo." + target, "k", "dbo." + source, "k");
            Assert.Contains("@" + collation, proof.ProofIdentity, StringComparison.Ordinal);
            await using (var tx = (SqlTransaction)await db.BeginTransactionAsync(ct))
            {
                Assert.True(proof.IsCurrent(db, tx));
                await tx.RollbackAsync(ct);
            }
            // A collation change after preview must invalidate the same proof.
            await Exec("ALTER TABLE dbo.[" + target + "] ALTER COLUMN k " + type +
                " COLLATE Latin1_General_100_BIN2 NOT NULL");
            await using var staleTx = (SqlTransaction)await db.BeginTransactionAsync(ct);
            Assert.False(proof.IsCurrent(db, staleTx));
            await staleTx.RollbackAsync(ct);
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + source + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + target + "]");
        }
    }

    // Exact native source UNIQUE and target equality comparison must be
    // proof-equivalent. A generic Text schema kind is never sufficient.
    [Theory]
    [InlineData("source-ci")]
    [InlineData("target-ci")]
    [InlineData("different-bin2")]
    [InlineData("source-ci-ai")]
    [InlineData("target-cs")]
    [InlineData("different-linguistic")]
    [InlineData("unsupported-cs-ai")]
    [InlineData("unsupported-sc")]
    [InlineData("different-length")]
    [InlineData("unicode-varchar")]
    [InlineData("varchar-max-target")]
    [InlineData("char-not-proven")]
    [InlineData("source-nullable-filtered")]
    [InlineData("wrong-source-snapshot")]
    [InlineData("wrong-target-snapshot")]
    [InlineData("filtered-index")]
    [InlineData("disabled-index")]
    public async Task TextPhysicalEqualityRequiresSameBuiltInLengthAndBinaryCollation(string variant)
    {
        var cs = Environment.GetEnvironmentVariable("TEST_SQLSERVER_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs))
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("Text BIN2 live proof requires TEST_SQLSERVER_CONNECTION.");
            Assert.Skip("Real SQL Server 2022 is required.");
        }
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..12];
        var src = "g9_txtsrc_" + id;
        var dst = "g9_txtdst_" + id;
        var index = "g9_txtuq_" + id;
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
        const string bin2 = "Latin1_General_100_BIN2";
        const string ci = "Latin1_General_100_CI_AS";
        var sourceType = variant == "char-not-proven" ? "CHAR(24)" : "VARCHAR(24)";
        var targetType = variant switch
        {
            "char-not-proven" => "CHAR(24)",
            "different-length" => "VARCHAR(32)",
            "unicode-varchar" => "NVARCHAR(24)",
            "varchar-max-target" => "VARCHAR(MAX)",
            _ => "VARCHAR(24)"
        };
        var sourceCollation = variant switch
        {
            "source-ci" or "different-linguistic" => ci,
            "source-ci-ai" => "Latin1_General_100_CI_AI",
            "target-cs" => "Latin1_General_100_CS_AS",
            "unsupported-cs-ai" => "Latin1_General_100_CS_AI",
            "unsupported-sc" => "Latin1_General_100_CI_AS_SC",
            _ => bin2
        };
        var targetCollation = variant switch
        {
            "target-ci" => ci,
            "different-bin2" => "Latin1_General_BIN2",
            "different-linguistic" => "Latin1_General_100_CI_AI",
            "target-cs" => ci,
            _ => bin2
        };
        try
        {
            await Exec("CREATE TABLE dbo.[" + dst + "] (k " + targetType +
                " COLLATE " + targetCollation + " NOT NULL, label VARCHAR(20))");
            await Exec("CREATE TABLE dbo.[" + src + "] (k " + sourceType +
                " COLLATE " + sourceCollation +
                (variant == "source-nullable-filtered" ? " NULL)" : " NOT NULL)"));
            await Exec("CREATE UNIQUE INDEX [" + index + "] ON dbo.[" + src + "] (k)" +
                (variant is "filtered-index" or "source-nullable-filtered" ? " WHERE k IS NOT NULL" : ""));
            if (variant == "disabled-index")
                await Exec("ALTER INDEX [" + index + "] ON dbo.[" + src + "] DISABLE");
            var sourceKind = variant == "wrong-source-snapshot"
                ? SqlSchemaValueKind.Numeric : SqlSchemaValueKind.Text;
            var targetKind = variant == "wrong-target-snapshot"
                ? SqlSchemaValueKind.Numeric : SqlSchemaValueKind.Text;
            var snapshot = new SqlSchemaSnapshot(SqlAgentToolType.MsSqlServer,
                "g9-text-" + variant, [
                    SqlSchemaTable.WithTypes("dbo." + dst,
                        [new SqlSchemaColumn("k", targetKind)]),
                    SqlSchemaTable.WithTypes("dbo." + src,
                        [new SqlSchemaColumn("k", sourceKind)])
                ]);
            var error = Record.Exception(() =>
                SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    db, snapshot, "dbo." + dst, "k", "dbo." + src, "k"));
            Assert.NotNull(error);
            Assert.True(error is ArgumentException or InvalidOperationException,
                "Unsupported text proof must fail closed at the domain gate: " + error);
        }
        finally
        {
            await Exec("DROP TABLE IF EXISTS dbo.[" + src + "]");
            await Exec("DROP TABLE IF EXISTS dbo.[" + dst + "]");
        }
    }


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
    [InlineData(true, false, 1, false, "INT", "BIGINT")]
    [InlineData(false, false, 1, false, "INT", "BIGINT")]
    [InlineData(true, true, 2, false, "INT", "BIGINT")]
    [InlineData(false, true, 3, false, "INT", "BIGINT")]
    [InlineData(true, false, 2, true, "INT", "BIGINT")]
    [InlineData(false, true, 1, true, "INT", "BIGINT")]
    [InlineData(true, false, 1, false, "SMALLINT", "TINYINT")]
    [InlineData(false, true, 2, false, "TINYINT", "SMALLINT")]
    [InlineData(true, true, 3, true, "SMALLINT", "INT")]
    [InlineData(true, false, 1, false, "DECIMAL(12,2)", "DECIMAL(18,4)")]
    [InlineData(false, true, 2, false, "NUMERIC(12,2)", "DECIMAL(18,4)")]
    [InlineData(true, true, 3, true, "DECIMAL(12,2)", "DECIMAL(20,0)")]
    [InlineData(true, false, 1, false, "UNIQUEIDENTIFIER", "INT")]
    [InlineData(false, true, 2, false, "SMALLINT", "UNIQUEIDENTIFIER")]
    [InlineData(true, true, 3, true, "UNIQUEIDENTIFIER", "DECIMAL(12,2)")]
    [InlineData(true, false, 1, false, "VARCHAR(24)", "INT")]
    [InlineData(false, true, 2, false, "NVARCHAR(32)", "VARCHAR(24)")]
    [InlineData(true, true, 3, true, "NVARCHAR(32)", "DECIMAL(12,2)")]
    [InlineData(true, false, 1, false, "VARCHAR(24) COLLATE Latin1_General_100_CI_AS", "INT")]
    [InlineData(false, true, 2, false, "NVARCHAR(32) COLLATE Latin1_General_100_CI_AI", "VARCHAR(24) COLLATE Latin1_General_100_CI_AI")]
    [InlineData(true, true, 3, true, "NVARCHAR(32) COLLATE Latin1_General_100_CS_AS", "DECIMAL(12,2)")]
    public async Task CrossPhysicalUniqueJoinRequiresBothLiveIndexesAndCommitFreshness(
        bool update, bool leftJoin, int hops, bool revokeJoinedIndex,
        string k1Type, string k2Type)
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

            var prefix = "WITH seed AS (SELECT s.k1 AS a, s.k2 AS b FROM dbo.[" +
                source + "] AS s " + (leftJoin ? "LEFT" : "INNER") +
                " JOIN dbo.[" + joined + "] AS j ON s.k1 = j.j1 AND s.k2 = j.j2) ";
            if (hops >= 2) prefix += ", picked AS (SELECT a, b FROM seed) ";
            if (hops >= 3) prefix += ", final AS (SELECT a, b FROM picked) ";
            var cte = hops switch { 1 => "seed", 2 => "picked", 3 => "final", _ => "?" };
            var sql = prefix + (update
                ? "UPDATE dbo.[" + target + "] SET label = 'updated' FROM " + cte
                : "DELETE FROM dbo.[" + target + "] FROM " + cte)
                + " WHERE dbo." + target + ".k1 = " + cte + ".a"
                + " AND dbo." + target + ".k2 = " + cte + ".b";
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.MsSqlServer, profile);
            var compiled = SqlCoreFacade.CompileDml(parsed, SqlAgentToolType.MsSqlServer,
                context, new DmlCompilationPolicy(), profile, null);
            Assert.StartsWith("WITH ", compiled.Sql, StringComparison.OrdinalIgnoreCase);

            // Full ordinary key-to-key equality is mandatory in JOIN ON.
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
            Assert.Equal(3, preview.AffectedRows);
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
                Assert.Equal(update ? 3 : 0, Convert.ToInt32(await count.ExecuteScalarAsync(ct)));
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
