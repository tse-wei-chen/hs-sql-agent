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



}
