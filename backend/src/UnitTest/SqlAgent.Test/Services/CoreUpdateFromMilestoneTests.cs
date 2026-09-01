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
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void ParseUpdateFrom_UnsupportedSourceDialect_FailsClosed(SqlAgentToolType sourceDialect)
    {
        var error = Assert.Throws<SqlParseException>(() => CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = 1 FROM warehouse WHERE inventory.id = warehouse.inventory_id",
            sourceDialect));

        Assert.Contains("dml.update.from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sourceDialect.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompileUpdateFrom_Postgres_BindsSourcesAndLowersNativeSyntax()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = quantity + 1 FROM warehouse WHERE inventory.id = warehouse.inventory_id",
            SqlAgentToolType.Postgres);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Equal(SqlStatementKind.Update, command.Kind);
        Assert.Contains("UPDATE", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FROM", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warehouse", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, Convert.ToInt64(Assert.Single(command.Parameters).Value));
    }

    [Theory]
    [InlineData(SqlAgentToolType.MySQL)]
    [InlineData(SqlAgentToolType.Oracle)]
    [InlineData(SqlAgentToolType.Sqlite)]
    [InlineData(SqlAgentToolType.Firebird)]
    public void CompileUpdateFrom_UnsupportedTarget_RemainsFailClosed(SqlAgentToolType targetProvider)
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = quantity + 1 FROM warehouse WHERE inventory.id = warehouse.inventory_id",
            SqlAgentToolType.Postgres);

        var error = Assert.Throws<SqlCompilationException>(() => CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            targetProvider,
            new SqlPlanValidationContext("policy-v1")));

        Assert.Contains("dml.update.from", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseUpdateFrom_Alias_IsRepresentedAndCompiles()
    {
        var parsed = CoreSqlTextParser.ParseDml(
            "UPDATE inventory SET quantity = 1 FROM warehouse AS w WHERE inventory.id = w.inventory_id",
            SqlAgentToolType.Postgres);

        var update = Assert.IsType<UpdateStatement>(parsed.Statement);
        var source = Assert.Single(update.From);
        Assert.Equal("w", source.Alias?.Value, ignoreCase: true);

        var command = CoreDmlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("policy-v1"));

        Assert.Contains("\"warehouse\"", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AS \"w\"", command.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
