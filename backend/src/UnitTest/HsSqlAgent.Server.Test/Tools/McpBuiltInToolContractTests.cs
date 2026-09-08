using System.Reflection;
using Common.Models;
using HsSqlAgent.Server.Models;
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

    [Theory]
    [InlineData(nameof(SqlAgentTool.ExecuteQuerySql), typeof(McpQueryToolResult))]
    [InlineData(nameof(SqlAgentTool.GetSchemas), typeof(McpSchemasToolResult))]
    [InlineData(nameof(SqlAgentTool.GetTables), typeof(McpTablesToolResult))]
    [InlineData(nameof(SqlAgentTool.GetColumns), typeof(McpColumnsToolResult))]
    public void ReadTools_AdvertiseStructuredReadOnlyOutputs(string methodName, Type resultType)
    {
        var method = typeof(SqlAgentTool).GetMethod(methodName);
        Assert.NotNull(method);

        var attribute = Assert.Single(method.GetCustomAttributes<McpServerToolAttribute>());
        Assert.True(attribute.UseStructuredContent);
        Assert.True(attribute.ReadOnly);
        Assert.Equal(typeof(Task<>).MakeGenericType(resultType), method.ReturnType);
    }

    [Fact]
    public void ExecuteDmlSql_AdvertisesStructuredMutatingOutput()
    {
        var method = typeof(SqlAgentTool).GetMethod(nameof(SqlAgentTool.ExecuteDmlSql));
        Assert.NotNull(method);

        var attribute = Assert.Single(method.GetCustomAttributes<McpServerToolAttribute>());
        Assert.True(attribute.UseStructuredContent);
        Assert.False(attribute.ReadOnly);
        Assert.Equal(typeof(Task<McpDmlToolResult>), method.ReturnType);
    }

    [Fact]
    public void DmlStructuredResult_SeparatesApprovalStateFromErrors()
    {
        var pending = McpDmlToolResult.Pending(
            "Postgres",
            2,
            4,
            15,
            "request-1",
            "ticket-7",
            "Pending review.");
        var rejected = McpDmlToolResult.Rejected(
            "Postgres",
            1,
            3,
            9,
            "request-2",
            "Rejected.");
        var failed = McpDmlToolResult.Failed(
            "Postgres",
            1,
            "Server busy.",
            new McpToolError("server.busy", "Server busy.", "Execution", true));

        Assert.Equal("pending", pending.Status);
        Assert.Equal("pending", pending.ApprovalDecision);
        Assert.Equal("ticket-7", pending.ApprovalExternalReference);
        Assert.Null(pending.Error);

        Assert.Equal("rejected", rejected.Status);
        Assert.Equal("rejected", rejected.ApprovalDecision);
        Assert.Null(rejected.Error);

        Assert.Equal("failed", failed.Status);
        Assert.Null(failed.ApprovalDecision);
        Assert.NotNull(failed.Error);
        Assert.True(failed.Error.Retryable);
    }
}
