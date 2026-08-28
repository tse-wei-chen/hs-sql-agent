using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Services;
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
        context.Items[McpContextItemKeys.TableWhitelist] = "public.users";
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);

        var policy = new SecurityPolicyModel
        {
            QueryMaxRows = 25,
            QueryTimeoutSeconds = 17
        };
        securityPolicyState.Setup(x => x.GetCurrent()).Returns(policy);
        concurrencyLimiter
            .Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);

        typedQueryRuntime
            .Setup(x => x.ExecuteAsync(
                It.Is<ISqlProvider>(candidate => candidate.Type == SqlAgentToolType.Postgres),
                "Host=localhost;Database=testdb",
                It.Is<ParsedStatement>(p => IsTable(p, "public.users")),
                policy,
                It.Is<IReadOnlySet<string>?>(tables => tables != null && tables.Contains("public.users")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult(
                [new Dictionary<string, object?> { ["id"] = 7 }],
                1,
                TimeSpan.Zero,
                []));

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            providerFactory.Object,
            auditService.Object,
            semanticService.Object,
            securityPolicyState.Object,
            concurrencyLimiter.Object,
            typedQueryRuntime.Object);

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.users");

        Assert.Contains("\"id\":7", result, StringComparison.Ordinal);
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
            It.IsAny<CancellationToken>()), Times.Once);
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
        context.Items[McpContextItemKeys.TableWhitelist] = "public.users";
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);
        securityPolicyState.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel());
        concurrencyLimiter
            .Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);
        typedQueryRuntime
            .Setup(x => x.ExecuteAsync(
                It.Is<ISqlProvider>(candidate => candidate.Type == SqlAgentToolType.Postgres),
                It.IsAny<string>(),
                It.IsAny<ParsedStatement>(),
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

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.secrets");

        Assert.Contains("table denied", result, StringComparison.OrdinalIgnoreCase);
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

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.users");

        Assert.Contains("authorization context is missing", result, StringComparison.OrdinalIgnoreCase);
        typedQueryRuntime.Verify(x => x.ExecuteAsync(
            It.IsAny<ISqlProvider>(),
            It.IsAny<string>(),
            It.IsAny<ParsedStatement>(),
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
            .Setup(x => x.ExecuteAsync(
                provider.Object,
                "Host=localhost;Database=testdb",
                It.IsAny<ParsedStatement>(),
                policy,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryExecutionResult([], 0, TimeSpan.Zero, []));

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            providerFactory.Object,
            auditService.Object,
            semanticService.Object,
            securityPolicyState.Object,
            concurrencyLimiter.Object,
            typedQueryRuntime.Object);

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.users");

        Assert.Equal("[]", result);
        typedQueryRuntime.VerifyAll();
    }

    private static bool IsTable(ParsedStatement parsed, string expected)
    {
        if (parsed.Statement is not SelectStatement { From: NamedTableSource source }) return false;
        var actual = string.Join('.', source.Name.Parts.Select(part => part.Value));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
