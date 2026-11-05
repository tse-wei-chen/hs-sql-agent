using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreMySqlDateFunctionCapabilityTests
{
    [Fact]
    public void Compile_MySqlDateFunction_RemainsNativeForMySqlTarget()
    {
        var command = Compile(
            "SELECT DATE(created_at) FROM events",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL);

        Assert.Contains("DATE(", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CORE_DATE_ONLY", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_MySqlDateFunctionToPostgres_FailsClosedWithoutTypedOperandProof()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT DATE(created_at) FROM events",
            SqlAgentToolType.MySQL,
            SqlAgentToolType.Postgres));

        Assert.Contains("temporal.date_only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("temporal value", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT DATE() FROM events")]
    [InlineData("SELECT DATE(created_at, updated_at) FROM events")]
    public void Compile_MySqlDateFunctionWrongArity_FailsAtSourceCapability(string sql)
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            sql,
            SqlAgentToolType.MySQL,
            SqlAgentToolType.MySQL));

        Assert.Contains("DATE(expr)", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exactly 1 argument", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected)]
    public void Matrix_DateOnlyCapability_TracksProvenTargetSemantics(
        SqlAgentToolType provider,
        SqlCapabilityStatus expectedStatus)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "temporal.date_only");

        Assert.Equal(expectedStatus, capability.Status);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType source,
        SqlAgentToolType target) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, source),
            target,
            new SqlPlanValidationContext("mysql-date-function-v1"),
            new SqlExecutionPlanPolicy());
}
