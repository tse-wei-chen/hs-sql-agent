using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreSqlLexicalResourceBudgetTests
{
    public static IEnumerable<object[]> SixDialects()
        => Enum.GetValues<SqlAgentToolType>().Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void EverySourceDialect_RejectsOversizedSqlBeforeLexing(
        SqlAgentToolType dialect)
    {
        var text = "SELECT '" + new string('X', 4_194_304) + "'";
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(
                text, dialect, dialect,
                new SqlPlanValidationContext("resource-guard-v1"),
                new SqlExecutionPlanPolicy()));
        Assert.NotNull(error.Diagnostic);
        Assert.Equal("SQL_LEXICAL_INPUT_LIMIT", error.Diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, error.Diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, error.Diagnostic.Category);
        Assert.Equal(4_194_304, error.Diagnostic.Span?.Start);
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void EverySourceDialect_RejectsExcessiveTokenCountWithoutParsing(
        SqlAgentToolType dialect)
    {
        var text = "SELECT " + string.Concat(
            Enumerable.Repeat("1,", 70_000)) + "1";
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(
                text, dialect, dialect,
                new SqlPlanValidationContext("resource-guard-v1"),
                new SqlExecutionPlanPolicy()));
        Assert.NotNull(error.Diagnostic);
        Assert.Equal("SQL_LEXICAL_TOKEN_LIMIT", error.Diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, error.Diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, error.Diagnostic.Category);
    }

    [Fact]
    public void NormalSql_IsNotAffectedByResourceBudgets()
    {
        var command = SqlCoreFacade.CompileQuery(
            "SELECT 1 AS result",
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("resource-guard-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(command.Sql);
    }
}
