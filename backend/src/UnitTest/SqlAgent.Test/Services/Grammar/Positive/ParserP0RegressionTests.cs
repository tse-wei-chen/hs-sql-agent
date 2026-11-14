using Xunit;

namespace SqlAgent.Test.Services;

public class ParserP0RegressionTests
{
    [Fact]
    public void ParseQuery_CommaFrom_BecomesCrossJoinWithoutLosingEitherTable()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT a.id, b.id FROM alpha a, beta b WHERE a.id = b.id",
            SqlAgentToolType.Postgres);

        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(parsed);
        Assert.Contains("alpha", facts.ReferencedTables);
        Assert.Contains("beta", facts.ReferencedTables);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("comma-from-p0-v2"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("CROSS JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alpha", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("beta", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseQuery_NestedCommaFrom_NormalizesAtNestedScope()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT q.id FROM (SELECT a.id FROM alpha a, beta b WHERE a.id = b.id) q",
            SqlAgentToolType.Postgres);

        var facts = HsSqlAgent.SqlCore.SqlCoreInspection.GetQueryFacts(parsed);
        Assert.Contains("alpha", facts.ReferencedTables);
        Assert.Contains("beta", facts.ReferencedTables);
        Assert.True(facts.ContainsSubquery);

        var command = CoreSqlCompiler.CreateDefault().Compile(
            parsed,
            SqlAgentToolType.Postgres,
            new SqlPlanValidationContext("comma-from-p0-v2"),
            new SqlExecutionPlanPolicy());

        Assert.Contains("CROSS JOIN", command.Sql, StringComparison.OrdinalIgnoreCase);
    }
}
