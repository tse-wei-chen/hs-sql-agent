using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed class SqlAgentToolDdlSecurityTests
{
    private static (SqlAgentTool Tool,
        Mock<ISqlProviderFactory> Factory,
        Mock<ISqlExecutionConcurrencyLimiter> Limiter,
        Mock<IAuditService> Audit)
        Build(string? allowedTools, bool auditAvailable = true)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[McpContextItemKeys.AccessKeyId] = 10;
        ctx.Items[McpContextItemKeys.DbManagementId] = 7;
        ctx.Items[McpContextItemKeys.DatabaseName] = "test";
        ctx.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        ctx.Items[McpContextItemKeys.SqlConnectionString] = "Host=unused;Database=test";
        if (allowedTools is not null)
            ctx.Items[McpContextItemKeys.AllowedTools] = allowedTools;

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(x => x.HttpContext).Returns(ctx);
        var factory = new Mock<ISqlProviderFactory>(MockBehavior.Strict);
        var audit = new Mock<IAuditService>();
        audit.Setup(x => x.WriteEventAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<AuditEventContext>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => auditAvailable
                ? Task.CompletedTask
                : Task.FromException(new InvalidOperationException("AuditLog unavailable.")));
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>(MockBehavior.Strict);
        var runtime = new PostgresDdlRuntime(new PostgresDdlExecutionOptions
        {
            Enabled = true,
            AllowedSchemas = new HashSet<string>(StringComparer.Ordinal) { "app" },
            AllowCreateTable = true
        });
        var tool = new SqlAgentTool(
            accessor.Object,
            factory.Object,
            audit.Object,
            Mock.Of<IDbSemanticService>(),
            Mock.Of<ISecurityPolicyRuntimeState>(),
            limiter.Object,
            postgresDdlRuntime: runtime);
        return (tool, factory, limiter, audit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("execute_query_sql,execute_dml_sql")]
    [InlineData("execute_ddl_sql_extra")]
    public async Task MissingExplicitDdlGrant_DeniedBeforeProviderOrDatabase(string? allowedTools)
    {
        var (tool, factory, limiter, _) = Build(allowedTools);
        var result = await tool.ExecuteDdlSql(
            "CREATE TABLE app.widgets (id INTEGER)",
            previewOnly: false, TestContext.Current.CancellationToken);
        Assert.Equal("rejected", result.Status);
        Assert.False(result.CommitConfirmed);
        Assert.Equal("ddl.permission_denied", result.Error?.Code);
        factory.VerifyNoOtherCalls();
        limiter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HostAuditUnavailable_FailsBeforeRequestingAnyDdlConnection()
    {
        var (tool, factory, limiter, _) = Build("execute_ddl_sql", auditAvailable: false);
        var result = await tool.ExecuteDdlSql(
            "CREATE TABLE app.widgets (id INTEGER)",
            previewOnly: false, TestContext.Current.CancellationToken);
        Assert.Equal("failed", result.Status);
        Assert.False(result.CommitConfirmed);
        Assert.Equal("ddl.host_failed", result.Error?.Code);
        factory.VerifyNoOtherCalls();
        limiter.VerifyNoOtherCalls();
    }

    [Fact]
    public void OutcomeUnknown_TransportHasNoSuccessOrAutomaticRetryAuthority()
    {
        var output = McpDdlToolResult.FromRuntime(new DdlRuntimeResult(
            DdlRuntimeStatus.OutcomeUnknown,
            null,
            "Lost PostgreSQL COMMIT reply; reconcile metadata.",
            "ddl.outcome_unknown"));
        Assert.Equal("outcome_unknown", output.Status);
        Assert.False(output.CommitConfirmed);
        Assert.Equal("ddl.outcome_unknown", output.Error?.Code);
        Assert.False(output.Error?.Retryable);
    }
}
