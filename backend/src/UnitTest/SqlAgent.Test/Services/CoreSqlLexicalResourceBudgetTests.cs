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

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void BalancedNestingBeyondEightThousandLevels_FailsWithTypedResourceDiagnostic(
        SqlAgentToolType dialect)
    {
        // Well below existing source length/token budgets: only the
        // independent *structural* budget may reject this input.
        const int permittedDepth = 8_192;
        var sql = "SELECT " + new string('(', permittedDepth + 1)
            + "1" + new string(')', permittedDepth + 1);
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect,
                new SqlPlanValidationContext("nesting-budget-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Equal("SQL_LEXICAL_NESTING_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, error.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
        Assert.Equal("SELECT ".Length + permittedDepth, error.Diagnostic?.Span?.Start);
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void ParensInsideQuotedSqlLiteral_DoNotUseStructuralBudget(
        SqlAgentToolType dialect)
    {
        var sql = "SELECT '" + new string('(', 8_193) + "'";
        var compiled = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("nesting-budget-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
    }

    [Theory]
    [MemberData(nameof(SixDialects))]
    public void BalancedDepthTwoThousandFortyEight_RemainsAccepted(
        SqlAgentToolType dialect)
    {
        const int depth = 2_048;
        var sql = "SELECT " + new string('(', depth) + "1" + new string(')', depth);
        var compiled = SqlCoreFacade.CompileQuery(
            sql, dialect, dialect,
            new SqlPlanValidationContext("nesting-budget-v1"),
            new SqlExecutionPlanPolicy());
        Assert.NotEmpty(compiled.Sql);
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
