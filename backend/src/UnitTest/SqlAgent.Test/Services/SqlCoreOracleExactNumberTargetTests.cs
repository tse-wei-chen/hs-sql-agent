using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Third exact-numeric target: Oracle NUMBER precision 1..38. The string
/// parameter must use explicit NLS decimal semantics, not ambient NLS.
/// Firebird INT128/DECFLOAT are intentionally not enabled by this proof.
/// </summary>
public sealed class SqlCoreOracleExactNumberTargetTests
{
    public static IEnumerable<object[]> LosslessNumbers()
    {
        yield return [new string('9', 38), new string('9', 38), 38, 0];
        yield return ["12345678901234567890123456789012345678", "12345678901234567890123456789012345678", 38, 0];
        yield return ["-0.12345678901234567890123456789", "-0.12345678901234567890123456789", 29, 29];
        yield return ["1e-29", "0." + new string('0', 28) + "1", 29, 29];
        yield return ["1.00000000000000000000000000001", "1.00000000000000000000000000001", 30, 29];
        yield return ["1e+30", "1" + new string('0', 30), 31, 0];
        yield return ["-1e-37", "-0." + new string('0', 36) + "1", 37, 37];
    }

    public static IEnumerable<object[]> InvalidNumbers()
    {
        yield return [new string('9', 39)];
        yield return ["1e-39"];
        yield return [new string('9', 20) + "." + new string('1', 20)];
        yield return ["1e+38"];
        yield return ["1e-1000"];
    }

    private static CompiledSqlCommand Compile(string value, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery("SELECT " + value + " AS amount",
            SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("oracle-exact-number-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [MemberData(nameof(LosslessNumbers))]
    public void Query_UsesExactTextParameterExplicitNUMBERShapeAndInvariantNls(
        string value, string expected, int precision, int scale)
    {
        var result = Compile(value, SqlAgentToolType.Oracle);
        Assert.Contains("TO_NUMBER(", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"AS NUMBER({precision},{scale}))", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NLS_NUMERIC_CHARACTERS", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value.TrimStart('-'), result.Sql, StringComparison.Ordinal);
        Assert.Equal(expected, Assert.IsType<string>(Assert.Single(result.Parameters).Value));
    }

    [Theory]
    [MemberData(nameof(LosslessNumbers))]
    public void Dml_UsesIdenticalLosslessOracleBoundNUMBERProof(
        string value, string expected, int precision, int scale)
    {
        var result = SqlCoreFacade.CompileDml("DELETE FROM metrics WHERE amount = " + value,
            SqlAgentToolType.Postgres, SqlAgentToolType.Oracle,
            new SqlPlanValidationContext("oracle-exact-number-dml-v1"),
            TestContext.Current.CancellationToken);
        Assert.Contains($"AS NUMBER({precision},{scale}))", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NLS_NUMERIC_CHARACTERS", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, Assert.IsType<string>(Assert.Single(result.Parameters).Value));
    }

    [Theory]
    [MemberData(nameof(InvalidNumbers))]
    public void TargetRejectsUnrepresentableValueWithoutRounding(string value)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(value, SqlAgentToolType.Oracle));
        Assert.Contains("numeric.exact_literal", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NUMBER", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("38", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OracleSourceStillFailsClosedForOverClrDecimalValues()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery("SELECT 1e-29",
                SqlAgentToolType.Oracle, SqlAgentToolType.Oracle,
                new SqlPlanValidationContext("oracle-source-no-unproven-lexer"),
                new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken));
        Assert.Equal("SQL_LEXICAL_NUMERIC_PRECISION_LOSS", error.Diagnostic?.Code);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Firebird)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void StillUnprovenTargetHasNoNewExactNumericCapability(SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("1e-29", target));
        Assert.Contains("numeric.exact_literal", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgreSqlSourceUnboundedNumericStillUnchanged()
    {
        var result = Compile("1e-1000", SqlAgentToolType.Postgres);
        Assert.Contains("AS NUMERIC)", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1e-1000", Assert.IsType<string>(Assert.Single(result.Parameters).Value));
    }
}
