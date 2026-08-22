using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class QueryDefinitionCoreMapperTests
{
    [Fact]
    public void Map_BasicQuery_ProducesCanonicalExpressions()
    {
        var definition = new QueryDefinition
        {
            TableName = "sales.orders",
            Alias = "o",
            SelectColumns =
            [
                new FieldSelectCondition { FieldName = "o.id" },
                new FunctionSelectCondition
                {
                    FunctionName = "SUM",
                    Arguments = [new FieldSelectCondition { FieldName = "o.total" }],
                    Alias = "total"
                }
            ],
            WhereColumnsAndValues =
            [
                new BasicWhereCondition { FieldName = "o.status", Operator = "=", Value = "open" },
                new BasicWhereCondition { FieldName = "o.id", Operator = "IN", Values = [1, 2, 3] }
            ]
        };

        var statement = Assert.IsType<SelectStatement>(QueryDefinitionCoreMapper.Map(definition));

        var source = Assert.IsType<NamedTableSource>(statement.From);
        Assert.Equal("sales", source.Name.Parts[0].Value);
        Assert.Equal("orders", source.Name.Parts[1].Value);
        Assert.Equal("o", source.Alias);
        Assert.Equal(2, statement.Select.Length);
        var predicate = Assert.IsType<BinaryExpr>(statement.Where);
        Assert.Equal("AND", predicate.Operator);
        Assert.IsType<InExpr>(predicate.Right);
    }

    [Fact]
    public void Map_IsNull_PreservesNullPredicate()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }],
            WhereColumnsAndValues =
            [
                new BasicWhereCondition { FieldName = "deleted_at", Operator = "IS NOT", Value = null }
            ]
        };

        var statement = Assert.IsType<SelectStatement>(QueryDefinitionCoreMapper.Map(definition));
        var predicate = Assert.IsType<IsNullExpr>(statement.Where);

        Assert.True(predicate.IsNegated);
    }

    [Fact]
    public void Map_SetOperation_ProducesQueryStatement()
    {
        var definition = new QueryDefinition
        {
            TableName = "active_users",
            SelectColumns = [new FieldSelectCondition { FieldName = "id" }],
            CombineConditions =
            [
                new CombineCondition
                {
                    Query = new QueryDefinition
                    {
                        TableName = "archived_users",
                        SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
                    }
                }
            ]
        };

        var statement = Assert.IsType<QueryStatement>(QueryDefinitionCoreMapper.Map(definition));

        Assert.Single(statement.SetOperations);
        Assert.Equal(SetOperationKind.Union, statement.SetOperations[0].Kind);
    }

    [Fact]
    public void Map_FunctionFilter_FailsClosedUntilCanonicalNodeExists()
    {
        var definition = new QueryDefinition
        {
            TableName = "orders",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "COUNT",
                    Arguments = [new FieldSelectCondition { FieldName = "*" }],
                    FilterWhereConditions =
                    [
                        new BasicWhereCondition { FieldName = "status", Operator = "=", Value = "open" }
                    ]
                }
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => QueryDefinitionCoreMapper.Map(definition));

        Assert.Contains("FILTER", ex.Message);
    }
}
