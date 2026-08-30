using Xunit;

namespace SqlAgent.Test.Services;

public class CoreDeleteUsingMilestoneTests
{
    [Fact]
    public void ParseDeleteUsing_Postgres_ProducesCanonicalSourceList()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM inventory USING warehouse, region WHERE inventory.id = warehouse.inventory_id",
            SqlAgentToolType.Postgres);

        var delete = Assert.IsType<DeleteStatement>(parsed.Statement);
        Assert.Equal(
            new[] { "warehouse", "region" },
            delete.Using.Select(source => Assert.Single(source.Name.Parts).Value).ToArray());
    }

    [Fact]
    public void CompileDeleteUsing_Postgres_BindsSourcesAndLowersNativeSyntax()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM inventory USING warehouse WHERE inventory.id = warehouse.inventory_id AND warehouse.region_id = 7",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Equal(SqlStatementKind.Delete, command.Kind);
        Assert.Contains("DELETE FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USING", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warehouse", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, Assert.Single(command.Parameters).Value);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileDeleteUsing_NonPostgresTarget_RemainsFailClosed(SqlAgentToolType targetProvider)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM inventory USING warehouse WHERE inventory.id = warehouse.inventory_id",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() => CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("dml.delete.using", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ParseDeleteUsing_NonPostgresSourceDialect_FailsClosed(SqlAgentToolType sourceDialect)
    {
        var error = Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseDml(
            "DELETE FROM inventory USING warehouse WHERE inventory.id = warehouse.inventory_id",
            sourceDialect));

        Assert.Contains("PostgreSQL source dialect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileDeleteUsing_WithoutWhere_StillDeniedByMutationPolicy()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "DELETE FROM inventory USING warehouse",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<UnauthorizedAccessException>(() => CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("without WHERE", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseDeleteUsing_Alias_RemainsFailClosed()
    {
        var error = Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseDml(
            "DELETE FROM inventory USING warehouse AS w WHERE inventory.id = w.inventory_id",
            SqlAgentToolType.Postgres));

        Assert.Contains("aliases", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
