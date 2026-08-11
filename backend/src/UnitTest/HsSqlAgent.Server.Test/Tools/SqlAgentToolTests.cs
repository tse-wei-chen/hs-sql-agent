using System.ComponentModel;
using System.Reflection;
using Admin.Service.Models;
using HsSqlAgent.Server.Tools;
using SqlAgent.Service.Models;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class SqlAgentToolTests
{
    [Fact]
    public void Discovery_ShouldUseExistingSchemaToolsWithoutExtraCapabilityOrSemanticTools()
    {
        Assert.Null(typeof(SqlAgentTool).GetMethod("GetSqlCapabilities"));
        Assert.Null(typeof(SqlAgentTool).GetMethod("GetSemanticModel"));
        Assert.NotNull(typeof(SqlAgentTool).GetMethod(nameof(SqlAgentTool.GetTables)));
        Assert.NotNull(typeof(SqlAgentTool).GetMethod(nameof(SqlAgentTool.GetColumns)));
    }

    [Fact]
    public void SemanticDescriptions_ShouldPreserveRelationshipAndMetricContext()
    {
        var relationshipMethod = typeof(SqlAgentTool).GetMethod(
            "DescribeRelationship", BindingFlags.Static | BindingFlags.NonPublic);
        var metricMethod = typeof(SqlAgentTool).GetMethod(
            "DescribeMetric", BindingFlags.Static | BindingFlags.NonPublic);

        var relationship = (string)relationshipMethod!.Invoke(null,
        [
            new DbSemanticRelationshipModel
            {
                Name = "orders_customer", SourceSchema = "main", SourceTable = "orders",
                SourceColumn = "customer_id", TargetSchema = "main", TargetTable = "customers",
                TargetColumn = "id", Cardinality = "many-to-one", Direction = "source-to-target"
            }
        ])!;
        var metric = (string)metricMethod!.Invoke(null,
        [
            new DbSemanticMetricModel
            {
                Name = "revenue", TableName = "orders", Formula = "orders.amount",
                Aggregation = "sum", Synonyms = ["sales"]
            }
        ])!;

        Assert.Contains("main.orders.customer_id -> main.customers.id", relationship);
        Assert.Contains("many-to-one", relationship);
        Assert.Contains("formula=orders.amount", metric);
        Assert.Contains("synonyms=sales", metric);
    }

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
        Assert.Equal(3, dmlParams.Length);
        Assert.Equal(typeof(string), dmlParams[0].ParameterType);
        Assert.Equal("sql", dmlParams[0].Name);
        Assert.Equal(typeof(ModelContextProtocol.Server.McpServer), dmlParams[1].ParameterType);
        Assert.Equal("server", dmlParams[1].Name);
        Assert.Equal(typeof(System.Threading.CancellationToken), dmlParams[2].ParameterType);
        Assert.Equal("cancellationToken", dmlParams[2].Name);
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

    [Fact]
    public void CollectFromQueryDefinition_ShouldNotTreatTableAliasAsPhysicalTableExemption()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var query = new QueryDefinition
        {
            TableName = "secret",
            Alias = "secret",
            SelectColumns = [new FieldSelectCondition { FieldName = "secret.id" }]
        };

        var method = typeof(SqlAgentTool).GetMethod(
            "CollectFromQueryDefinition",
            BindingFlags.Static | BindingFlags.NonPublic);

        method!.Invoke(null, [query, referenced, aliases]);

        Assert.Contains("secret", referenced);
    }

}
