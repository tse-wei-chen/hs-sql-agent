using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.SqlParsing;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Explicit first provider proof for the closed arbitrary-precision numeric
/// model. Never send a rounded CLR decimal or paste source SQL into a command.
/// </summary>
public sealed class SqlCorePostgresExactNumericLiteralTests
{
    public static IEnumerable<object[]> ExactValues()
    {
        yield return ["1234567890123456789012345678901234567890"];
        yield return ["999999999999999999999999999999.0"];
        yield return ["0.12345678901234567890123456789"];
        yield return ["1.00000000000000000000000000001"];
        yield return ["1e-29"];
        yield return ["1e-1000"];
        yield return ["-1234567890123456789012345678901234567890"];
        yield return ["-0.12345678901234567890123456789"];
    }

    private static CompiledSqlCommand Compile(string value, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery("SELECT " + value + " AS amount",
            SqlAgentToolType.Postgres, target,
            new SqlPlanValidationContext("postgres-exact-numeric-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [MemberData(nameof(ExactValues))]
    public void Query_PreservesExactNumberViaBoundTextAndExplicitNumericCast(string value)
    {
        var result = Compile(value, SqlAgentToolType.Postgres);
        Assert.Contains("CAST(", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS NUMERIC)", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value.TrimStart('-'), result.Sql, StringComparison.Ordinal);
        var parameter = Assert.Single(result.Parameters);
        Assert.Equal(value, Assert.IsType<string>(parameter.Value));
    }

    [Theory]
    [MemberData(nameof(ExactValues))]
    public void DmlPredicate_UsesSameExactBindingAndNeverInjectsLiteral(string value)
    {
        var sql = "DELETE FROM metrics WHERE amount = " + value;
        var result = SqlCoreFacade.CompileDml(sql,
            SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("postgres-exact-numeric-dml-v1"),
            TestContext.Current.CancellationToken);
        Assert.Contains("AS NUMERIC)", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value.TrimStart('-'), result.Sql, StringComparison.Ordinal);
        Assert.Equal(value, Assert.IsType<string>(Assert.Single(result.Parameters).Value));
    }

    [Theory]
    [MemberData(nameof(ExactValues))]
    public void ScriptApis_AcceptExactNumbersWithoutFloatingPointCoercion(string value)
    {
        var sql = "SELECT " + value + "; SELECT 2";
        var parsed = SqlCoreFacade.ParseScript(sql, SqlAgentToolType.Postgres,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, parsed.Statements.Count);
        Assert.All(parsed.Statements, item =>
            Assert.Equal(HsSqlAgent.SqlCore.Script.SqlScriptParseStatus.Parsed, item.Status));
        var analyzed = SqlCoreFacade.AnalyzeScript(sql, SqlAgentToolType.Postgres,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, analyzed.Statements.Count);
    }

    [Theory]
    // This 40-digit value fits MySQL DECIMAL(40,0), but not SQL Server's
    // maximum precision 38. Its remaining targets are still unproven.
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CrossProvider_RejectsUnprovenExactNumericBinding(SqlAgentToolType target)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("1234567890123456789012345678901234567890", target));
        Assert.Contains("numeric.exact_literal", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void OtherSourceDialects_KeepPreviousLosslessFailClosedContract(SqlAgentToolType source)
    {
        var sql = "SELECT 1e-29";
        var e = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(sql, source, source,
                new SqlPlanValidationContext("numeric-no-unproven-sources"),
                new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken));
        Assert.Equal("SQL_LEXICAL_NUMERIC_PRECISION_LOSS", e.Diagnostic?.Code);
    }

    [Fact]
    public void BeyondInitialProofCap_StillRejectsWithTypedSourceDiagnostic()
    {
        var sql = "SELECT " + new string('9', 1001);
        var e = Assert.Throws<SqlParseException>(() =>
            SqlCoreFacade.CompileQuery(sql, SqlAgentToolType.Postgres,
                SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("postgres-max-exact-numeric"),
                new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken));
        Assert.Equal("SQL_LEXICAL_NUMERIC_OUT_OF_RANGE", e.Diagnostic?.Code);
        Assert.Equal(SqlDiagnosticStage.Lexical, e.Diagnostic?.Stage);
    }

    [Fact]
    public void PreCancelledExactNumeric_DoesNotProduceCommand()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlCoreFacade.CompileQuery(
                "SELECT " + new string('9', 512),
                SqlAgentToolType.Postgres, SqlAgentToolType.Postgres,
                new SqlPlanValidationContext("exact-cancel-v1"),
                new SqlExecutionPlanPolicy(), cancelled.Token));
    }
}
