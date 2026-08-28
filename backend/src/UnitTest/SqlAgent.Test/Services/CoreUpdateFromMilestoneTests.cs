using Xunit;

namespace SqlAgent.Test.Services;

public class CoreUpdateFromMilestoneTests
{
    [Fact]
    public void ParseUpdateFrom_Postgres_ProducesCanonicalSourceList()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = quantity + 1 FROM warehouse WHERE inventory.id = warehouse.inventory_id",
            SqlAgentToolType.Postgres);

        var update = Assert.IsType<UpdateStatement>(parsed.Statement);
        var source = Assert.Single(update.From);
        Assert.Equal("warehouse", Assert.Single(source.Name.Parts).Value, ignoreCase: true);
    }

    [Fact]
    public void ParseUpdateFrom_MultiplePostgresSources_PreservesOrder()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = quantity + 1 FROM warehouse, region WHERE inventory.id = warehouse.inventory_id",
            SqlAgentToolType.Postgres);

        var update = Assert.IsType<UpdateStatement>(parsed.Statement);
        Assert.Equal(new[] { "warehouse", "region" }, update.From.Select(source => Assert.Single(source.Name.Parts).Value).ToArray());
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.MsSqlServer)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ParseUpdateFrom_NonPostgresSourceDialect_FailsClosed(SqlAgentToolType sourceDialect)
    {
        var error = Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = 1 FROM warehouse WHERE inventory.id = warehouse.inventory_id",
            sourceDialect));

        Assert.Contains("PostgreSQL source dialect", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileUpdateFrom_RemainsFailClosedUntilSemanticPipelineSupportsNewNode()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = quantity + 1 FROM warehouse WHERE inventory.id = warehouse.inventory_id",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() => CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("remains fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("binder", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native lowering", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseUpdateFrom_Alias_RemainsFailClosedInFirstSlice()
    {
        var error = Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = 1 FROM warehouse AS w WHERE inventory.id = w.inventory_id",
            SqlAgentToolType.Postgres));

        Assert.Contains("aliases", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
