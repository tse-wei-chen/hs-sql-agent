using System.Text.Json;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using HsSqlAgent.Server.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class CustomToolProxyTests
{
    private sealed class AcceptingApprovalClient : IDmlApprovalClient
    {
        public bool SupportsElicitation => true;
        public ElicitRequestParams? LastRequest { get; private set; }
        public int RequestCount { get; private set; }

        public ValueTask<ElicitResult> ElicitAsync(
            ElicitRequestParams request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            RequestCount++;
            return ValueTask.FromResult(new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement>
                {
                    ["approve"] = JsonSerializer.SerializeToElement(true)
                }
            });
        }
    }

    private readonly Mock<ICustomSqlToolService> _toolServiceMock;
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly Mock<ISqlStrategyFactory> _strategyFactoryMock;
    private readonly Mock<IAuditService> _auditServiceMock;
    private readonly Mock<IQueryValueParserService> _queryValueParserMock;
    private readonly CustomToolProxy _proxy;

    public CustomToolProxyTests()
    {
        _toolServiceMock = new Mock<ICustomSqlToolService>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _configMock = new Mock<IConfiguration>();
        _strategyFactoryMock = new Mock<ISqlStrategyFactory>();
        _auditServiceMock = new Mock<IAuditService>();
        _queryValueParserMock = new Mock<IQueryValueParserService>();

        var context = new DefaultHttpContext();
        context.Items[Common.Models.McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(context);

        _proxy = new CustomToolProxy("test_tool",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _configMock.Object,
            _strategyFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object);
    }

    [Fact]
    public async Task Execute_ShouldReturnError_WhenToolNotFound()
    {
        _toolServiceMock.Setup(t => t.GetToolByNameAsync("test_tool"))
            .ReturnsAsync((CustomSqlTool?)null);

        var args = JsonSerializer.SerializeToElement(new { });
        var result = await _proxy.Execute(args);

        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Execute_ShouldReplaceParameters_ForQueryTool()
    {
        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            DefinitionJson = """{ "tableName": "users", "alias": "u", "selectColumns": [ { "type": "field", "fieldName": "{{colName}}" } ] }"""
        };
        _toolServiceMock.Setup(t => t.GetToolByNameAsync("test_tool"))
            .ReturnsAsync(tool);

        _queryValueParserMock.Setup(q => q.UnwrapJsonElement(It.IsAny<JsonElement>()))
            .Returns("email");

        var strategyMock = new Mock<ISqlStrategy>();
        strategyMock.Setup(s => s.ExecuteQueryAsync(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("\"result\": [{ \"email\": \"test@example.com\" }]");
        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres))
            .Returns(strategyMock.Object);

        var args = JsonSerializer.SerializeToElement(new { colName = "email" });
        var result = await _proxy.Execute(args);

        Assert.Contains("result", result);
        strategyMock.Verify(s => s.ExecuteQueryAsync(
            It.Is<QueryDefinition>(q => q.TableName == "users"),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ShouldReturnError_WhenSqlConfigMissing()
    {
        var emptyContext = new DefaultHttpContext();
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(emptyContext);

        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            DefinitionJson = "{}"
        };
        _toolServiceMock.Setup(t => t.GetToolByNameAsync("test_tool"))
            .ReturnsAsync(tool);

        var args = JsonSerializer.SerializeToElement(new { });
        var result = await _proxy.Execute(args);

        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ShouldRequireElicitationForDmlTool()
    {
        var tool = new CustomSqlTool
        {
            Name = "delete_user",
            Type = "DML",
            DefinitionJson = """{ "operation": "delete", "tableName": "users", "confirmToken": "caller-controlled" }"""
        };
        _toolServiceMock.Setup(t => t.GetToolByNameAsync("delete_user"))
            .ReturnsAsync(tool);

        var strategyMock = new Mock<ISqlStrategy>();
        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres))
            .Returns(strategyMock.Object);

        var dmlProxy = new CustomToolProxy("delete_user",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _configMock.Object,
            _strategyFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object);

        var args = JsonSerializer.SerializeToElement(new { });
        var result = await dmlProxy.Execute(
            args,
            server: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("does not support", result, StringComparison.OrdinalIgnoreCase);
        strategyMock.Verify(s => s.ExecuteDmlAsync(
            It.IsAny<string>(),
            It.IsAny<DmlDefinition>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_ShouldIgnoreCallerToken_AndCommitOnlyAfterApproval()
    {
        var tool = new CustomSqlTool
        {
            Name = "delete_user",
            Type = "DML",
            DefinitionJson = """{ "operation": "delete", "tableName": "users", "confirmToken": "caller-controlled" }"""
        };
        _toolServiceMock.Setup(t => t.GetToolByNameAsync("delete_user"))
            .ReturnsAsync(tool);

        var observedTokens = new List<string?>();
        var strategyMock = new Mock<ISqlStrategy>();
        strategyMock
            .Setup(s => s.ExecuteDmlAsync(
                It.IsAny<string>(),
                It.IsAny<DmlDefinition>(),
                It.IsAny<CancellationToken>()))
            .Callback<string?, DmlDefinition?, CancellationToken>((_, dml, _) =>
                observedTokens.Add(dml?.ConfirmToken))
            .ReturnsAsync(() => observedTokens.Count == 1
                ? "Dry Run Result | affectedRows=1 | TokenRequired=server-token | Security Note: not committed."
                : "Success | affectedRows=1 | Operation Committed.");
        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres))
            .Returns(strategyMock.Object);

        var approvalClient = new AcceptingApprovalClient();

        var dmlProxy = new CustomToolProxy("delete_user",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _configMock.Object,
            _strategyFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object);

        var result = await dmlProxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            approvalClient,
            TestContext.Current.CancellationToken);

        Assert.StartsWith("Success", result);
        Assert.Equal([null, "server-token"], observedTokens);
        Assert.Equal(1, approvalClient.RequestCount);
        Assert.NotNull(approvalClient.LastRequest);
        Assert.Contains("delete_user", approvalClient.LastRequest.Message);
        Assert.Contains("1 row", approvalClient.LastRequest.Message);
    }

    [Fact]
    public async Task Execute_ShouldLogAuditOnSuccess()
    {
        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            DefinitionJson = """{ "tableName": "users" }"""
        };
        _toolServiceMock.Setup(t => t.GetToolByNameAsync("test_tool"))
            .ReturnsAsync(tool);

        _queryValueParserMock.Setup(q => q.UnwrapJsonElement(It.IsAny<JsonElement>()))
            .Returns(null!);

        var strategyMock = new Mock<ISqlStrategy>();
        strategyMock.Setup(s => s.ExecuteQueryAsync(It.IsAny<QueryDefinition>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("\"result\": []");
        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres))
            .Returns(strategyMock.Object);

        var args = JsonSerializer.SerializeToElement(new { });
        var result = await _proxy.Execute(args);

        _auditServiceMock.Verify(a => a.WriteLogAsync(
            "mcp.test_tool.executed",
            "test_tool",
            "success",
            It.Is<string>(s => s.Contains("Query")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ShouldLogAuditOnFailure()
    {
        _toolServiceMock.Setup(t => t.GetToolByNameAsync("test_tool"))
            .ReturnsAsync((CustomSqlTool?)null);

        var args = JsonSerializer.SerializeToElement(new { });
        var result = await _proxy.Execute(args);

        _auditServiceMock.Verify(a => a.WriteLogAsync(
            "mcp.test_tool.executed",
            "test_tool",
            "failed",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
