using Xunit;

namespace SqlAgent.Test.Services;

public sealed class CorePostgresDistinctOnCapabilityTests
{
    [Fact]
    public void Parse_DistinctOn_IsStructuredInCompatibilityAst()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT DISTINCT ON (customer_id) customer_id, created_at " +
            "FROM orders ORDER BY customer_id, created_at DESC",
            SqlAgentToolType.Postgres);

        var select = Assert.IsType<SelectStatement>(parsed.Statement);
        Assert.True(select.Distinct);
        Assert.Single(select.DistinctOn);
        Assert.Equal(2, select.Select.Length);
    }

    [Fact]
    public void Compile_DistinctOn_RendersNativePostgresSemantics()
    {
        var command = Compile(
            "SELECT DISTINCT ON (customer_id) customer_id, created_at " +
            "FROM orders ORDER BY customer_id, created_at DESC",
            SqlAgentToolType.Postgres);

        Assert.Contains("SELECT DISTINCT ON (", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DistinctOnWithoutOrderBy_RemainsLegalPostgresSyntax()
    {
        var command = Compile(
            "SELECT DISTINCT ON (customer_id) customer_id, created_at FROM orders",
            SqlAgentToolType.Postgres);

        Assert.Contains("SELECT DISTINCT ON (", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_DistinctOnMultipleExpressions_PreservesStructuredKeys()
    {
        var command = Compile(
            "SELECT DISTINCT ON (tenant_id, customer_id) tenant_id, customer_id, created_at " +
            "FROM orders ORDER BY tenant_id, customer_id, created_at DESC",
            SqlAgentToolType.Postgres);

        Assert.Contains("DISTINCT ON (", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(",", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_DistinctOnToNonPostgresTarget_FailsClosed()
    {
        var error = Assert.Throws<SqlCompilationException>(() => Compile(
            "SELECT DISTINCT ON (customer_id) customer_id, created_at FROM orders ORDER BY customer_id",
            SqlAgentToolType.MySQL));

        Assert.Contains("select.distinct_on", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_DistinctOnFromNonPostgresSource_FailsAtSourceBoundary()
    {
        var error = Assert.Throws<SqlParseException>(() =>
            CoreSqlTextParser.ParseQuery(
                "SELECT DISTINCT ON (customer_id) customer_id FROM orders",
                SqlAgentToolType.MySQL));

        Assert.Contains("select.distinct_on", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspection_DistinctOnExpression_TraversesNestedSubquery()
    {
        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(
            "SELECT DISTINCT ON ((SELECT MAX(id) FROM archived_orders)) id FROM orders",
            SqlAgentToolType.Postgres);

        Assert.Contains("orders", facts.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("archived_orders", facts.Tables, StringComparer.OrdinalIgnoreCase);
        Assert.True(facts.ContainsSubquery);
    }

    [Theory]
    [InlineData(SqlAgentToolType.Postgres, SqlCapabilityStatus.Supported)]
    [InlineData(SqlAgentToolType.MySQL, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Sqlite, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.MsSqlServer, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Oracle, SqlCapabilityStatus.Rejected)]
    [InlineData(SqlAgentToolType.Firebird, SqlCapabilityStatus.Rejected)]
    public void Matrix_DistinctOnCapability_TracksProvenTargetSemantics(
        SqlAgentToolType provider,
        SqlCapabilityStatus expectedStatus)
    {
        var capability = Assert.Single(
            SqlCapabilityMatrix.ForProvider(provider).Capabilities,
            item => item.Id == "select.distinct_on");

        Assert.Equal(expectedStatus, capability.Status);
    }

    private static CompiledSqlCommand Compile(string sql, SqlAgentToolType targetProvider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            CoreSqlTextParser.ParseQuery(sql, SqlAgentToolType.Postgres),
            targetProvider,
            new SqlPlanValidationContext("postgres-distinct-on-v1"),
            new SqlExecutionPlanPolicy());
}
