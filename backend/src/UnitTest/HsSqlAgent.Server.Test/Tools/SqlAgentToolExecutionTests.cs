using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;
using SqlAgent.Service.Strategies;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class SqlAgentToolExecutionTests
{
    [Fact]
    public async Task ExecuteQuerySql_UsesTypedRuntime()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var strategyFactory = new Mock<ISqlStrategyFactory>();
        var auditService = new Mock<IAuditService>();
        var semanticService = new Mock<IDbSemanticService>();
        var securityPolicyState = new Mock<ISecurityPolicyRuntimeState>();
        var concurrencyLimiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();
        var strategy = new Mock<ISqlStrategy>();

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
        strategy.SetupGet(x => x.DbType).Returns(SqlAgentToolType.Postgres);
        strategyFactory.Setup(x => x.GetStrategy(SqlAgentToolType.Postgres)).Returns(strategy.Object);

        typedQueryRuntime
            .Setup(x => x.ExecuteAsync(
                strategy.Object,
                "Host=localhost;Database=testdb",
                It.Is<QueryDefinition>(q => q.TableName == "public.users"),
                SqlAgentToolType.Postgres,
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
            strategyFactory.Object,
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
                audit.ToolName == "execute_query_sql" &&
                audit.Operation == "select" &&
                audit.ReturnedRows == 1),
            It.Is<string>(detail => detail.Contains("Postgres", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteQuerySql_TypedRuntimeAuthorizationFailure_RemainsFailClosed()
    {
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var strategyFactory = new Mock<ISqlStrategyFactory>();
        var auditService = new Mock<IAuditService>();
        var semanticService = new Mock<IDbSemanticService>();
        var securityPolicyState = new Mock<ISecurityPolicyRuntimeState>();
        var concurrencyLimiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        var typedQueryRuntime = new Mock<ITypedQueryRuntime>();
        var strategy = new Mock<ISqlStrategy>();

        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[McpContextItemKeys.TableWhitelist] = "public.users";
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);
        securityPolicyState.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel());
        concurrencyLimiter
            .Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());
        strategyFactory.Setup(x => x.GetStrategy(SqlAgentToolType.Postgres)).Returns(strategy.Object);
        typedQueryRuntime
            .Setup(x => x.ExecuteAsync(
                It.IsAny<ISqlStrategy>(),
                It.IsAny<string>(),
                It.IsAny<QueryDefinition>(),
                SqlAgentToolType.Postgres,
                It.IsAny<SecurityPolicyModel>(),
                It.IsAny<IReadOnlySet<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("table denied"));

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            strategyFactory.Object,
            auditService.Object,
            semanticService.Object,
            securityPolicyState.Object,
            concurrencyLimiter.Object,
            typedQueryRuntime.Object);

        var result = await tool.ExecuteQuerySql("SELECT id FROM public.secrets");

        Assert.Contains("table denied", result, StringComparison.OrdinalIgnoreCase);
    }
}
