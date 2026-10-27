using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Catalog;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Script;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Real Firebird 5 DDL differential oracle. Static analysis runs before connecting
/// to the DB; only this integration harness executes SQL. Every statement's
/// CatalogAfter is compared with native RDB$ metadata. Per-case table/index
/// names are unique, and temporary schema objects are removed in finally.
/// </summary>
public sealed class SqlCoreFirebirdLiveDdlParityTests
{
    private sealed record Column(string Name, string Type, bool Nullable);
    private sealed record Table(string Name, Column[] Columns, string[] Indexes);
    private sealed record Case(string Name, string Template);

    private static readonly Case[] Scenarios =
    [
        new("create_add", "CREATE TABLE {T1}(ID INTEGER NOT NULL, CODE VARCHAR(32)); ALTER TABLE {T1} ADD QUANTITY INTEGER;"),
        new("create_drop_column", "CREATE TABLE {T1}(ID INTEGER, DETAILS VARCHAR(32)); ALTER TABLE {T1} DROP DETAILS;"),
        new("create_add_drop", "CREATE TABLE {T1}(ID INTEGER); ALTER TABLE {T1} ADD STATUS VARCHAR(32); ALTER TABLE {T1} DROP STATUS;"),
        new("two_tables", "CREATE TABLE {T1}(ID INTEGER); CREATE TABLE {T2}(ID INTEGER, PARENT_ID INTEGER);"),
        new("drop_secondary", "CREATE TABLE {T1}(ID INTEGER); CREATE TABLE {T2}(ID INTEGER); DROP TABLE {T2};"),
        new("drop_recreate", "CREATE TABLE {T1}(ID INTEGER); DROP TABLE {T1}; CREATE TABLE {T1}(LABEL VARCHAR(32) NOT NULL);"),
        new("index_kept", "CREATE TABLE {T1}(ID INTEGER, LABEL VARCHAR(32)); CREATE INDEX {I1} ON {T1}(LABEL);"),
        new("index_dropped", "CREATE TABLE {T1}(ID INTEGER, LABEL VARCHAR(32)); CREATE INDEX {I1} ON {T1}(LABEL); DROP INDEX {I1};"),
        new("two_indexes", "CREATE TABLE {T1}(ID INTEGER, LABEL VARCHAR(32), STATUS VARCHAR(32)); CREATE INDEX {I1} ON {T1}(LABEL); CREATE INDEX {I2} ON {T1}(STATUS);"),
        new("index_table_drop", "CREATE TABLE {T1}(ID INTEGER, LABEL VARCHAR(32)); CREATE INDEX {I1} ON {T1}(LABEL); DROP TABLE {T1};"),
        new("add_then_index", "CREATE TABLE {T1}(ID INTEGER); ALTER TABLE {T1} ADD LABEL VARCHAR(32); CREATE INDEX {I1} ON {T1}(LABEL);"),
        new("index_drop_then_column", "CREATE TABLE {T1}(ID INTEGER, LABEL VARCHAR(32)); CREATE INDEX {I1} ON {T1}(LABEL); DROP INDEX {I1}; ALTER TABLE {T1} DROP LABEL;"),
        new("create_drop_other", "CREATE TABLE {T1}(ID INTEGER); CREATE TABLE {T2}(ID INTEGER, CODE VARCHAR(32)); ALTER TABLE {T2} ADD QUANTITY SMALLINT; DROP TABLE {T1};"),
        new("nullable_shape", "CREATE TABLE {T1}(ID INTEGER NOT NULL, LABEL VARCHAR(32)); ALTER TABLE {T1} ADD QUANTITY SMALLINT;"),
    ];

    public static IEnumerable<object[]> Cases => Scenarios.Select(x => new object[] { x.Name });

    [Fact]
    public void AcceptanceCorpusContainsFourteenDistinctMultiStatementFirebirdWorkflows()
    {
        Assert.Equal(14, Scenarios.Length);
        Assert.Equal(14, Scenarios.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(14, Scenarios.Select(x => x.Template).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Scenarios, x => Assert.True(x.Template.Count(c => c == ';') >= 2, x.Name));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task FirebirdSystemCatalogMatchesEveryProvenVirtualCatalogTransition(string name)
    {
        var source = ConnectionString();
        var example = Scenarios.Single(x => x.Name == name);
        var prefix = "FBP6_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var sql = example.Template
            .Replace("{T1}", prefix + "_T1", StringComparison.Ordinal)
            .Replace("{T2}", prefix + "_T2", StringComparison.Ordinal)
            .Replace("{I1}", prefix + "_I1", StringComparison.Ordinal)
            .Replace("{I2}", prefix + "_I2", StringComparison.Ordinal);

        // IMPORTANT: no server access occurs while analyzing source.
        var analysis = SqlCoreFacade.AnalyzeScript(
            sql, SqlAgentToolType.Firebird, SqlCatalogState.KnownEmpty());
        Assert.True(analysis.Statements.Count >= 2, name);
        Assert.All(analysis.Statements, x => Assert.Equal(SqlStatementAnalysisStatus.Analyzed, x.Status));
        Assert.Empty(analysis.Diagnostics);

        await using var db = new FbConnection(source);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        try
        {
            for (var index = 0; index < analysis.Statements.Count; index++)
            {
                var statement = analysis.Statements[index];
                Assert.True(statement.CatalogAfter.IsClosedWorld, $"{name}#{index}");
                await Execute(db, statement.Syntax.Text);
                AssertSchemaEquals(name, index, statement.CatalogAfter, ReadSchema(db, prefix));
            }
            AssertSchemaEquals(name, analysis.Statements.Count,
                analysis.FinalCatalog, ReadSchema(db, prefix));
        }
        finally
        {
            // Firebird DDL cannot be assumed to have the same implicit-commit
            // or rollback semantics as any other source dialect. Explicitly
            // remove only this test's unique tables in reverse name order.
            foreach (var table in ReadSchema(db, prefix).OrderByDescending(x => x.Name, StringComparer.Ordinal))
                await Execute(db, "DROP TABLE \"" + table.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            Assert.Empty(ReadSchema(db, prefix));
        }
    }

    [Fact]
    public void UnmodeledCheckConstraintDoesNotGainFalseFirebirdCatalogProof()
    {
        var result = SqlCoreFacade.AnalyzeScript(
            "CREATE TABLE checked_table(ID INTEGER, CHECK(ID > 0));",
            SqlAgentToolType.Firebird, SqlCatalogState.KnownEmpty());
        Assert.Equal(SqlStatementAnalysisStatus.Unsupported,
            Assert.Single(result.Statements).Status);
        Assert.False(result.FinalCatalog.IsClosedWorld);
        Assert.Equal(SqlEffectCoverage.Unknown, result.EffectCoverage);
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("TEST_FIREBIRD_CONNECTION");
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Firebird live DDL parity requires TEST_FIREBIRD_CONNECTION in CI.");
        Assert.Skip("Firebird live DDL parity needs TEST_FIREBIRD_CONNECTION.");
        throw new InvalidOperationException("Test was skipped.");
    }

    private static async Task Execute(FbConnection connection, string sql)
    {
        using var command = new FbCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static IReadOnlyList<Table> ReadSchema(FbConnection connection, string prefix)
    {
        var names = new List<string>();
        using (var cmd = new FbCommand(
            "SELECT RDB$RELATION_NAME FROM RDB$RELATIONS " +
            "WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0 AND RDB$VIEW_BLR IS NULL " +
            "ORDER BY RDB$RELATION_NAME", connection))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                var tableName = reader.GetString(0).TrimEnd();
                if (tableName.StartsWith(prefix + "_", StringComparison.Ordinal))
                    names.Add(tableName);
            }

        var result = new List<Table>();
        foreach (var name in names)
        {
            var columns = new List<Column>();
            using (var cmd = new FbCommand(
                "SELECT TRIM(TRAILING FROM RF.RDB$FIELD_NAME), " +
                "F.RDB$FIELD_TYPE, F.RDB$CHARACTER_LENGTH, " +
                "COALESCE(RF.RDB$NULL_FLAG, F.RDB$NULL_FLAG, 0) " +
                "FROM RDB$RELATION_FIELDS RF " +
                "JOIN RDB$FIELDS F ON RF.RDB$FIELD_SOURCE = F.RDB$FIELD_NAME " +
                "WHERE RF.RDB$RELATION_NAME = @name ORDER BY RF.RDB$FIELD_POSITION", connection))
            {
                cmd.Parameters.AddWithValue("@name", name);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var fieldType = Convert.ToInt32(reader.GetValue(1));
                    var length = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                    var type = fieldType switch
                    {
                        7 => "SMALLINT",
                        8 => "INTEGER",
                        37 => "VARCHAR(" + length + ")",
                        _ => throw new InvalidOperationException(
                            $"Unexpected Firebird field type {fieldType} in proven test scenario.")
                    };
                    columns.Add(new Column(reader.GetString(0).TrimEnd(), type,
                        Convert.ToInt32(reader.GetValue(3)) != 1));
                }
            }

            var indexes = new List<string>();
            using (var cmd = new FbCommand(
                "SELECT TRIM(TRAILING FROM RDB$INDEX_NAME) FROM RDB$INDICES " +
                "WHERE RDB$RELATION_NAME = @name AND COALESCE(RDB$SYSTEM_FLAG, 0) = 0 " +
                "ORDER BY RDB$INDEX_NAME", connection))
            {
                cmd.Parameters.AddWithValue("@name", name);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) indexes.Add(reader.GetString(0).TrimEnd());
            }
            result.Add(new Table(name, columns.ToArray(), indexes.ToArray()));
        }
        return result;
    }

    private static void AssertSchemaEquals(string scenario, int index,
        SqlCatalogState model, IReadOnlyList<Table> live)
    {
        Assert.True(model.IsClosedWorld, $"{scenario}#{index}: unknown virtual catalog");
        Assert.Equal(model.Tables.Count, live.Count);
        foreach (var table in model.Tables)
        {
            Assert.Equal(SqlCatalogKnowledge.Known, table.Knowledge);
            var physical = Assert.Single(live, x => string.Equals(
                x.Name, table.Name, StringComparison.Ordinal));
            Assert.Equal(table.Columns.Count, physical.Columns.Length);
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var expected = table.Columns[i];
                var actual = physical.Columns[i];
                Assert.Equal(expected.Name, actual.Name);
                Assert.True(string.Equals(expected.TypeName, actual.Type,
                    StringComparison.OrdinalIgnoreCase),
                    $"{scenario}#{index}/{table.Name}.{expected.Name}: {expected.TypeName} != {actual.Type}");
                Assert.Equal(expected.Nullable, actual.Nullable);
            }
            var modeledIndexes = table.Indexes.Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();
            var actualIndexes = physical.Indexes.Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(modeledIndexes, actualIndexes);
        }
    }
}
