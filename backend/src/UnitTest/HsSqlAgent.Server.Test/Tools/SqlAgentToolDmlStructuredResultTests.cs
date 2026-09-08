using Admin.Service.Interfaces;
using Common.Models;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public class SqlAgentToolDmlStructuredResultTests
{
    [Fact]
    public void CommittedResult_PreservesReturnedRowsAsStructuredContent()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?>
            {
                ["id"] = 7,
                ["status"] = "updated"
            }
        ];

        var result = McpDmlToolResult.CommittedResult(
            "Postgres",
            1,
            1,
            12,
            "request-1",
            rows,
            "Committed.");

        Assert.Equal("committed", result.Status);
        Assert.True(result.Committed);
        Assert.Equal("approved", result.ApprovalDecision);
        Assert.Equal(1, result.AffectedRows);
        Assert.Null(result.Error);
        var row = Assert.Single(result.ReturnedRows);
        Assert.Equal(7, row["id"]);
        Assert.Equal("updated", row["status"]);
    }

    [Fact]
    public async Task ExecuteDmlSql_InvalidConfiguration_ReturnsStructuredFailureWithoutSecret()
    {
        const string secretConnectionString =
            "Host=prod.example;Database=payments;Username=service;Password=super-secret";
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.AllowedTools] = string.Empty;
        context.Items[McpContextItemKeys.SqlProvider] = "NotAProvider";
        context.Items[McpContextItemKeys.SqlConnectionString] = secretConnectionString;
        httpContextAccessor.Setup(x => x.HttpContext).Returns(context);

        var tool = new SqlAgentTool(
            httpContextAccessor.Object,
            Mock.Of<ISqlProviderFactory>(),
            Mock.Of<IAuditService>(),
            Mock.Of<IDbSemanticService>(),
            Mock.Of<ISecurityPolicyRuntimeState>(),
            Mock.Of<ISqlExecutionConcurrencyLimiter>());

        var result = await tool.ExecuteDmlSql(
            "UPDATE public.users SET name = 'Alice' WHERE id = 7",
            null!,
            TestContext.Current.CancellationToken);

        Assert.Equal("failed", result.Status);
        Assert.False(result.Committed);
        Assert.Equal(0, result.StatementCount);
        Assert.Null(result.Provider);
        Assert.Empty(result.ReturnedRows);
        Assert.NotNull(result.Error);
        Assert.Equal("configuration.invalid", result.Error.Code);
        Assert.Equal("Configuration", result.Error.Stage);
        Assert.Equal("Invalid database provider or connection configuration.", result.Error.Message);
        Assert.DoesNotContain("super-secret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretConnectionString, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretConnectionString, result.Error.Message, StringComparison.Ordinal);
    }
}
