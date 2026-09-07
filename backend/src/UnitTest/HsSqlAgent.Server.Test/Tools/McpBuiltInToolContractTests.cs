using System.Reflection;
using Common.Models;
using HsSqlAgent.Server.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class McpBuiltInToolContractTests
{
    [Fact]
    public void AnnotatedBuiltInSurface_MatchesCanonicalCatalog()
    {
        var annotatedMethods = typeof(SqlAgentTool)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), false).Length != 0)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                nameof(SqlAgentTool.ExecuteDmlSql),
                nameof(SqlAgentTool.ExecuteQuerySql),
                nameof(SqlAgentTool.GetColumns),
                nameof(SqlAgentTool.GetSchemas),
                nameof(SqlAgentTool.GetTables)
            ],
            annotatedMethods);

        Assert.Equal(5, McpBuiltInTools.Names.Count);
        Assert.DoesNotContain(
            "update_semantic_layer",
            McpBuiltInTools.Names.AsEnumerable());
    }

    [Fact]
    public void DefaultSelection_IsFourReadQueryTools_AndExcludesDml()
    {
        Assert.Equal(4, McpBuiltInTools.DefaultNames.Count);
        Assert.Contains(McpBuiltInTools.GetSchemas, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.Contains(McpBuiltInTools.GetTables, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.Contains(McpBuiltInTools.GetColumns, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.Contains(McpBuiltInTools.ExecuteQuerySql, McpBuiltInTools.DefaultNames.AsEnumerable());
        Assert.DoesNotContain(McpBuiltInTools.ExecuteDmlSql, McpBuiltInTools.DefaultNames.AsEnumerable());

        var dml = Assert.Single(McpBuiltInTools.Catalog, tool => tool.Name == McpBuiltInTools.ExecuteDmlSql);
        Assert.False(dml.DefaultSelected);
        Assert.Equal("high", dml.Risk);
    }
}
