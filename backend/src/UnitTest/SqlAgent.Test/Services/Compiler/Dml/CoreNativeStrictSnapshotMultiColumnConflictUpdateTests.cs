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
    [InlineData("NULL", "NULL")]
    [InlineData("'new-name'", "42")]
    [InlineData("NULL", "-32768")]
    [InlineData("'new-name'", "9223372036854775807")]
    public void PostgresStrictSnapshot_TypedAndNullableProposals_RetainClosedShape(
        string nameValue, string integerValue)
    {
        // The virtual F# catalog establishes declared columns and grammar
        // only. The Host must separately attest PostgreSQL physical data types,
        // nullability and exact signed integer width before approval.
        var sql = "INSERT INTO users (id,name,amount) VALUES (7," +
            nameValue + "," + integerValue + ") ON CONFLICT (id) " +
            "DO UPDATE SET amount=excluded.amount, name=excluded.name";
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        var compiled = CoreDmlCompiler.CreateDefault().Compile(
            parsed, SqlAgentToolType.Postgres, Context(["name", "amount"]));
        Assert.Equal(SqlStatementKind.Insert, compiled.Kind);
        Assert.Contains("DO UPDATE SET", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("RETURNING id")]
    [InlineData("RETURNING city, id")]
    [InlineData("RETURNING id, name, city")]
    public void PostgresStrictSnapshot_ReturnsOnlyDirectApprovedColumns(string returning)
    {
        var sql = BuildSql(["name", "city"]) + " " + returning;
        var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
        var compiled = CoreDmlCompiler.CreateDefault().Compile(parsed,
            SqlAgentToolType.Postgres, Context(["name", "city"]));
        Assert.Equal(SqlStatementKind.Insert, compiled.Kind);
        Assert.True(compiled.ReturnsRows);
        Assert.Contains("RETURNING", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("RETURNING *")]
    [InlineData("RETURNING id,id")]
    [InlineData("RETURNING id, upper(name)")]
    [InlineData("RETURNING id AS alias_id")]
    [InlineData("RETURNING xmax")]
    [InlineData("RETURNING id, ctid")]
    [InlineData("RETURNING id, users.name")]
    public void PostgresStrictSnapshot_UnprovedReturnProjectionRemainsDenied(string returning)
    {
        var sql = BuildSql(["name", "city"]) + " " + returning;
        Assert.ThrowsAny<Exception>(() =>
        {
            var parsed = CoreSqlTextParser.ParseDml(sql, SqlAgentToolType.Postgres);
            CoreDmlCompiler.CreateDefault().Compile(parsed,
                SqlAgentToolType.Postgres, Context(["name", "city"]));
        });
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
