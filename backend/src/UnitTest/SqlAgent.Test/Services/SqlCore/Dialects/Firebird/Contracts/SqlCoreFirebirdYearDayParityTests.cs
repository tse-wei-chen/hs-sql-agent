using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

/// <summary>
/// Firebird YEARDAY is zero-based; PostgreSQL DOY is one-based.
/// Preserve the source field's meaning in either target dialect.
/// </summary>
public sealed class SqlCoreFirebirdYearDayParityTests
{
    private static CompiledSqlCommand Compile(string part, SqlAgentToolType source, SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery(
            "SELECT EXTRACT(" + part + " FROM DATE '2024-02-29') AS day_no FROM RDB$DATABASE",
            source, target,
            new SqlPlanValidationContext("firebird-yearday-doy-v1"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("DOY", SqlAgentToolType.Postgres, SqlAgentToolType.Firebird,
        "EXTRACT(YEARDAY FROM CAST(", " + 1) AS SMALLINT)")]
    [InlineData("YEARDAY", SqlAgentToolType.Firebird, SqlAgentToolType.Postgres,
        "EXTRACT(DOY FROM", " - 1) AS SMALLINT)")]
    [InlineData("YEARDAY", SqlAgentToolType.Firebird, SqlAgentToolType.Firebird,
        "EXTRACT(YEARDAY FROM CAST(", " AS DATE))")]
    public void DayOfYear_PreservesSourceIndexingAcrossTargets(
        string part, SqlAgentToolType source, SqlAgentToolType target,
        string nativeFunction, string expectedSuffix)
    {
        var command = Compile(part, source, target);
        Assert.Contains(nativeFunction, command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedSuffix, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DOY", SqlAgentToolType.Firebird, SqlAgentToolType.Postgres)]
    [InlineData("YEARDAY", SqlAgentToolType.Postgres, SqlAgentToolType.Firebird)]
    public void DayOfYear_RejectsOtherProvidersSourceSpelling(
        string part, SqlAgentToolType source, SqlAgentToolType target)
    {
        Assert.Throws<SqlParseException>(() => Compile(part, source, target));
    }
}
