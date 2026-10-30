using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Unsupported numeric magnitudes must fail with a typed lexical diagnostic,
/// never leak CLR Decimal.Parse overflows through Script/Compiler APIs.
/// </summary>
public sealed class SqlCoreNumericLiteralOverflowTests
{
    private const string DiagnosticCode = "SQL_LEXICAL_NUMERIC_OUT_OF_RANGE";

    public static IEnumerable<object[]> OverflowCases()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var number in new[]
        {
            new string('9', 64),                      // Int64 and Decimal overflow
            "999999999999999999999999999999.0",       // decimal fraction overflow
            "1e1000"                                 // scientific notation overflow
        })
            yield return [provider, number];
    }

    private static void AssertOverflow(SqlParseException exception, string sql, string number)
    {
        var diagnostic = Assert.IsType<SqlDiagnostic>(exception.Diagnostic);
        Assert.Equal(DiagnosticCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        var span = Assert.IsType<SqlDiagnosticSpan>(diagnostic.Span);
        Assert.Equal(sql.IndexOf(number, StringComparison.Ordinal), span.Start);
        Assert.Equal(number.Length, span.Length);
    }

    [Theory]
    [MemberData(nameof(OverflowCases))]
    public void CompileQuery_RejectsOutOfRangeNumbersWithLexicalSpan(
        SqlAgentToolType provider, string number)
    {
        var sql = "SELECT " + number;
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(sql, provider, provider,
                new SqlPlanValidationContext("numeric-overflow-v1"),
                new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken));
        AssertOverflow(error, sql, number);
    }

    [Theory]
    [MemberData(nameof(OverflowCases))]
    public void CompileDml_RejectsOutOfRangeNumbersBeforePolicy(
        SqlAgentToolType provider, string number)
    {
        var sql = "DELETE FROM users WHERE id = " + number;
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileDml(sql, provider, provider,
                new SqlPlanValidationContext("numeric-overflow-v1"),
                TestContext.Current.CancellationToken));
        AssertOverflow(error, sql, number);
    }

    [Theory]
    [MemberData(nameof(OverflowCases))]
    public void ScriptApis_RejectOutOfRangeNumbersWithSameDiagnostic(
        SqlAgentToolType provider, string number)
    {
        var sql = "SELECT " + number + "; SELECT 1";
        var parsed = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.ParseScript(sql, provider, TestContext.Current.CancellationToken));
        AssertOverflow(parsed, sql, number);
        var analyzed = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.AnalyzeScript(sql, provider, TestContext.Current.CancellationToken));
        AssertOverflow(analyzed, sql, number);
    }

    [Theory]
    [MemberData(nameof(OverflowCases))]
    public void TryCompileQuery_ReportsExactDiagnosticAndNeverRawClrOverflow(
        SqlAgentToolType provider, string number)
    {
        var sql = "SELECT " + number;
        var result = SqlCoreFacade.TryCompileQuery(sql, provider, provider,
            new SqlPlanValidationContext("numeric-overflow-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal(DiagnosticCode, result.ErrorCode);
        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal(DiagnosticCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        Assert.Equal(sql.IndexOf(number, StringComparison.Ordinal),
            diagnostic.Span.Start);
        Assert.Equal(number.Length, diagnostic.Span.Length);
    }

    [Fact]
    public void MaxDecimalMagnitude_StillParsesAndCannotBeSilentlyReduced()
    {
        const string number = "79228162514264337593543950335";
        var sql = "SELECT " + number;
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres,
            TestContext.Current.CancellationToken);
        var statement = Assert.Single(parsed.Statements);
        Assert.Equal(HsSqlAgent.SqlCore.Script.SqlScriptParseStatus.Parsed, statement.Status);
    }
}
