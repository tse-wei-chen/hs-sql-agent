using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlServerBooleanSourceSyntaxTests
{
    [Theory]
    [InlineData("TRUE")]
    [InlineData("FALSE")]
    public void ParseQuery_BareBooleanLiteral_IsRejectedForSqlServerSource(string literal)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                $"SELECT id FROM users WHERE is_active = {literal}",
                SqlAgentToolType.MsSqlServer));

        Assert.Contains(literal, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T-SQL", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0 or 1", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_PostgresBooleanLiteral_RemainsValidRawSourceSyntax()
    {
        var command = CompileQuery(
            "SELECT TRUE",
            SqlAgentToolType.Postgres,
            SqlAgentToolType.Postgres);

        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void Compile_SqlServerBitPredicate_RemainsValidRawSourceSyntax()
    {
        var command = CompileQuery(
            "SELECT id FROM users WHERE is_active = 1",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.Postgres);

        Assert.NotEmpty(command.Sql);
    }

    [Theory]
    [InlineData("TRUE")]
    [InlineData("NULL")]
    [InlineData("CURRENT_DATE")]
    public void Compile_QuotedKeywordIdentifier_RemainsColumnReference(string identifier)
    {
        var command = CompileQuery(
            $"SELECT [{identifier}] FROM users",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.NotEmpty(command.Sql);
    }

    [Fact]
    public void ParseDml_BareBooleanLiteral_UsesTheSameSqlServerSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseDml(
                "DELETE FROM users WHERE is_active = TRUE",
                SqlAgentToolType.MsSqlServer));

        Assert.Contains("TRUE", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T-SQL", error.Message, StringComparison.OrdinalIgnoreCase);
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
