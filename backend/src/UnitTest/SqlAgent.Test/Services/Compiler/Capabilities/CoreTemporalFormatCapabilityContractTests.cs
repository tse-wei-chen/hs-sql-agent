using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreTemporalFormatCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Translated, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Translated, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Translated, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Translated, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected, SqlCapabilityStatus.Rejected)]
    public void Matrix_TemporalFormatCapabilities_StayAligned(
        SqlAgentToolType provider,
        SqlCapabilityStatus dateFormatStatus,
        SqlCapabilityStatus formattedParseStatus)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);

        Assert.Equal(
            dateFormatStatus,
            Assert.Single(
                matrix.Capabilities,
                item => item.Id == "temporal.date_format").Status);
        Assert.Equal(
            formattedParseStatus,
            Assert.Single(
                matrix.Capabilities,
                item => item.Id == "temporal.formatted_parse").Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "TO_CHAR")]
    [InlineData(SqlAgentToolType.MySQL, "DATE_FORMAT")]
    [InlineData(SqlAgentToolType.Sqlite, "STRFTIME")]
    [InlineData(SqlAgentToolType.MsSqlServer, "FORMAT")]
    [InlineData(SqlAgentToolType.Oracle, "TO_CHAR")]
    public void Compile_DateFormat_ReachesDeclaredNativeRenderer(
        SqlAgentToolType targetProvider,
        string expectedFunction)
    {
        var command = Compile(
            "SELECT DATE_FORMAT(created_at, '%Y-%m-%d') FROM events",
            SqlAgentToolType.MySQL,
            targetProvider);

        Assert.Contains(
            expectedFunction,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "TO_DATE")]
    [InlineData(SqlAgentToolType.MySQL, "STR_TO_DATE")]
    [InlineData(SqlAgentToolType.Oracle, "TO_DATE")]
    public void Compile_FormattedParse_ReachesDeclaredNativeRenderer(
        SqlAgentToolType targetProvider,
        string expectedFunction)
    {
        var command = Compile(
            "SELECT TO_DATE('2026-08-27', 'YYYY-MM-DD')",
            SqlAgentToolType.Oracle,
            targetProvider);

        Assert.Contains(
            expectedFunction,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Sqlite)]
    public void Compile_FormattedParse_UndeclaredTargets_FailAtCapabilityBoundary(
        SqlAgentToolType targetProvider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT TO_DATE('2026-08-27', 'YYYY-MM-DD')",
            SqlAgentToolType.Oracle,
            targetProvider));

        Assert.Contains(
            "function.date_parse",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, sourceDialect),
            targetProvider,
            new SqlPlanValidationContext("temporal-format-contract-v1"),
            new SqlExecutionPlanPolicy());
}
