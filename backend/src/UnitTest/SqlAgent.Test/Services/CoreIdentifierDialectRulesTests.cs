using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreIdentifierDialectRulesTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SELECT foo.id FROM users AS \"foo\"")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT foo.id FROM users AS \"FOO\"")]
    [InlineData(SqlAgentToolType.Firebird, "SELECT foo.id FROM users AS \"FOO\"")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT foo.id FROM users AS `Foo`")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT foo.id FROM users AS [Foo]")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT foo.id FROM users AS \"Foo\"")]
    public void Bind_QualifierIdentity_UsesDialectIdentifierSemantics(
        SqlAgentToolType provider,
        string sql)
    {
        var command = Compile(sql, provider);
        Assert.Contains("SELECT", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SELECT 1 AS \"foo\" ORDER BY Foo")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT 1 AS \"FOO\" ORDER BY foo")]
    [InlineData(SqlAgentToolType.Firebird, "SELECT 1 AS \"FOO\" ORDER BY foo")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT 1 AS `Foo` ORDER BY foo")]
    [InlineData(SqlAgentToolType.MsSqlServer, "SELECT 1 AS [Foo] ORDER BY foo")]
    [InlineData(SqlAgentToolType.Sqlite, "SELECT 1 AS \"Foo\" ORDER BY foo")]
    public void Compile_NoFromOrderByAlias_UsesDialectIdentifierSemantics(
        SqlAgentToolType provider,
        string sql)
    {
        var command = Compile(sql, provider);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SELECT 1 AS \"foo\" UNION SELECT 2 ORDER BY Foo")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT 1 AS \"FOO\" UNION SELECT 2 ORDER BY foo")]
    [InlineData(SqlAgentToolType.MySQL, "SELECT 1 AS `Foo` UNION SELECT 2 ORDER BY foo")]
    public void Compile_SetOperationOrderByAlias_UsesDialectIdentifierSemantics(
        SqlAgentToolType provider,
        string sql)
    {
        var command = Compile(sql, provider);
        Assert.Contains("UNION", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "SELECT 1 AS \"Foo\" ORDER BY foo")]
    [InlineData(SqlAgentToolType.Oracle, "SELECT 1 AS \"Foo\" ORDER BY foo")]
    [InlineData(SqlAgentToolType.Firebird, "SELECT 1 AS \"Foo\" ORDER BY foo")]
    public void Compile_QuotedAliasWithDifferentCanonicalCase_RemainsDistinct(
        SqlAgentToolType provider,
        string sql)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(sql, provider));
        Assert.Contains(
            "requires a FROM source",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType provider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, provider),
            provider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
