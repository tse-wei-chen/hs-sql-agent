using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Only native Firebird clock-part EXTRACT lowering for time-of-day values.
/// Date fields must keep their existing DATE cast; unsupported parts fail closed.
/// </summary>
public sealed class SqlCoreFirebirdClockExtractTests
{
    private static CompiledSqlCommand Compile(
        string expression, SqlAgentToolType source, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(
            "SELECT " + expression + " AS extracted FROM RDB$DATABASE",
            source, target,
            new SqlPlanValidationContext("firebird-clock-extract-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("HOUR")]
    [InlineData("MINUTE")]
    [InlineData("SECOND")]
    public void FirebirdTarget_UsesTimeNotDateForClockParts(string part)
    {
        var compiled = Compile(
            "EXTRACT(" + part + " FROM TIMESTAMP '2021-09-21 14:05:06.2500')",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird);

        Assert.Contains("EXTRACT(" + part + " FROM CAST(",
            compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" AS TIME))", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" AS DATE))", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdSource_UsesNativeHourWithPostgresTarget()
    {
        var compiled = Compile(
            "EXTRACT(HOUR FROM TIMESTAMP '2021-09-21 14:05:06')",
            SqlAgentToolType.Firebird, SqlAgentToolType.Postgres);
        Assert.Contains("EXTRACT(HOUR FROM", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdTarget_DoesNotExpandEpochCapability()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "EXTRACT(EPOCH FROM TIMESTAMP '2021-09-21 14:05:06')",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird));
        Assert.Contains("temporal.date_part.epoch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirebirdTarget_StillCastsCalendarPartsAsDate()
    {
        var compiled = Compile(
            "EXTRACT(MONTH FROM TIMESTAMP '2021-09-21 14:05:06')",
            SqlAgentToolType.Postgres, SqlAgentToolType.Firebird);
        Assert.Contains(" AS DATE))", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
