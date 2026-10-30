using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Second target proof for PostgreSQL-source ExactNumeric: an explicit,
/// exactly sized DECIMAL(p,s) bound-string CAST, never source SQL inlining,
/// implicit target defaults, double, or silently rounded conversion.
/// </summary>
public sealed class SqlCoreBoundedExactNumericTargetTests
{
    public static IEnumerable<object[]> LosslessTargets()
    {
        const string wide40 = "1234567890123456789012345678901234567890";
        yield return [SqlAgentToolType.MySQL, wide40, wide40, 40, 0];
        yield return [SqlAgentToolType.MySQL, new string('9', 65), new string('9', 65), 65, 0];
        yield return [SqlAgentToolType.MySQL, "1e-29", "0." + new string('0', 28) + "1", 29, 29];
        yield return [SqlAgentToolType.MySQL, "-1.00000000000000000000000000001",
            "-1.00000000000000000000000000001", 30, 29];
        yield return [SqlAgentToolType.MsSqlServer, new string('9', 38), new string('9', 38), 38, 0];
        yield return [SqlAgentToolType.MsSqlServer, "1e-29", "0." + new string('0', 28) + "1", 29, 29];
        yield return [SqlAgentToolType.MsSqlServer, "-0.12345678901234567890123456789",
            "-0.12345678901234567890123456789", 29, 29];
        yield return [SqlAgentToolType.MsSqlServer, "1e+30", "1" + new string('0', 30), 31, 0];
    }

    public static IEnumerable<object[]> RejectedTargets()
    {
        yield return [SqlAgentToolType.MySQL, new string('9', 66)];
        yield return [SqlAgentToolType.MySQL, "1e-31"];
        yield return [SqlAgentToolType.MySQL, new string('9', 40) + "." + new string('1', 30)];
        yield return [SqlAgentToolType.MsSqlServer, new string('9', 39)];
        yield return [SqlAgentToolType.MsSqlServer, "1e-39"];
        yield return [SqlAgentToolType.MsSqlServer, new string('9', 30) + "." + new string('1', 20)];
        yield return [SqlAgentToolType.MySQL, "1e-1000"];
        yield return [SqlAgentToolType.MsSqlServer, "1e-1000"];
    }

    private static CompiledSqlCommand Compile(string value, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery("SELECT " + value + " AS amount",
            SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("bounded-exact-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [MemberData(nameof(LosslessTargets))]
    public void Query_EmitsExactParameterizedDecimalWithProvenPrecisionAndScale(
        SqlAgentToolType target, string value, string fixedValue, int precision, int scale)
    {
        var result = Compile(value, target);
        Assert.Contains($"AS DECIMAL({precision},{scale}))", result.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value.TrimStart('-'), result.Sql, StringComparison.Ordinal);
        Assert.Equal(fixedValue, Assert.IsType<string>(Assert.Single(result.Parameters).Value));
    }

    [Theory]
    [MemberData(nameof(LosslessTargets))]
    public void DmlPredicate_UsesSameExactBoundDecimalProof(
        SqlAgentToolType target, string value, string fixedValue, int precision, int scale)
    {
        var result = SqlCoreFacade.CompileDml("DELETE FROM metrics WHERE amount = " + value,
            SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("bounded-exact-dml-v1"),
            TestContext.Current.CancellationToken);
        Assert.Contains($"AS DECIMAL({precision},{scale}))", result.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixedValue, Assert.IsType<string>(Assert.Single(result.Parameters).Value));
    }

    [Theory]
    [MemberData(nameof(RejectedTargets))]
    public void TargetProof_RejectsScaleAndPrecisionOverflowBeforeAnyParameterIsRendered(
        SqlAgentToolType target, string value)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(value, target));
        Assert.Contains("numeric.exact_literal", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DECIMAL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherTargets_RemainExplicitlyUnproven(SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("1e-29", target));
        Assert.Contains("numeric.exact_literal", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void NonPostgresSource_StillHasNoUnprovenNumericLexerExpansion(
        SqlAgentToolType source)
    {
        var error = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery("SELECT 1e-29", source, source,
                new SqlPlanValidationContext("bounded-exact-source"),
                new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken));
        Assert.Equal("SQL_LEXICAL_NUMERIC_PRECISION_LOSS", error.Diagnostic?.Code);
    }

    [Fact]
    public void PostgresSourceAndTarget_RemainUnboundedNumericCastWithOriginalParameter()
    {
        var command = Compile("1e-1000", SqlAgentToolType.Postgres);
        Assert.Contains("AS NUMERIC)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1e-1000", Assert.IsType<string>(Assert.Single(command.Parameters).Value));
    }
}
