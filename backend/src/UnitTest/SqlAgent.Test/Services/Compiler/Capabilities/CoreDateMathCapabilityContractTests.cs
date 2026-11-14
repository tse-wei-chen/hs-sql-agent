using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDateMathCapabilityContractTests
{
    [Theory]
    [InlineData("dd", "DAY")]
    [InlineData("wk", "WEEK")]
    [InlineData("mm", "MONTH")]
    [InlineData("qq", "QUARTER")]
    [InlineData("yy", "YEAR")]
    [InlineData("hh", "HOUR")]
    [InlineData("mi", "MINUTE")]
    [InlineData("ss", "SECOND")]
    public void SqlServer_DateAddAndDateDiffAliases_CanonicalizeIdentically(
        string alias,
        string canonical)
    {
        var command = Compile(
            "SELECT DATEADD(" + alias + ", 1, created_at), " +
            "DATEDIFF(" + alias + ", created_at, updated_at) FROM events",
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer);

        Assert.Contains(
            "DATEADD(" + canonical,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "DATEDIFF(" + canonical,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "MONTH", true, "INTERVAL '1 month'")]
    [InlineData(SqlAgentToolType.Oracle, "MONTH", false, "core_date_add.unit.month")]
    [InlineData(SqlAgentToolType.Sqlite, "MONTH", false, "core_date_add.unit.month")]
    [InlineData(SqlAgentToolType.Oracle, "HOUR", true, "NUMTODSINTERVAL")]
    [InlineData(SqlAgentToolType.Sqlite, "HOUR", true, "hour")]
    [InlineData(SqlAgentToolType.Firebird, "QUARTER", true, "DATEADD(MONTH")]
    [InlineData(SqlAgentToolType.Firebird, "WEEK", true, "DATEADD(WEEK")]
    [InlineData(SqlAgentToolType.MySQL, "QUARTER", true, "TIMESTAMPADD(QUARTER")]
    [InlineData(SqlAgentToolType.MsSqlServer, "QUARTER", true, "DATEADD(QUARTER")]
    public void DateAdd_TargetUnitContract_IsConsistentAcrossValidationAndRendering(
        SqlAgentToolType targetProvider,
        string unit,
        bool supported,
        string expected)
    {
        if (!supported)
        {
            var error = Assert.Throws<SqlCompilationException>(() => Compile(
                "SELECT DATEADD(" + unit + ", 1, created_at) FROM events",
                SqlAgentToolType.MsSqlServer,
                targetProvider));

            Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        var command = Compile(
            "SELECT DATEADD(" + unit + ", 1, created_at) FROM events",
            SqlAgentToolType.MsSqlServer,
            targetProvider);

        Assert.Contains(expected, command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DATEADD")]
    [InlineData("DATEDIFF")]
    public void UnsupportedUnitAlias_RemainsFailClosed(string function)
    {
        var sql = function == "DATEADD"
            ? "SELECT DATEADD(millisecond, 1, created_at) FROM events"
            : "SELECT DATEDIFF(millisecond, created_at, updated_at) FROM events";

        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            sql,
            SqlAgentToolType.MsSqlServer,
            SqlAgentToolType.MsSqlServer));

        Assert.Contains("Unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("millisecond", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Matrix_DateArithmeticContract_RemainsTranslated(SqlAgentToolType provider)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "temporal.date_arithmetic");

        Assert.Equal(SqlCapabilityStatus.Translated, capability.Status);
        Assert.Contains(
            "typed in the closed F# AST",
            capability.Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "non-DAY date difference remains fail-closed",
            capability.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("date-math-contract-v1"),
            new SqlExecutionPlanPolicy());
}
