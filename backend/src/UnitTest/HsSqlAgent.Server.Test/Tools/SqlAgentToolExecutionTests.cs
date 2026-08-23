using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
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

    private static bool IsTable(ParsedStatement parsed, string expected)
    {
        if (parsed.Statement is not SelectStatement { From: NamedTableSource source }) return false;
        var actual = string.Join('.', source.Name.Parts.Select(part => part.Value));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
