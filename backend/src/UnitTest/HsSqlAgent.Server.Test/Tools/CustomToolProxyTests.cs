using System.Text.Json;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.AspNetCore.Http;
using Moq;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class CustomToolProxyTests
{
    private readonly Mock<ICustomSqlToolService> _toolServiceMock;
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private readonly Mock<ISqlStrategyFactory> _strategyFactoryMock;
    private readonly Mock<IAuditService> _auditServiceMock;
    private readonly Mock<IQueryValueParserService> _queryValueParserMock;
    private readonly Mock<ISecurityPolicyRuntimeState> _securityPolicyRuntimeStateMock;
    private readonly Mock<ISqlExecutionConcurrencyLimiter> _sqlConcurrencyLimiterMock;
    private readonly CustomToolProxy _proxy;

    public CustomToolProxyTests()
    {
        _toolServiceMock = new Mock<ICustomSqlToolService>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _strategyFactoryMock = new Mock<ISqlStrategyFactory>();
        _auditServiceMock = new Mock<IAuditService>();
        _queryValueParserMock = new Mock<IQueryValueParserService>();
        _securityPolicyRuntimeStateMock = new Mock<ISecurityPolicyRuntimeState>();
        _sqlConcurrencyLimiterMock = new Mock<ISqlExecutionConcurrencyLimiter>();
        _sqlConcurrencyLimiterMock
            .Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        _securityPolicyRuntimeStateMock.Setup(s => s.GetCurrent()).Returns(new SecurityPolicyModel
        {
            QueryMaxRows = 1000,
            QueryTimeoutSeconds = 30,
            RequireWhereForUpdate = false,
            RequireWhereForDelete = false,
            AllowFullTableUpdate = true,
            AllowFullTableDelete = true,
            DmlMaxAffectedRows = 100
        });

        var context = new DefaultHttpContext();
        context.Items[Common.Models.McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(context);

        _proxy = new CustomToolProxy("test_tool",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _strategyFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object,
            _securityPolicyRuntimeStateMock.Object,
            _sqlConcurrencyLimiterMock.Object);
    }

    [Fact]
    public async Task Execute_ShouldReturnError_WhenToolNotFound()
    {
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CustomSqlTool?)null);

        var args = JsonSerializer.SerializeToElement(new { });
        var result = await _proxy.Execute(args, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task Execute_ShouldRecheckAllowedTools_ForExistingSession()
    {
        _httpContextAccessorMock.Object.HttpContext!.Items[Common.Models.McpContextItemKeys.AllowedTools] = "get_tables";

        var result = await _proxy.Execute(JsonSerializer.SerializeToElement(new { }), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("does not have permission", result);
        _toolServiceMock.Verify(
            x => x.GetPublishedToolByNameAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_ShouldReplaceParameters_ForQueryTool()
    {
        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            SqlTemplate = "SELECT email FROM users WHERE email = {{email}}",
            ParametersJson = """[{"name":"email","type":"string"}]"""
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);

        _queryValueParserMock.Setup(q => q.UnwrapJsonElement(It.IsAny<JsonElement>()))
            .Returns("email");

        var strategyMock = new Mock<ISqlStrategy>();
        strategyMock.Setup(s => s.ExecuteQueryAsync(
                It.IsAny<QueryDefinition>(),
                It.IsAny<string>(),
                It.IsAny<SqlExecutionPolicy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("\"result\": [{ \"email\": \"test@example.com\" }]");
        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres))
            .Returns(strategyMock.Object);

        var args = JsonSerializer.SerializeToElement(new { email = "test@example.com" });
        var result = await _proxy.Execute(args, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("result", result);
        strategyMock.Verify(s => s.ExecuteQueryAsync(
            It.Is<QueryDefinition>(q => q.TableName == "users"),
            It.IsAny<string>(),
            It.Is<SqlExecutionPolicy>(p => p.QueryMaxRows == 1000 && p.QueryTimeoutSeconds == 30),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ShouldReturnError_WhenSqlConfigMissing()
    {
        var emptyContext = new DefaultHttpContext();
        emptyContext.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(emptyContext);

        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            SqlTemplate = "SELECT * FROM users"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);

        var args = JsonSerializer.SerializeToElement(new { });
        var result = await _proxy.Execute(args, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ShouldRequireElicitationForDmlTool_WithoutLegacyExecution()
    {
        var tool = new CustomSqlTool
        {
            Name = "delete_user",
            Type = "DML",
            SqlTemplate = "DELETE FROM users"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("delete_user", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);

        var strategyMock = new Mock<ISqlStrategy>();
        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres))
            .Returns(strategyMock.Object);

        var dmlProxy = new CustomToolProxy("delete_user",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _strategyFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object,
            _securityPolicyRuntimeStateMock.Object,
            _sqlConcurrencyLimiterMock.Object);

        var result = await dmlProxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            server: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("does not support", result, StringComparison.OrdinalIgnoreCase);
        strategyMock.Verify(s => s.ExecuteDmlAsync(
            It.IsAny<string>(),
            It.IsAny<DmlDefinition>(),
            It.IsAny<SqlExecutionPolicy>(),
            It.IsAny<CancellationToken>()), Times.Never);
        strategyMock.Verify(s => s.ExecuteDmlAsync(
            It.IsAny<string>(),
            It.IsAny<DmlDefinition>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_InsertCustomTool_RemainsFailClosedWithoutLegacyFallback()
    {
        var tool = new CustomSqlTool
        {
            Name = "insert_user",
            Type = "DML",
            SqlTemplate = "INSERT INTO users (name) VALUES ('Alice')"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("insert_user", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);

        var strategyMock = new Mock<ISqlStrategy>();
        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres))
            .Returns(strategyMock.Object);

        var dmlProxy = new CustomToolProxy("insert_user",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _strategyFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object,
            _securityPolicyRuntimeStateMock.Object,
            _sqlConcurrencyLimiterMock.Object);

        var result = await dmlProxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            approvalClient: null,
            TestContext.Current.CancellationToken);

        Assert.Contains("INSERT", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", result, StringComparison.OrdinalIgnoreCase);
        strategyMock.Verify(s => s.ExecuteDmlAsync(
            It.IsAny<string>(),
            It.IsAny<DmlDefinition>(),
            It.IsAny<SqlExecutionPolicy>(),
            It.IsAny<CancellationToken>()), Times.Never);
        strategyMock.Verify(s => s.ExecuteDmlAsync(
            It.IsAny<string>(),
            It.IsAny<DmlDefinition>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_ShouldLogAuditOnSuccess()
    {
        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            SqlTemplate = "SELECT * FROM users"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);

        _queryValueParserMock.Setup(q => q.UnwrapJsonElement(It.IsAny<JsonElement>()))
            .Returns(null!);

        var strategyMock = new Mock<ISqlStrategy>();
        strategyMock.Setup(s => s.ExecuteQueryAsync(
                It.IsAny<QueryDefinition>(),
                It.IsAny<string>(),
                It.IsAny<SqlExecutionPolicy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("[]");
        _strategyFactoryMock.Setup(f => f.GetStrategy(SqlAgentToolType.Postgres))
            .Returns(strategyMock.Object);

        var args = JsonSerializer.SerializeToElement(new { });
        _ = await _proxy.Execute(args, cancellationToken: TestContext.Current.CancellationToken);

        _auditServiceMock.Verify(a => a.WriteEventAsync(
            "mcp.test_tool.executed",
            "test_tool",
            "success",
            It.Is<AuditEventContext>(c =>
                c.ToolName == "test_tool" &&
                c.Operation == "select" &&
                c.ReturnedRows == 0 &&
                c.Definition != null &&
                !c.Definition.Contains("test@example.com")),
            It.Is<string>(s => s.Contains("Query")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ShouldLogAuditOnFailure()
    {
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CustomSqlTool?)null);

        var args = JsonSerializer.SerializeToElement(new { });
        _ = await _proxy.Execute(args, cancellationToken: TestContext.Current.CancellationToken);

        _auditServiceMock.Verify(a => a.WriteLogAsync(
            "mcp.test_tool.executed",
            "test_tool",
            "failed",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
