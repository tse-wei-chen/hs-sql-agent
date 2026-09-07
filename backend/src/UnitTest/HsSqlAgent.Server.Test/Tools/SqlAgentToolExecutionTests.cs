using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.SqlCore;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class SqlAgentToolExecutionTests
{
    [Fact]
    public async Task ExecuteQuerySql_UsesParserNativeTypedRuntime()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var providerFactory = new Mock<ISqlProviderFactory>();
        var auditService = new Mock<IAuditService>();
        var semanticService = new Mock<IDbSemanticService>();
        var securityPolicyState = new Mock<ISecurityPolicyRuntimeState>();
        var concurrencyLimiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();
        var provider = new Mock<ISqlProvider>();

        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[McpContextItemKeys.AllowedTools] = string.Empty;
        context.Items[McpContextItemKeys.TableWhitelist] = "public.users";
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);

        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;
        var policy = new SecurityPolicyModel
        {
            QueryMaxRows = 25,
            QueryTimeoutSeconds = 17
        };
        securityPolicyState.Setup(x => x.GetCurrent()).Returns(policy);
        concurrencyLimiter
            .Setup(x => x.TryAcquireAsync(cancellationToken))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);

        typedQueryRuntime
            .As<ITypedQueryRuntimeFacts>()
            .Setup(x => x.ExecuteWithFactsAsync(
                It.Is<ISqlProvider>(candidate => candidate.Type == SqlAgentToolType.Postgres),
                "Host=localhost;Database=testdb",
                It.Is<string>(candidate => candidate.Contains("public.users", StringComparison.OrdinalIgnoreCase)),
                SqlAgentToolType.Postgres,
                policy,
                It.Is<IReadOnlySet<string>?>(tables => tables != null && tables.Contains("public.users")),
                cancellationToken))
            .ReturnsAsync(new QueryExecutionWithFacts(
                new QueryExecutionResult(
                    [new Dictionary<string, object?> { ["id"] = 7 }],
                    1,
                    TimeSpan.FromMilliseconds(12),
                    []),
                SqlCoreInspection.GetQueryFacts("SELECT id FROM public.users", SqlAgentToolType.Postgres)));

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            providerFactory.Object,
            auditService.Object,
            semanticService.Object,
            securityPolicyState.Object,
            concurrencyLimiter.Object,
            typedQueryRuntime.Object);

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.users", cancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Postgres", result.Provider);
        Assert.Equal(1, result.RowCount);
        Assert.Equal(12, result.DurationMs);
        Assert.Null(result.Error);
        var row = Assert.Single(result.Rows);
        Assert.Equal(7, row["id"]);
        typedQueryRuntime.VerifyAll();
        auditService.Verify(x => x.WriteEventAsync(
            "mcp.query.executed",
            "public.users",
            "success",
            It.Is<AuditEventContext>(audit =>
                audit.ToolName == "execute_query_sql"
                && audit.Operation == "select"
                && audit.ReturnedRows == 1),
            It.Is<string>(detail => detail.Contains("Postgres", StringComparison.Ordinal)),
            cancellationToken), Times.Once);
    }

    [Fact]
    public async Task ExecuteQuerySql_TypedRuntimeAuthorizationFailure_RemainsFailClosed()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var providerFactory = new Mock<ISqlProviderFactory>();
        var auditService = new Mock<IAuditService>();
        var semanticService = new Mock<IDbSemanticService>();
        var securityPolicyState = new Mock<ISecurityPolicyRuntimeState>();
        var concurrencyLimiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();
        var provider = new Mock<ISqlProvider>();

        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[McpContextItemKeys.AllowedTools] = string.Empty;
        context.Items[McpContextItemKeys.TableWhitelist] = "public.users";
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);
        securityPolicyState.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel());
        concurrencyLimiter
            .Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);
        typedQueryRuntime
            .As<ITypedQueryRuntimeFacts>()
            .Setup(x => x.ExecuteWithFactsAsync(
                It.Is<ISqlProvider>(candidate => candidate.Type == SqlAgentToolType.Postgres),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<SqlAgentToolType>(),
                It.IsAny<SecurityPolicyModel>(),
                It.IsAny<IReadOnlySet<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("table denied"));

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            providerFactory.Object,
            auditService.Object,
            semanticService.Object,
            securityPolicyState.Object,
            concurrencyLimiter.Object,
            typedQueryRuntime.Object);

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.secrets", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("authorization.denied", result.Error.Code);
        Assert.Equal("Authorization", result.Error.Stage);
        Assert.Contains("table denied", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        auditService.Verify(x => x.WriteEventAsync(
            "mcp.query.executed",
            "query",
            "failed",
            It.IsAny<AuditEventContext>(),
            "table denied",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteQuerySql_MissingToolAuthorizationContext_FailsClosed()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var providerFactory = new Mock<ISqlProviderFactory>();
        var auditService = new Mock<IAuditService>();
        var semanticService = new Mock<IDbSemanticService>();
        var securityPolicyState = new Mock<ISecurityPolicyRuntimeState>();
        var concurrencyLimiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();

        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[McpContextItemKeys.TableWhitelist] = string.Empty;
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            providerFactory.Object,
            auditService.Object,
            semanticService.Object,
            securityPolicyState.Object,
            concurrencyLimiter.Object,
            typedQueryRuntime.Object);

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.users", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("authorization.denied", result.Error.Code);
        Assert.Contains("tool authorization context is missing", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        typedQueryRuntime.Verify(x => x.ExecuteAsync(
            It.IsAny<ISqlProvider>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<SqlAgentToolType>(),
            It.IsAny<SecurityPolicyModel>(),
            It.IsAny<IReadOnlySet<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteQuerySql_MissingTableAuthorizationContext_FailsClosed()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var providerFactory = new Mock<ISqlProviderFactory>();
        var auditService = new Mock<IAuditService>();
        var semanticService = new Mock<IDbSemanticService>();
        var securityPolicyState = new Mock<ISecurityPolicyRuntimeState>();
        var concurrencyLimiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();
        var provider = new Mock<ISqlProvider>();

        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[McpContextItemKeys.AllowedTools] = string.Empty;
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);
        securityPolicyState.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel());
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            providerFactory.Object,
            auditService.Object,
            semanticService.Object,
            securityPolicyState.Object,
            concurrencyLimiter.Object,
            typedQueryRuntime.Object);

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.users", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("authorization.denied", result.Error.Code);
        Assert.Contains("authorization context is missing", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        typedQueryRuntime.Verify(x => x.ExecuteAsync(
            It.IsAny<ISqlProvider>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<SqlAgentToolType>(),
            It.IsAny<SecurityPolicyModel>(),
            It.IsAny<IReadOnlySet<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteQuerySql_ExplicitEmptyTableWhitelist_RemainsUnrestricted()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var providerFactory = new Mock<ISqlProviderFactory>();
        var auditService = new Mock<IAuditService>();
        var semanticService = new Mock<IDbSemanticService>();
        var securityPolicyState = new Mock<ISecurityPolicyRuntimeState>();
        var concurrencyLimiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();
        var provider = new Mock<ISqlProvider>();

        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[McpContextItemKeys.AllowedTools] = string.Empty;
        context.Items[McpContextItemKeys.TableWhitelist] = string.Empty;
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);

        var policy = new SecurityPolicyModel();
        securityPolicyState.Setup(x => x.GetCurrent()).Returns(policy);
        concurrencyLimiter
            .Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);
        typedQueryRuntime
            .As<ITypedQueryRuntimeFacts>()
            .Setup(x => x.ExecuteWithFactsAsync(
                provider.Object,
                "Host=localhost;Database=testdb",
                It.IsAny<string>(),
                SqlAgentToolType.Postgres,
                policy,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionWithFacts(
                new QueryExecutionResult([], 0, TimeSpan.Zero, []),
                SqlCoreInspection.GetQueryFacts("SELECT id FROM public.users", SqlAgentToolType.Postgres)));

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            providerFactory.Object,
            auditService.Object,
            semanticService.Object,
            securityPolicyState.Object,
            concurrencyLimiter.Object,
            typedQueryRuntime.Object);

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.users", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, result.RowCount);
        Assert.Empty(result.Rows);
        Assert.Null(result.Error);
        typedQueryRuntime.VerifyAll();
    }

    [Fact]
    public async Task ExecuteQuerySql_InvalidConfiguration_DoesNotExposeConnectionString()
    {
        const string secretConnectionString =
            "Host=prod.example;Database=payments;Username=service;Password=super-secret";
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "NotAProvider";
        context.Items[McpContextItemKeys.SqlConnectionString] = secretConnectionString;
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            Mock.Of<ISqlProviderFactory>(),
            Mock.Of<IAuditService>(),
            Mock.Of<IDbSemanticService>(),
            Mock.Of<ISecurityPolicyRuntimeState>(),
            Mock.Of<ISqlExecutionConcurrencyLimiter>(),
            Mock.Of<ITypedQueryRuntime>());

        var result = await tool.ExecuteQuerySql("SELECT 1", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Null(result.Provider);
        Assert.NotNull(result.Error);
        Assert.Equal("configuration.invalid", result.Error.Code);
        Assert.Equal("Invalid database provider or connection configuration.", result.Error.Message);
        Assert.DoesNotContain("super-secret", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretConnectionString, result.Error.Message, StringComparison.Ordinal);
    }
}
