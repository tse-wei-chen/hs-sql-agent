using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreStandaloneTimeCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
    public void Matrix_StandaloneTimeCapability_StaysAligned(
        SqlAgentToolType provider,
        SqlCapabilityStatus expectedStatus)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "temporal.standalone_time");

        Assert.Equal(expectedStatus, capability.Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_StandaloneTime_SupportedTargets_RemainAccepted(
        SqlAgentToolType targetProvider)
    {
        var command = Compile(targetProvider);

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
        Assert.Single(command.Parameters);
    }

    [Fact]
    public void Compile_StandaloneTime_Oracle_FailsAtCapabilityBoundary()
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(SqlAgentToolType.Oracle));

        Assert.Contains(
            "literal.time",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "standalone TIME",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT TIME '12:34:56'",
                SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("standalone-time-contract-v1"),
            new SqlExecutionPlanPolicy());
}
