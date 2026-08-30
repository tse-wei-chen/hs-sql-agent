using System.Data.Common;
using System.Text.Json;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using Moq;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class CustomToolProxyTests
{
    private readonly Mock<ICustomSqlToolService> _toolServiceMock;
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private readonly Mock<ISqlProviderFactory> _providerFactoryMock;
    private readonly Mock<IAuditService> _auditServiceMock;
    private readonly Mock<IQueryValueParserService> _queryValueParserMock;
    private readonly Mock<ISecurityPolicyRuntimeState> _securityPolicyRuntimeStateMock;
    private readonly Mock<ISqlExecutionConcurrencyLimiter> _sqlConcurrencyLimiterMock;
    private readonly Mock<ITypedQueryRuntime> _typedQueryRuntimeMock;
    private readonly CustomToolProxy _proxy;

    public CustomToolProxyTests()
    {
        _toolServiceMock = new Mock<ICustomSqlToolService>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _providerFactoryMock = new Mock<ISqlProviderFactory>();
        _auditServiceMock = new Mock<IAuditService>();
        _queryValueParserMock = new Mock<IQueryValueParserService>();
        _securityPolicyRuntimeStateMock = new Mock<ISecurityPolicyRuntimeState>();
        _sqlConcurrencyLimiterMock = new Mock<ISqlExecutionConcurrencyLimiter>();
        _typedQueryRuntimeMock = new Mock<ITypedQueryRuntime>();
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
        context.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
        context.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
        context.Items[Common.Models.McpContextItemKeys.DatabaseName] = "testdb";
        context.Items[Common.Models.McpContextItemKeys.AllowedTools] = string.Empty;
        context.Items[Common.Models.McpContextItemKeys.TableWhitelist] = string.Empty;
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(context);

        _proxy = new CustomToolProxy(
            "test_tool",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _providerFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object,
            _securityPolicyRuntimeStateMock.Object,
            _sqlConcurrencyLimiterMock.Object,
            _typedQueryRuntimeMock.Object);
    }

    [Fact]
    public async Task Execute_ShouldReturnError_WhenToolNotFound()
    {
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CustomSqlTool?)null);

        var result = await _proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task Execute_MissingToolAuthorizationContext_FailsClosed()
    {
        _httpContextAccessorMock.Object.HttpContext!.Items.Remove(Common.Models.McpContextItemKeys.AllowedTools);

        var result = await _proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("tool authorization context is missing", result, StringComparison.OrdinalIgnoreCase);
        _toolServiceMock.Verify(
            x => x.GetPublishedToolByNameAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_ShouldRecheckAllowedTools_ForExistingSession()
    {
        _httpContextAccessorMock.Object.HttpContext!.Items[Common.Models.McpContextItemKeys.AllowedTools] = "get_tables";

        var result = await _proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("does not have permission", result);
        _toolServiceMock.Verify(
            x => x.GetPublishedToolByNameAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Execute_QueryTool_UsesParserNativeTypedRuntime()
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

        SetupPostgresProvider();
        _typedQueryRuntimeMock.Setup(r => r.ExecuteAsync(
                It.Is<ISqlProvider>(provider => provider.Type == SqlAgentToolType.Postgres),
                "Host=localhost;Database=testdb",
                It.Is<string>(sql => sql.Contains("users", StringComparison.OrdinalIgnoreCase)),
                SqlAgentToolType.Postgres,
                It.Is<SecurityPolicyModel>(p => p.QueryMaxRows == 1000 && p.QueryTimeoutSeconds == 30),
                It.IsAny<IReadOnlySet<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult(
                [new Dictionary<string, object?> { ["email"] = "test@example.com" }],
                1,
                TimeSpan.Zero,
                []));

        var result = await _proxy.Execute(
            JsonSerializer.SerializeToElement(new { email = "test@example.com" }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("test@example.com", result);
        _typedQueryRuntimeMock.VerifyAll();
    }

    [Fact]
    public async Task Execute_QueryTool_MissingTableAuthorizationContext_FailsClosed()
    {
        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            SqlTemplate = "SELECT * FROM users"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);
        SetupPostgresProvider();
        _httpContextAccessorMock.Object.HttpContext!.Items.Remove(Common.Models.McpContextItemKeys.TableWhitelist);

        var result = await _proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("table authorization context is missing", result, StringComparison.OrdinalIgnoreCase);
        _typedQueryRuntimeMock.Verify(x => x.ExecuteAsync(
            It.IsAny<ISqlProvider>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<SqlAgentToolType>(),
            It.IsAny<SecurityPolicyModel>(),
            It.IsAny<IReadOnlySet<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_ShouldReturnError_WhenSqlConfigMissing()
    {
        var emptyContext = new DefaultHttpContext();
        emptyContext.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
        emptyContext.Items[Common.Models.McpContextItemKeys.AllowedTools] = string.Empty;
        emptyContext.Items[Common.Models.McpContextItemKeys.TableWhitelist] = string.Empty;
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(emptyContext);

        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            SqlTemplate = "SELECT * FROM users"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);

        var result = await _proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("missing", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ShouldRequireElicitationForDmlTool()
    {
        var tool = new CustomSqlTool
        {
            Name = "delete_user",
            Type = "DML",
            SqlTemplate = "DELETE FROM users"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("delete_user", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);
        SetupPostgresProvider();

        var dmlProxy = new CustomToolProxy(
            "delete_user",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _providerFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object,
            _securityPolicyRuntimeStateMock.Object,
            _sqlConcurrencyLimiterMock.Object);

        var result = await dmlProxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            server: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("does not support", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_InsertValuesCustomTool_ReachesInteractiveApprovalPreview()
    {
        var tool = new CustomSqlTool
        {
            Name = "insert_user",
            Type = "DML",
            SqlTemplate = "INSERT INTO public.users (name) VALUES ('Alice')"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("insert_user", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);

        var metadata = new Mock<IProviderMetadataReader>();
        metadata.Setup(x => x.GetColumnsAsync(
                "Host=localhost;Database=testdb",
                "public",
                "users",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DatabaseColumnMetadata("public", "users", "name", "text", false)
            ]);
        var verificationConnection = new Mock<DbConnection>();
        verificationConnection.SetupGet(x => x.State).Returns(System.Data.ConnectionState.Open);
        verificationConnection.SetupGet(x => x.ServerVersion).Returns("17.5");
        verificationConnection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var connections = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        connections.Setup(x => x.Create("Host=localhost;Database=testdb"))
            .Returns(verificationConnection.Object);
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Metadata).Returns(metadata.Object);
        provider.SetupGet(x => x.Connections).Returns(connections.Object);
        _providerFactoryMock.Setup(f => f.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);

        var approval = new DecliningApprovalClient();

        var dmlProxy = new CustomToolProxy(
            "insert_user",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _providerFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object,
            _securityPolicyRuntimeStateMock.Object,
            _sqlConcurrencyLimiterMock.Object);

        var result = await dmlProxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            approval,
            TestContext.Current.CancellationToken);

        Assert.Contains("cancelled by user", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, approval.ElicitCount);
        connections.Verify(x => x.Create("Host=localhost;Database=testdb"), Times.Once);
        metadata.VerifyAll();
    }

    [Fact]
    public async Task Execute_InsertSelectCustomTool_RemainsFailClosed()
    {
        var tool = new CustomSqlTool
        {
            Name = "insert_user",
            Type = "DML",
            SqlTemplate = "INSERT INTO public.users (name) SELECT name FROM public.pending_users"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("insert_user", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);
        SetupPostgresProvider();

        var dmlProxy = new CustomToolProxy(
            "insert_user",
            _toolServiceMock.Object,
            _httpContextAccessorMock.Object,
            _providerFactoryMock.Object,
            _auditServiceMock.Object,
            _queryValueParserMock.Object,
            _securityPolicyRuntimeStateMock.Object,
            _sqlConcurrencyLimiterMock.Object);

        var result = await dmlProxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            approvalClient: null,
            TestContext.Current.CancellationToken);

        Assert.Contains("INSERT ... SELECT", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail-closed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ShouldLogTypedQueryRowCountOnSuccess()
    {
        var tool = new CustomSqlTool
        {
            Name = "test_tool",
            Type = "Query",
            SqlTemplate = "SELECT * FROM users"
        };
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tool);

        SetupPostgresProvider();
        _typedQueryRuntimeMock.Setup(r => r.ExecuteAsync(
                It.Is<ISqlProvider>(provider => provider.Type == SqlAgentToolType.Postgres),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<SqlAgentToolType>(),
                It.IsAny<SecurityPolicyModel>(),
                It.IsAny<IReadOnlySet<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult([], 0, TimeSpan.Zero, []));

        _ = await _proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        _auditServiceMock.Verify(a => a.WriteEventAsync(
            "mcp.test_tool.executed",
            "test_tool",
            "success",
            It.Is<AuditEventContext>(c =>
                c.ToolName == "test_tool"
                && c.Operation == "select"
                && c.ReturnedRows == 0
                && c.Definition != null
                && !c.Definition.Contains("test@example.com")),
            It.Is<string>(s => s.Contains("Query")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ShouldLogAuditOnFailure()
    {
        _toolServiceMock.Setup(t => t.GetPublishedToolByNameAsync("test_tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((CustomSqlTool?)null);

        _ = await _proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            cancellationToken: TestContext.Current.CancellationToken);

        _auditServiceMock.Verify(a => a.WriteLogAsync(
            "mcp.test_tool.executed",
            "test_tool",
            "failed",
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private void SetupPostgresProvider()
    {
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(p => p.Type).Returns(SqlAgentToolType.Postgres);
        _providerFactoryMock
            .Setup(f => f.GetProvider(SqlAgentToolType.Postgres))
            .Returns(provider.Object);
    }


    private sealed class DecliningApprovalClient : IDmlApprovalClient
    {
        public bool SupportsElicitation => true;
        public int ElicitCount { get; private set; }

        public ValueTask<ElicitResult> ElicitAsync(
            ElicitRequestParams request,
            CancellationToken cancellationToken)
        {
            ElicitCount++;
            return ValueTask.FromResult(new ElicitResult { Action = "decline" });
        }
    }
}
