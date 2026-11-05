using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// The F# AST stores decimal literals with CLR Decimal precision. A valid SQL
/// numeric token must never silently underflow to zero or round to a
/// different value when projected into that finite-precision representation.
/// </summary>
public sealed class SqlCoreNumericLiteralPrecisionTests
{
    private const string DiagnosticCode = "SQL_LEXICAL_NUMERIC_PRECISION_LOSS";

    public static IEnumerable<object[]> LossyNumericCases()
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        foreach (var value in new[]
        {
            "1e-29",                                // Smaller than Decimal's minimum step.
            "1e-1000",                              // Long-exponent nonzero underflow.
            "0.00000000000000000000000000001",      // Fractional underflow.
            "1.00000000000000000000000000001",      // Silently rounded mantissa.
            "0.12345678901234567890123456789"      // Over-precision fractional literal.
        })
        {
            // PostgreSQL exact NUMERIC is independently proven by the new
            // AST/parameter-bound tests; other sources still fail closed.
            if (provider != SqlAgentToolType.Postgres)
                yield return [provider, value];
        }
    }

    private static void AssertLosslessRejection(
        SqlParseException error, string sql, string numericLiteral)
    {
        var diagnostic = Assert.IsType<SqlDiagnostic>(error.Diagnostic);
        Assert.Equal(DiagnosticCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        var span = Assert.IsType<SqlDiagnosticSpan>(diagnostic.Span);
        Assert.Equal(sql.IndexOf(numericLiteral, StringComparison.Ordinal), span.Start);
        Assert.Equal(numericLiteral.Length, span.Length);
    }

    [Theory]
    [MemberData(nameof(LossyNumericCases))]
    public void CompileQueryAndTryCompile_FailClosedOnSilentDecimalPrecisionLoss(
        SqlAgentToolType provider, string value)
    {
        var sql = "SELECT " + value;
        var context = new SqlPlanValidationContext("decimal-lossless-v1");
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(
                sql, provider, provider, context, new SqlExecutionPlanPolicy(),
                TestContext.Current.CancellationToken));
        AssertLosslessRejection(error, sql, value);

        var result = SqlCoreFacade.TryCompileQuery(
            sql, provider, provider, context, new SqlExecutionPlanPolicy(),
            TestContext.Current.CancellationToken);
        Assert.False(result.Success);
        Assert.Equal("SQL_PARSE_ERROR", result.ErrorCode);
        var diagnostic = Assert.Single(result.TypedDiagnostics);
        Assert.Equal(DiagnosticCode, diagnostic.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, diagnostic.Stage);
        Assert.Equal(SqlDiagnosticCategory.Syntax, diagnostic.Category);
        var span = Assert.IsType<SqlDiagnosticSpan>(diagnostic.Span);
        Assert.Equal(sql.IndexOf(value, StringComparison.Ordinal), span.Start);
        Assert.Equal(value.Length, span.Length);
    }

    [Theory]
    [MemberData(nameof(LossyNumericCases))]
    public void CompileDmlAndStaticScript_FailWithIdenticalSpan(
        SqlAgentToolType provider, string value)
    {
        var dml = "DELETE FROM users WHERE id = " + value;
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileDml(
                dml, provider, provider,
                new SqlPlanValidationContext("decimal-lossless-v1"),
                TestContext.Current.CancellationToken));
        AssertLosslessRejection(error, dml, value);

        var script = "SELECT " + value + "; SELECT 2";
        var parsedError = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.ParseScript(script, provider,
                TestContext.Current.CancellationToken));
        AssertLosslessRejection(parsedError, script, value);

        var analysisError = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.AnalyzeScript(script, provider,
                TestContext.Current.CancellationToken));
        AssertLosslessRejection(analysisError, script, value);
    }

    [Theory]
    [InlineData("1e-28")]
    [InlineData("10e-29")]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("1.230000000000000000000000000000")]
    [InlineData("0.000000000000000000000000000000")]
    public void ExactlyRepresentableDecimalValue_RemainsAccepted(string value)
    {
        foreach (var provider in Enum.GetValues<SqlAgentToolType>())
        {
            var script = SqlCoreFacade.ParseScript(
                "SELECT " + value, provider, TestContext.Current.CancellationToken);
            Assert.Equal(HsSqlAgent.SqlCore.Script.SqlScriptParseStatus.Parsed,
                Assert.Single(script.Statements).Status);
        }
    }
}
