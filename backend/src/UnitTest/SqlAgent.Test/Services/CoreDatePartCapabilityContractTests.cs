using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDatePartCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, true)]
    [InlineData(SqlAgentToolType.MySQL, false)]
    [InlineData(SqlAgentToolType.Sqlite, false)]
    [InlineData(SqlAgentToolType.MsSqlServer, false)]
    [InlineData(SqlAgentToolType.Oracle, false)]
    [InlineData(SqlAgentToolType.Firebird, false)]
    public void QuarterFacade_StaysAlignedWithCapabilityMatrix(
        SqlAgentToolType provider,
        bool supported)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "temporal.date_part.quarter");

        Assert.Equal(supported, SqlQuarterDatePartCapabilityRules.SupportsTarget(provider));
        Assert.Equal(
            supported ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
            capability.Status);
        Assert.Equal(
            supported,
            SqlQuarterDatePartCapabilityRules.TargetValidationError(provider) is null);
    }

    [Theory]
    [InlineData("YEAR")]
    [InlineData("MONTH")]
    [InlineData("DAY")]
    [InlineData("QUARTER")]
    public void PostgresCompile_AllRepresentedDateParts_ReachNativeRenderer(string part)
    {
        var command = Compile(
            "SELECT EXTRACT(" + part + " FROM order_date) FROM public.orders");

        Assert.Contains(
            "EXTRACT(" + part + " FROM",
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PostgresParse_UnrepresentedDatePart_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT EXTRACT(WEEK FROM order_date) FROM public.orders",
                SqlAgentToolType.Postgres));

        Assert.Contains(
            "not yet represented by the canonical date-part family",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(string sql) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                sql,
                SqlAgentToolType.Postgres),
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("date-part-contract-v1"),
            new SqlExecutionPlanPolicy());
}
