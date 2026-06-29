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
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class CustomToolProxyTests
{
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
    public async Task Execute_ShouldHandleDmlTool()
    {
        var tool = new CustomSqlTool
        {
            Name = "delete_user",
            Type = "DML",
            DefinitionJson = """{ "operation": "delete", "tableName": "users" }"""
        };
        _toolServiceMock.Setup(t => t.GetToolByNameAsync("delete_user"))
            .ReturnsAsync(tool);

        var strategyMock = new Mock<ISqlStrategy>();
        strategyMock.Setup(s => s.ExecuteDmlAsync(It.IsAny<string>(), It.IsAny<DmlDefinition>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("\"affectedRows\": 1");
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
        var result = await dmlProxy.Execute(args);

        Assert.Contains("affectedRows", result);
        strategyMock.Verify(s => s.ExecuteDmlAsync(
            It.IsAny<string>(),
            It.Is<DmlDefinition>(d => d.TableName == "users"), It.IsAny<CancellationToken>()), Times.Once);
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
