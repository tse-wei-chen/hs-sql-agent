using HsSqlAgent.SqlCore.Core.Pipeline;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// F# Strict Snapshot proves only the bounded source column shape. It does
/// not assert a real UNIQUE index; only the live Host can mint that proof.
/// </summary>
public sealed class CoreNativeStrictSnapshotMultiColumnConflictUpdateTests
{
    private static readonly string[] Values = ["name", "city", "region", "code", "extra"];

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PostgresStrictSnapshot_BoundedMultiColumnUpdate_Compiles(int count)
    {
        var columns = Values.Take(count).ToArray();
        var sql = BuildSql(columns);
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        var compiled = CoreDmlCompiler.CreateDefault().Compile(
            parsed, SqlAgentToolType.Postgres, Context(columns));

        Assert.Equal(SqlStatementKind.Insert, compiled.Kind);
        Assert.Contains("DO UPDATE SET", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        foreach (var column in columns)
            Assert.Contains("EXCLUDED.\"" + column + "\"", compiled.Sql,
                StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(5)]
    public void PostgresStrictSnapshot_ExcessiveUpdateColumnCount_RemainsFailClosed(int count)
    {
        var columns = Values.Take(count).ToArray();
        var parsed = CoreSqlTextParser.ParseDml(BuildSql(columns), SqlAgentToolType.Postgres);
        Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(parsed,
                SqlAgentToolType.Postgres, Context(columns)));
    }

    [Theory]
    [InlineData("name=excluded.name")]
    [InlineData("name=excluded.name, city=excluded.name")]
    [InlineData("name=excluded.name, city='literal'")]
    [InlineData("id=excluded.id, city=excluded.city")]
    public void PostgresStrictSnapshot_IncompleteOrMismatchedUpdate_Denies(string assignments)
    {
        var sql = "INSERT INTO users (id,name,city) VALUES (7,'n','c') " +
            "ON CONFLICT (id) DO UPDATE SET " + assignments;
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        Assert.Throws<SqlCompilationException>(() =>
            CoreDmlCompiler.CreateDefault().Compile(parsed,
                SqlAgentToolType.Postgres, Context(["name", "city"])));
    }

    private static string BuildSql(string[] columns) =>
        "INSERT INTO users (id," + string.Join(",", columns) + ") VALUES (7," +
        string.Join(",", columns.Select(c => "'new-" + c + "'")) +
        ") ON CONFLICT (id) DO UPDATE SET " +
        string.Join(", ", columns.Reverse().Select(c => c + "=excluded." + c));

    private static SqlPlanValidationContext Context(IReadOnlyList<string> columns) =>
        new SqlPlanValidationContext("m12-fsharp-bounded-multi-update").WithSchemaSnapshot(
            new SqlSchemaSnapshot(SqlAgentToolType.Postgres, "m12-virtual-no-key-proof", [
                new SqlSchemaTable("users", columns.Prepend("id").ToArray())
            ]));
}
