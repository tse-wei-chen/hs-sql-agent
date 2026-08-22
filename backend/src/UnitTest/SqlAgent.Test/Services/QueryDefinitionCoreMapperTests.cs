using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Mapping;
using SqlAgent.Service.Enums;
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
    public void Map_FunctionFilter_ProducesCanonicalFilterExpression()
    {
        var definition = new QueryDefinition
        {
            TableName = "orders",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "COUNT",
                    Arguments = [new FieldSelectCondition { FieldName = "id" }],
                    FilterWhereConditions =
                    [
                        new BasicWhereCondition { FieldName = "status", Operator = "=", Value = "open" }
                    ]
                }
            ]
        };

        var statement = Assert.IsType<SelectStatement>(QueryDefinitionCoreMapper.Map(definition));
        var filter = Assert.IsType<FilterExpr>(statement.Select[0].Expression);

        Assert.IsType<FunctionCallExpr>(filter.Expression);
        Assert.IsType<BinaryExpr>(filter.Predicate);
    }

    [Fact]
    public void Map_Window_PreservesPartitionOrderAndFrame()
    {
        var definition = new QueryDefinition
        {
            TableName = "orders",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "SUM",
                    Arguments = [new FieldSelectCondition { FieldName = "amount" }],
                    Window = new WindowDefinition
                    {
                        PartitionBy = [new FieldGroupByCondition { FieldName = "customer_id" }],
                        OrderBy = [new FieldOrderByCondition { FieldName = "created_at", Direction = SortDirection.Asc }],
                        Frame = new WindowFrameDefinition
                        {
                            Unit = WindowFrameUnit.Rows,
                            Start = new WindowFrameBound
                            {
                                Kind = WindowFrameBoundKind.Preceding,
                                Offset = 1
                            },
                            End = new WindowFrameBound { Kind = WindowFrameBoundKind.CurrentRow }
                        }
                    }
                }
            ]
        };

        var statement = Assert.IsType<SelectStatement>(QueryDefinitionCoreMapper.Map(definition));
        var windowed = Assert.IsType<WindowedExpr>(statement.Select[0].Expression);

        Assert.Single(windowed.Window.PartitionBy);
        Assert.Single(windowed.Window.OrderBy);
        Assert.NotNull(windowed.Window.Frame);
        Assert.Equal(WindowFrameUnitKind.Rows, windowed.Window.Frame!.Unit);
        Assert.Equal(WindowFrameBoundKindCore.Preceding, windowed.Window.Frame.Start.Kind);
        Assert.Equal(1, windowed.Window.Frame.Start.Offset);
        Assert.Equal(WindowFrameBoundKindCore.CurrentRow, windowed.Window.Frame.End!.Kind);
    }
}
