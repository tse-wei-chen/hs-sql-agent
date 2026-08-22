using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using Xunit;

namespace SqlAgent.Test.Services;

public class CoreProviderFinalizationTests
{
    [Fact]
    public void SubQueryAlias_IsVisibleThroughBaseSelectConditionContract()
    {
        SelectCondition condition = new SubQuerySelectCondition
        {
            TableName = "orders",
            Alias = "order_count"
        };

        Assert.Equal("order_count", condition.Alias);
    }

    [Fact]
    public void ScalarSubqueryProjection_EmitsOuterAlias()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "u",
            SelectColumns =
            [
                new SubQuerySelectCondition
                {
                    TableName = "orders",
                    SelectColumns =
                    [
                        new FunctionSelectCondition
                        {
                            FunctionName = "COUNT",
                            Arguments = [new FieldSelectCondition { FieldName = "id" }]
                        }
                    ],
                    WhereColumnsAndValues =
                    [
                        new ColumnCompareWhereCondition
                        {
                            LeftFieldName = "user_id",
                            Operator = "=",
                            RightFieldName = "u.id"
                        }
                    ],
                    Alias = "order_count"
                }
            ]
        };

        var command = Compile(definition, SqlAgentToolType.Postgres);

        Assert.Contains("AS \"order_count\"", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Postgres_UnquotedIdentifiersFoldToLowercaseBeforeQuoting()
    {
        var definition = new QueryDefinition
        {
            TableName = "users",
            Alias = "u",
            SelectColumns = [new FieldSelectCondition { FieldName = "u.Name" }]
        };

        var command = Compile(definition, SqlAgentToolType.Postgres);

        Assert.Contains("\"u\".\"name\"", command.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"u\".\"Name\"", command.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Firebird_NumericLiteralOnlyExpressionsHaveExplicitParameterTypes()
    {
        var definition = new QueryDefinition
        {
            TableName = "orders",
            SelectColumns =
            [
                new FunctionSelectCondition
                {
                    FunctionName = "ROUND",
                    Arguments =
                    [
                        new ConstantSelectCondition { Constant = 1.25m },
                        new ConstantSelectCondition { Constant = 2 }
                    ]
                }
            ]
        };

        var command = Compile(definition, SqlAgentToolType.Firebird);

        Assert.Contains("CAST(@p0 AS DECIMAL", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAST(@p1 AS INTEGER)", command.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<decimal>(command.Parameters[0].Value);
        Assert.IsType<int>(command.Parameters[1].Value);
    }

    private static CompiledSqlCommand Compile(QueryDefinition definition, SqlAgentToolType provider) =>
        CoreSqlCompiler.CreateDefault().Compile(
            definition,
            provider,
            provider,
            new SqlPlanValidationContext("provider-finalization-test"),
            new SqlExecutionPlanPolicy());
}
