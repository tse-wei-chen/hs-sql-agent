using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreDatePartCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, true)]
    [InlineData(SqlAgentToolType.MySQL, true)]
    [InlineData(SqlAgentToolType.Sqlite, true)]
    [InlineData(SqlAgentToolType.MsSqlServer, true)]
    [InlineData(SqlAgentToolType.Oracle, false)]
    [InlineData(SqlAgentToolType.Firebird, true)]
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
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
    public void ClockFacade_ExposesActualProviderCapability(
        SqlAgentToolType provider,
        SqlCapabilityStatus expected)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "temporal.date_part.clock");

        Assert.Equal(expected, capability.Status);
    }

    [Theory]
    [InlineData("YEAR")]
    [InlineData("MONTH")]
    [InlineData("DAY")]
    [InlineData("QUARTER")]
    [InlineData("HOUR")]
    [InlineData("WEEK")]
    [InlineData("EPOCH")]
    [InlineData("ISOYEAR")]
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
    public void PostgresParse_UnknownDatePart_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT EXTRACT(FOOBAR FROM order_date) FROM public.orders",
                SqlAgentToolType.Postgres));

        Assert.Contains("FOOBAR", error.Message, StringComparison.OrdinalIgnoreCase);
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
