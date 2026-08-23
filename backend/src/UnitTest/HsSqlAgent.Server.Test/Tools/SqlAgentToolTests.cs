using System.ComponentModel;
using System.Reflection;
using Admin.Service.Models;
using HsSqlAgent.Server.Tools;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
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
        Assert.Equal(typeof(CancellationToken), dmlParams[2].ParameterType);
        Assert.Equal("cancellationToken", dmlParams[2].Name);
    }

    [Fact]
    public void BinderFacts_ShouldInspectNestedWindowSubqueries()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT LAG(order_date) OVER (" +
            "PARTITION BY COALESCE((SELECT id FROM secret_partition_table), 0) " +
            "ORDER BY COALESCE((SELECT id FROM secret_order_table), 0)) FROM orders",
            SqlAgentToolType.Postgres);

        var facts = new SqlAstBinder().Bind(parsed).Facts;

        Assert.Contains("orders", facts.ReferencedTables);
        Assert.Contains("secret_partition_table", facts.ReferencedTables);
        Assert.Contains("secret_order_table", facts.ReferencedTables);
        Assert.True(facts.ContainsSubquery);
    }

    [Fact]
    public void BinderFacts_ShouldNotTreatTableAliasAsPhysicalTableExemption()
    {
        var parsed = CoreSqlTextParser.ParseQuery(
            "SELECT secret.id FROM secret AS secret",
            SqlAgentToolType.Postgres);

        var facts = new SqlAstBinder().Bind(parsed).Facts;

        Assert.Contains("secret", facts.ReferencedTables);
    }
}
