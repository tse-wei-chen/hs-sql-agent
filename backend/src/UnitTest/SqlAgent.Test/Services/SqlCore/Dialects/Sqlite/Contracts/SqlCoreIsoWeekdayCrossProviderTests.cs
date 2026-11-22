using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Xunit;

namespace SqlAgent.Test.Services;

public sealed class SqlCoreIsoWeekdayCrossProviderTests
{
    private static CompiledSqlCommand Compile(string expression, SqlAgentToolType source,
        SqlAgentToolType target) =>
        SqlCoreFacade.CompileQuery("SELECT " + expression + " AS weekday_no",
            source, target, new SqlPlanValidationContext("iso-weekday-cross-provider"),
            new SqlExecutionPlanPolicy(), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, "WEEKDAY(", " + 1)")]
    [InlineData(SqlAgentToolType.Sqlite, "STRFTIME('%w', ", "% 7) + 1")]
    public void PostgresIsoDow_UsesProvenNativeTargetWeekdayNumbering(
        SqlAgentToolType target, string nativeWeekday, string conversion)
    {
        var compiled = Compile("EXTRACT(ISODOW FROM DATE '2021-01-03')",
            SqlAgentToolType.Postgres, target);
        Assert.Contains(nativeWeekday, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(conversion, compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    public void NonPostgresSources_RejectIsodowAtParse(SqlAgentToolType source)
    {
        var exception = Assert.Throws<SqlParseException>(() =>
            Compile("EXTRACT(ISODOW FROM '2021-01-03')", source,
                SqlAgentToolType.Postgres));
        Assert.Contains("ISODOW", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void UnprovenEpoch_IsStillTargetDenied(SqlAgentToolType target)
    {
        var exception = Assert.Throws<SqlCompilationException>(() =>
            Compile("EXTRACT(EPOCH FROM DATE '2021-01-03')",
                SqlAgentToolType.Postgres, target));
        Assert.Contains("temporal.date_part.epoch", exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
