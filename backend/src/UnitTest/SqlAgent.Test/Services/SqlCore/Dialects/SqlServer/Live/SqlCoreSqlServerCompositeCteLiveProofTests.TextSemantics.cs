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


}
