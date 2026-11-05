using Xunit;

namespace SqlAgent.Test.Services;

public class CoreInsertSelectShapeValidationTests
{
    [Fact]
    public void Compile_InsertSelectWithMismatchedProjectionWidth_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO archive (id, name) SELECT id FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("projection width", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target column count", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectWithWildcardProjection_FailsClosed()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO archive (id, name) SELECT * FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres));

        Assert.Contains("statically known", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wildcard", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertSelectWithMatchingProjectionWidth_RemainsSupported()
    {
        var command = Compile(
            "INSERT INTO archive (id, name) SELECT id, name FROM users",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        Assert.Contains("INSERT INTO", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_InsertDateLiteral_NormalizesTemporalBinding()
    {
        var command = Compile(
            "INSERT INTO archive (created_on) VALUES (DATE '2026-08-23')",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.Equal(SqlStatementKind.Insert, command.Kind);
        var value = Assert.Single(command.Parameters).Value;
        var dateTime = Assert.IsType<DateTime>(value);
        Assert.Equal(new DateTime(2026, 8, 23), dateTime);
    }

    [Fact]
    public void Compile_InsertValuesValidatesEveryRowAgainstProviderCapabilities()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => Compile(
            "INSERT INTO archive (value) VALUES (DATE '2026-08-23'), (TIME '12:34:56')",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Oracle));

        Assert.Contains("literal.time", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreDmlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseDml(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"));
}
