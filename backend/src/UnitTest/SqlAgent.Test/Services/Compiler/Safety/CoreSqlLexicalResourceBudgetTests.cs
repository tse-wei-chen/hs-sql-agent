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
        // Split across small literals: Firebird intentionally rejects
        // a single UTF-8 VARCHAR literal longer than 8,191 characters.
        // The aggregate source text still contains >8,192 quoted '('.
        var sql = "SELECT '" + new string('(', 4_096)
            + "' AS first_part, '" + new string('(', 4_097) + "' AS second_part";
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

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void ExcessiveExpressionAstNodes_AreRejectedBeforeBinding(
        SqlAgentToolType dialect)
    {
        // 40k operands + 40k binary nodes exceed the 65,536-node
        // AST budget, while staying under the independent lexical
        // 131,072-token and 4 Mi-character input caps.
        var sql = "SELECT id" + string.Concat(
            Enumerable.Repeat(" + id", 40_000)) + " FROM users";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, dialect, dialect,
                new SqlPlanValidationContext("ast-budget-v1"),
                new SqlExecutionPlanPolicy()));
        Assert.Equal("SQL_COMPILER_AST_NODE_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Binding, error.Diagnostic?.Stage);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
    }

    [Fact]
    public void ExcessiveDmlAstNodes_AreRejectedBeforeBinding()
    {
        var sql = "UPDATE users SET id = id" +
            string.Concat(Enumerable.Repeat(" + id", 40_000));
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreFacade.CompileDml(
                sql, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("ast-budget-v1")));
        Assert.Equal("SQL_COMPILER_AST_NODE_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
    }

    [Fact]
    public void QueryInspection_CannotBypassParsedAstWorkBudget()
    {
        // Inspection used to enter Binder directly, independently of the
        // public compilation pipeline. Both must share the same budget.
        var sql = "SELECT id" +
            string.Concat(Enumerable.Repeat(" + id", 40_000)) +
            " FROM users";
        var error = Assert.Throws<SqlCompilationException>(() =>
            SqlCoreInspection.GetQueryFacts(sql, SqlAgentToolType.Postgres));
        Assert.Equal("SQL_COMPILER_AST_NODE_LIMIT", error.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticCategory.Resource, error.Diagnostic?.Category);
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
