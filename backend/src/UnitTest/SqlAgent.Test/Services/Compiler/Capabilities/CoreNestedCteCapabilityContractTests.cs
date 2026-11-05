using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CoreNestedCteCapabilityContractTests
{
    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Translated)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected)]
    public void Matrix_NestedCteCapabilities_StayAligned(
        SqlAgentToolType provider,
        SqlCapabilityStatus expectedStatus)
    {
        var matrix = SqlCapabilityMatrix.ForProvider(provider);
        var ids = new[]
        {
            "select.cte_derived",
            "select.cte_set_branch",
            "select.cte_scalar_root",
            "select.cte_definition_local",
            "dml.nested_cte_scope"
        };

        foreach (var id in ids)
        {
            Assert.Equal(
                expectedStatus,
                Assert.Single(
                    matrix.Capabilities,
                    item => item.Id == id).Status);
        }
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, true)]
    [InlineData(SqlAgentToolType.MySQL, true)]
    [InlineData(SqlAgentToolType.Sqlite, true)]
    [InlineData(SqlAgentToolType.MsSqlServer, false)]
    [InlineData(SqlAgentToolType.Oracle, false)]
    [InlineData(SqlAgentToolType.Firebird, false)]
    public void Compile_DerivedNestedCte_TracksDeclaredTargetContract(
        SqlAgentToolType targetProvider,
        bool shouldCompile)
    {
        const string sql =
            "SELECT d.id FROM " +
            "(WITH active AS (SELECT id FROM users) SELECT id FROM active) AS d";

        if (shouldCompile)
        {
            var command = Compile(sql, targetProvider);
            Assert.Contains("WITH ", command.Sql, StringComparison.OrdinalIgnoreCase);
            return;
        }

        var error = Assert.Throws<SqlCompilationException>(() =>
            Compile(sql, targetProvider));
        Assert.Contains(
            "select.cte_scope",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static CompiledSqlCommand Compile(
        string sql,
        SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("nested-cte-contract-v1"),
            new SqlExecutionPlanPolicy());
}
