using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CorePostgresSetAllCapabilityTests
{
    [Theory]
    [InlineData("INTERSECT ALL", "set.intersect_all")]
    [InlineData("EXCEPT ALL", "set.except_all")]
    public void Compile_PostgresDuplicatePreservingSetOperator_RendersNatively(
        string setOperator,
        string capabilityId)
    {
        var command = Compile(
            $"SELECT id FROM alpha {setOperator} SELECT id FROM beta",
            SqlAgentToolType.Postgres);

        Assert.Contains(setOperator, command.Sql, StringComparison.OrdinalIgnoreCase);

        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(SqlAgentToolType.Postgres).Capabilities,
            item => item.Id == capabilityId);
        Assert.Equal(SqlCapabilityStatus.Supported, capability.Status);
    }

    [Theory]
    [InlineData("INTERSECT ALL", "set.intersect_all")]
    [InlineData("EXCEPT ALL", "set.except_all")]
    public void Compile_PostgresDuplicatePreservingSetOperator_ToMySqlFailsBeforeRender(
        string setOperator,
        string capabilityId)
    {
        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(
                $"SELECT id FROM alpha {setOperator} SELECT id FROM beta",
                SqlAgentToolType.MySQL));

        Assert.Contains(capabilityId, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target provider MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void Matrix_DuplicatePreservingSetOperators_RemainRejectedWithoutProof(
        SqlAgentToolType provider)
    {
        var capabilities = SqlCapabilityMatrix.ForProvider(provider).Capabilities;

        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Assert.Single(capabilities, item => item.Id == "set.intersect_all").Status);
        Assert.Equal(
            SqlCapabilityStatus.Rejected,
            Assert.Single(capabilities, item => item.Id == "set.except_all").Status);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("postgres-set-all-v1"),
            new SqlExecutionPlanPolicy());
}
