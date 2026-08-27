using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreScalarBooleanCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Supported, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Supported, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Supported, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected, SqlCapabilityStatus.Rejected)]
    public void Matrix_ScalarBooleanCapabilities_StayAligned(
        SqlAgentToolType provider,
        SqlCapabilityStatus projectionStatus,
        SqlCapabilityStatus updateStatus)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);

        Assert.Equal(
            projectionStatus,
            Assert.Single(
                matrix.Capabilities,
                item => item.Id == "expression.boolean_select").Status);
        Assert.Equal(
            updateStatus,
            Assert.Single(
                matrix.Capabilities,
                item => item.Id == "dml.update.boolean_assignment").Status);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres)]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Compile_BooleanProjection_SupportedTargets_RemainAccepted(
        SqlAgentToolType targetProvider)
    {
        var command = CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(
                "SELECT id = 1 FROM users",
                SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("scalar-boolean-contract-v1"),
            new SqlExecutionPlanPolicy());

        Assert.False(string.IsNullOrWhiteSpace(command.Sql));
    }

    [Theory]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    public void Compile_BooleanProjection_RejectedTargets_KeepCapabilityDiagnostic(
        SqlAgentToolType targetProvider)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            CoreSqlCompiler.CreateDefault().Compile(
                CoreSqlTextParser.ParseQuery(
                    "SELECT id = 1 FROM users",
                    SqlAgentToolType.Postgres),
                targetProvider,
                new SqlPlanValidationContext("scalar-boolean-contract-v1"),
                new SqlExecutionPlanPolicy()));

        Assert.Contains(
            "expression.boolean_select",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
