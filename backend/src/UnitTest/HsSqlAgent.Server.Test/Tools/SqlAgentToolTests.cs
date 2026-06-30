using System.ComponentModel;
using System.Reflection;
using HsSqlAgent.Server.Tools;
using SqlAgent.Service.Models;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class SqlAgentToolTests
{
    [Fact]
    public void ExecuteSqlTools_ShouldExposeSqlStringParameters()
    {
        var queryMethod = typeof(SqlAgentTool).GetMethod(nameof(SqlAgentTool.ExecuteQuerySql));
        var dmlMethod = typeof(SqlAgentTool).GetMethod(nameof(SqlAgentTool.ExecuteDmlSql));

        var queryDescription = queryMethod?.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var queryParam = Assert.Single(queryMethod!.GetParameters());
        var dmlParams = dmlMethod!.GetParameters();

        Assert.NotNull(queryDescription);
        Assert.Contains("SELECT SQL", queryDescription);
        Assert.Equal(typeof(string), queryParam.ParameterType);
        Assert.Equal("sql", queryParam.Name);
        Assert.Equal(2, dmlParams.Length);
        Assert.Equal(typeof(string), dmlParams[0].ParameterType);
        Assert.Equal("sql", dmlParams[0].Name);
        Assert.Equal(typeof(string), dmlParams[1].ParameterType);
        Assert.True(dmlParams[1].HasDefaultValue);
        Assert.Equal("confirmToken", dmlParams[1].Name);
    }

    [Fact]
    public void CollectReferencesAndAliases_ShouldInspectSelectFunctionWindowExpressions()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<SelectCondition> selectColumns =
        [
            new FunctionSelectCondition
            {
                FunctionName = "LAG",
                Arguments = [new FieldSelectCondition { FieldName = "orders.order_date" }],
                Window = new WindowDefinition
                {
                    PartitionBy =
                    [
                        new FunctionGroupByCondition
                        {
                            FunctionName = "COALESCE",
                            Arguments =
                            [
                                new SubQuerySelectCondition
                                {
                                    TableName = "secret_partition_table",
                                    SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
                                }
                            ]
                        }
                    ],
                    OrderBy =
                    [
                        new FunctionOrderByCondition
                        {
                            FunctionName = "COALESCE",
                            Arguments =
                            [
                                new SubQuerySelectCondition
                                {
                                    TableName = "secret_order_table",
                                    SelectColumns = [new FieldSelectCondition { FieldName = "id" }]
                                }
                            ]
                        }
                    ]
                }
            }
        ];

        var method = typeof(SqlAgentTool).GetMethod(
            "CollectReferencesAndAliases",
            BindingFlags.Static | BindingFlags.NonPublic);

        method!.Invoke(null, [null, null, null, null, null, selectColumns, null, referenced, aliases]);

        Assert.Contains("secret_partition_table", referenced);
        Assert.Contains("secret_order_table", referenced);
    }

}
