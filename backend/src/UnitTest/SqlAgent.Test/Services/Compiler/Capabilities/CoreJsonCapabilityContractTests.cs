using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreJsonCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Translated, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Translated, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected, SqlCapabilityStatus.Rejected)]
    public void Matrix_JsonCapabilities_StayAligned(
        SqlAgentToolType provider,
        SqlCapabilityStatus extractStatus,
        SqlCapabilityStatus setStatus)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);

        Assert.Equal(
            extractStatus,
            Assert.Single(
                matrix.Capabilities,
                item => item.Id == "json.extract").Status);
        Assert.Equal(
            setStatus,
            Assert.Single(
                matrix.Capabilities,
                item => item.Id == "json.set").Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "JSONB_EXTRACT_PATH")]
    [InlineData(SqlAgentToolType.MySQL, "JSON_EXTRACT")]
    [InlineData(SqlAgentToolType.Sqlite, "JSON_EXTRACT")]
    public void Compile_JsonExtract_ReachesDeclaredNativeRenderer(
        SqlAgentToolType targetProvider,
        string expectedFunction)
    {
        var command = Compile(
            "SELECT JSON_EXTRACT(payload, '$.user.name') FROM events",
            targetProvider);

        Assert.Contains(
            expectedFunction,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_JsonExtract_UndeclaredTargets_FailAtCapabilityBoundary(
        SqlAgentToolType targetProvider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT JSON_EXTRACT(payload, '$.user.name') FROM events",
            targetProvider));

        Assert.Contains(
            "function.json_extract",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, "JSONB_SET")]
    [InlineData(SqlAgentToolType.MySQL, "JSON_SET")]
    [InlineData(SqlAgentToolType.Sqlite, "JSON_SET")]
    [InlineData(SqlAgentToolType.MsSqlServer, "JSON_MODIFY")]
    public void Compile_JsonSet_ReachesDeclaredNativeRenderer(
        SqlAgentToolType targetProvider,
        string expectedFunction)
    {
        var command = Compile(
            "SELECT JSON_SET(payload, '$.user.name', 'Alice') FROM events",
            targetProvider);

        Assert.Contains(
            expectedFunction,
            command.Sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_JsonSet_UndeclaredTargets_FailAtCapabilityBoundary(
        SqlAgentToolType targetProvider)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT JSON_SET(payload, '$.user.name', 'Alice') FROM events",
            targetProvider));

        Assert.Contains(
            "function.json_set",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.MySQL),
            targetProvider,
            new SqlPlanValidationContext("json-capability-contract-v1"),
            new SqlExecutionPlanPolicy());
}
