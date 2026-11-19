using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Target Firebird QUARTER must work even on older Firebird engines without
/// native EXTRACT(QUARTER); the proof is the portable MONTH integer algebra.
/// </summary>
public sealed class SqlCoreFirebirdQuarterExtractTests
{
    private static CompiledSqlCommand Compile(string expression) =>
        SqlCoreFacade.CompileQuery(
            "SELECT " + expression + " AS extracted FROM RDB$DATABASE",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
            new SqlPlanValidationContext("firebird-quarter-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Fact]
    public void FirebirdTarget_QuarterLowersThroughMonthWithoutNativeQuarterDependency()
    {
        var compiled = Compile("EXTRACT(QUARTER FROM DATE '2021-12-31')");

        Assert.Contains("EXTRACT(MONTH FROM CAST(", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("+ 2) / 3) AS SMALLINT)", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXTRACT(QUARTER FROM", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdTarget_MonthPreservesNativeCalendarRendering()
    {
        var compiled = Compile("EXTRACT(MONTH FROM DATE '2021-12-31')");
        Assert.Contains("EXTRACT(MONTH FROM CAST(", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("+ 2) / 3", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdTarget_EpochRemainsCapabilityDenied()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile("EXTRACT(EPOCH FROM TIMESTAMP '2021-12-31 12:00:00')"));
        Assert.Contains("temporal.date_part.epoch", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
