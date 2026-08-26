using Xunit;

namespace SqlAgent.Test.Services;

public class QueryFactsBinderTests
{
    [Fact]
    public void Bind_CollectsPhysicalTablesAcrossCteJoinAndSubquery()
    {
        var definition = new QueryDefinition
        {
            TableName = "recent",
            Alias = "r",
            CteConditions =
            [
                new CteCondition
                {
                    CteAliasName = "recent",
                    Query = new QueryDefinition { TableName = "sales.orders" }
                }
            ],
            Joins =
            [
                new JoinCondition { Table = "crm.customers", Alias = "c", Type = JoinType.Cross },
                new JoinCondition
                {
                    Alias = "x",
                    Type = JoinType.Cross,
                    SubQuery = new QueryDefinition { TableName = "audit.events" }
                }
            ]
        };

        var facts = BindFacts(definition);

        Assert.Equal(3, facts.ReferencedTables.Count);
        Assert.Contains("sales.orders", facts.ReferencedTables);
        Assert.Contains("crm.customers", facts.ReferencedTables);
        Assert.Contains("audit.events", facts.ReferencedTables);
        Assert.DoesNotContain("recent", facts.ReferencedTables);
        Assert.True(facts.ContainsCte);
        Assert.True(facts.ContainsSubquery);
        Assert.Contains(facts.Aliases, a => a.Alias == "c" && a.Target == "crm.customers");
        Assert.Contains(facts.Aliases, a => a.Alias == "x" && a.Target == "<subquery>");
    }

    [Fact]
    public void Bind_CollectsWhereSubqueryPhysicalTable()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            WhereColumnsAndValues =
            [
                new SubQueryWhereCondition
                {
                    Operator = "EXISTS",
                    SubQuery = new QueryDefinition { TableName = "permissions" }
                }
            ]
        };

        var facts = BindFacts(definition);

        Assert.Contains("users", facts.ReferencedTables);
        Assert.Contains("permissions", facts.ReferencedTables);
        Assert.True(facts.ContainsSubquery);
    }

    [Fact]
    public void Bind_DuplicateAliasInSameScope_FailsClosed()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "x",
            Joins = [new JoinCondition { Table = "orders", Alias = "x", Type = JoinType.Cross }]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => BindFacts(definition));

        Assert.Contains("Duplicate table alias", ex.Message);
    }

    [Fact]
    public void Bind_SameAliasInNestedScope_IsAllowed()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "x",
            WhereColumnsAndValues =
            [
                new SubQueryWhereCondition
                {
                    Operator = "EXISTS",
                    SubQuery = new QueryDefinition { TableName = "orders", Alias = "x" }
                }
            ]
        };

        var facts = BindFacts(definition);

        Assert.Equal(2, facts.Aliases.Count(a => a.Alias == "x"));
        Assert.Equal(2, facts.Aliases.Where(a => a.Alias == "x").Select(a => a.ScopeId).Distinct().Count());
    }

    private static QueryFacts BindFacts(QueryDefinition definition)
    {
        var parsed = new ParsedStatement(
            QueryDefinitionCoreMapper.Map(definition),
            SqlAgentToolType.Postgres);
        return new SqlAstBinder().Bind(parsed).Facts;
    }
}
