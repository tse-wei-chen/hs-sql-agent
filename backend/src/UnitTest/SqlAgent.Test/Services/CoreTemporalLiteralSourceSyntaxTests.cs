using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreTemporalLiteralSourceSyntaxTests
{
    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer, "DATE '2026-08-24'")]
    [InlineData(SqlAgentToolType.MsSqlServer, "TIME '12:34:56'")]
    [InlineData(SqlAgentToolType.MsSqlServer, "TIMESTAMP '2026-08-24 12:34:56'")]
    [InlineData(SqlAgentToolType.MsSqlServer, "TIMESTAMP WITH TIME ZONE '2026-08-24 12:34:56+00:00'")]
    [InlineData(SqlAgentToolType.Sqlite, "DATE '2026-08-24'")]
    [InlineData(SqlAgentToolType.Sqlite, "TIME '12:34:56'")]
    [InlineData(SqlAgentToolType.Sqlite, "TIMESTAMP '2026-08-24 12:34:56'")]
    [InlineData(SqlAgentToolType.Oracle, "TIME '12:34:56'")]
    [InlineData(SqlAgentToolType.Oracle, "TIMESTAMP WITH TIME ZONE '2026-08-24 12:34:56+00:00'")]
    [InlineData(SqlAgentToolType.MySQL, "TIMESTAMP WITH TIME ZONE '2026-08-24 12:34:56+00:00'")]
    [InlineData(SqlAgentToolType.Firebird, "TIME WITH TIME ZONE '12:34:56+00:00'")]
    [InlineData(SqlAgentToolType.Firebird, "TIMESTAMP WITH TIME ZONE '2026-08-24 12:34:56+00:00'")]
    public void ParseQuery_UnsupportedTypedTemporalSpelling_FailsAtSourceBoundary(
        SqlAgentToolType sourceDialect,
        string literal)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery($"SELECT {literal}", sourceDialect));

        Assert.Contains("typed temporal literal", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceDialect.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Core source profile", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "DATE '2026-08-24'")]
    [InlineData(SqlAgentToolType.Postgres, "TIME '12:34:56'")]
    [InlineData(SqlAgentToolType.Postgres, "TIMESTAMP '2026-08-24 12:34:56'")]
    [InlineData(SqlAgentToolType.Postgres, "TIMESTAMP WITH TIME ZONE '2026-08-24 12:34:56+00:00'")]
    [InlineData(SqlAgentToolType.MySQL, "DATE '2026-08-24'")]
    [InlineData(SqlAgentToolType.MySQL, "TIME '12:34:56'")]
    [InlineData(SqlAgentToolType.MySQL, "TIMESTAMP '2026-08-24 12:34:56'")]
    [InlineData(SqlAgentToolType.Oracle, "DATE '2026-08-24'")]
    [InlineData(SqlAgentToolType.Oracle, "TIMESTAMP '2026-08-24 12:34:56'")]
    [InlineData(SqlAgentToolType.Firebird, "DATE '2026-08-24'")]
    [InlineData(SqlAgentToolType.Firebird, "TIME '12:34:56'")]
    [InlineData(SqlAgentToolType.Firebird, "TIMESTAMP '2026-08-24 12:34:56'")]
    public void Compile_DeclaredTypedTemporalSpelling_RemainsPortable(
        SqlAgentToolType sourceDialect,
        string literal)
    {
        var command = CompileQuery(
            $"SELECT {literal}",
            sourceDialect,
            SqlAgentToolType.Postgres);

        Assert.NotEmpty(command.Sql);
        Assert.Single(command.Parameters);
    }

    [Fact]
    public void Compile_SqlServerCastDate_RemainsValidRawSourceSyntax()
    {
        var command = CompileQuery(
            "SELECT CAST('2026-08-24' AS DATE)",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.Contains("CAST(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" AS DATE)", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_SqlServerQuotedDatetimeString_RemainsValidRawSourceSyntax()
    {
        var command = CompileQuery(
            "SELECT '2026-08-24 12:34:56'",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.Single(command.Parameters);
        Assert.Equal("2026-08-24 12:34:56", command.Parameters[0].Value);
    }

    [Fact]
    public void Compile_QuotedDateIdentifier_RemainsColumnReference()
    {
        var command = CompileQuery(
            "SELECT [DATE] FROM users",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void ParseDml_UnsupportedTypedTemporalSpelling_UsesTheSameSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "UPDATE orders SET created_at = DATE '2026-08-24' WHERE id = 9",
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("DATE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MsSqlServer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand CompileQuery(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("policy-v1"),
            new SqlExecutionPlanPolicy());
}
