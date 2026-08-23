using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class QueryFactsCteVisibilityTests
{
    [Fact]
    public void Bind_LaterCteAlias_IsNotVisibleInsideEarlierCteBody()
    {
        var definition = new QueryDefinition
        {
            TableName = "first_cte",
            CteConditions =
            [
                new CteCondition
                {
                    CteAliasName = "first_cte",
                    Query = new QueryDefinition { TableName = "later_cte" }
                },
                new CteCondition
                {
                    CteAliasName = "later_cte",
                    Query = new QueryDefinition { TableName = "physical.source" }
                }
            ]
        };

        var facts = QueryFactsBinder.Bind(definition);

        Assert.Contains("later_cte", facts.ReferencedTables);
        Assert.Contains("physical.source", facts.ReferencedTables);
        Assert.DoesNotContain("first_cte", facts.ReferencedTables);
    }

    [Fact]
    public void Bind_SetOperationBranch_CanSeeEnclosingCtes()
    {
        var definition = new QueryDefinition
        {
            TableName = "recent",
            CteConditions =
            [
                new CteCondition
                {
                    CteAliasName = "recent",
                    Query = new QueryDefinition { TableName = "sales.orders" }
                }
            ],
            CombineConditions =
            [
                new CombineCondition
                {
                    Query = new QueryDefinition { TableName = "recent" }
                }
            ]
        };

        var facts = QueryFactsBinder.Bind(definition);

        Assert.Single(facts.ReferencedTables);
        Assert.Contains("sales.orders", facts.ReferencedTables);
        Assert.DoesNotContain("recent", facts.ReferencedTables);
    }
}
