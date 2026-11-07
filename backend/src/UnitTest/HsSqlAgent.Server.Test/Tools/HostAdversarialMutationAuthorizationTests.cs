using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

/// <summary>
/// Host-side permission and injection boundary, not just SqlCore parsing.
/// Unauthorized MCP inputs must never obtain a provider, acquire a DB
/// concurrency lease, or reach the approval/runtime entry points.
/// </summary>
public sealed class HostAdversarialMutationAuthorizationTests
{
    private static readonly string?[] MissingOrForgedDmlGrants =
    [
        null,
        "",
        " ",
        "\t",
        "execute_query_sql",
        "execute_query_sql,execute_ddl_sql",
        "execute_dml_sql_extra",
        "extra_execute_dml_sql",
        "execute_dml_sql-v2",
        "execute_dml_sql;execute_query_sql",
        "execute_query_sql execute_dml_sql",
        "execute_query_sql\nexecute_dml_sql",
        "execute_query_sql, execute_dml_sql_extra",
        "execute_ddl_sql,execute_dml_sql_extra"
    ];

    private static readonly string[] AttackerSql =
    [
        "DELETE FROM users WHERE id = 7",
        "UPDATE users SET name = 'owned' WHERE id = 7",
        "INSERT INTO users (id, name) VALUES (7, 'owned')",
        "UPDATE users SET name = 'owned' WHERE id = 7 OR 1=1",
        "DELETE FROM users WHERE id = 7; DROP TABLE users",
        "INSERT INTO users (id) VALUES (7); GRANT ALL ON users TO PUBLIC",
        "UPDATE users SET name = 'x; DROP TABLE users; --' WHERE id = 7",
        "DELETE FROM users WHERE id = 7 /* forged approval */"
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaturatedDmlPreflight_CannotCreateUnbudgetedProviderConnection(bool auditUnavailable)
    {
        var fixture = CreateTool("execute_dml_sql", auditUnavailable: auditUnavailable);
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        var physicalConnections = new Mock<HsSqlAgent.Provider.Abstractions.IDbConnectionFactory>(MockBehavior.Strict);
        provider.SetupGet(x => x.Connections).Returns(physicalConnections.Object);
        fixture.Providers.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);
        fixture.Limiter.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IAsyncDisposable?)null);

        var result = await fixture.Tool.ExecuteDmlSql(
            "INSERT INTO public.users (id) VALUES (7)", null!,
            TestContext.Current.CancellationToken);

        Assert.False(result.Committed);
        Assert.Equal("failed", result.Status);
        Assert.Equal("server.busy", result.Error?.Code);
        Assert.True(result.Error?.Retryable);
        physicalConnections.VerifyNoOtherCalls();
        fixture.Limiter.Verify(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForgedOrMissingDmlGrant_NeverReachesProviderOrApproval()
    {
        var count = 0;
        foreach (var grant in MissingOrForgedDmlGrants)
        foreach (var sql in AttackerSql)
        {
            var fixture = CreateTool(grant);
            var result = await fixture.Tool.ExecuteDmlSql(
                sql, null!, TestContext.Current.CancellationToken);

            Assert.False(result.Committed, grant + " " + sql);
            Assert.Equal("failed", result.Status);
            Assert.Equal("authorization.denied", result.Error?.Code);
            Assert.Equal("Authorization", result.Error?.Stage);
            Assert.Equal(0, result.StatementCount);
            fixture.Providers.VerifyNoOtherCalls();
            fixture.Limiter.VerifyNoOtherCalls();
            fixture.SecurityPolicy.VerifyNoOtherCalls();
            count++;
        }

        Assert.Equal(112, count);
    }

    [Theory]
    [InlineData("execute_dml_sql")]
    [InlineData("EXECUTE_DML_SQL")]
    [InlineData(" execute_query_sql, execute_dml_sql ")]
    [InlineData("execute_dml_sql,execute_ddl_sql")]
    public async Task ExplicitDmlGrant_ContinuesToRequiredIdentityGate(string grant)
    {
        // Positive authorization control: denying all DML would also be a regression.
        var fixture = CreateTool(grant, validIdentity: false);
        var result = await fixture.Tool.ExecuteDmlSql(
            "UPDATE users SET name = 'x' WHERE id = 7",
            null!, TestContext.Current.CancellationToken);

        Assert.Equal("authorization.denied", result.Error?.Code);
        Assert.Contains("stable MCP access-key identity",
            result.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        fixture.Providers.VerifyNoOtherCalls();
        fixture.Limiter.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnrelatedQueryGrant_DoesNotReachQueryRuntime()
    {
        var grants = new[]
        {
            "execute_dml_sql",
            "execute_ddl_sql",
            "execute_query_sql_extra",
            "extra_execute_query_sql",
            "execute_query_sql;execute_dml_sql",
            "execute_dml_sql\nexecute_query_sql"
        };
        var sqls = new[]
        {
            "SELECT id FROM users",
            "SELECT id FROM users; DROP TABLE users",
            "WITH hidden AS (SELECT id FROM secrets) SELECT id FROM hidden",
            "SELECT id FROM users WHERE name = 'x'' OR 1=1 --'"
        };
        var count = 0;
        foreach (var grant in grants)
        foreach (var sql in sqls)
        {
            var fixture = CreateTool(grant);
            var result = await fixture.Tool.ExecuteQuerySql(
                sql, TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal("authorization.denied", result.Error?.Code);
            fixture.Providers.VerifyNoOtherCalls();
            fixture.Limiter.VerifyNoOtherCalls();
            fixture.QueryRuntime.VerifyNoOtherCalls();
            count++;
        }
        Assert.Equal(24, count);
    }

    private static Fixture CreateTool(string? allowedTools, bool validIdentity = true, bool auditUnavailable = false)
    {
        var context = new DefaultHttpContext();
        context.Items[McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[McpContextItemKeys.SqlConnectionString] = "Host=not-used;Database=test";
        context.Items[McpContextItemKeys.TableWhitelist] = "public.users";
        if (allowedTools is not null)
            context.Items[McpContextItemKeys.AllowedTools] = allowedTools;
        if (validIdentity)
        {
            context.Items[McpContextItemKeys.AccessKeyId] = 7;
            context.Items[McpContextItemKeys.DbManagementId] = 11;
            context.Items[McpContextItemKeys.DatabaseName] = "test";
        }

        var http = new Mock<IHttpContextAccessor>();
        http.SetupGet(x => x.HttpContext).Returns(context);
        var providers = new Mock<ISqlProviderFactory>(MockBehavior.Strict);
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>(MockBehavior.Strict);
        var policy = new Mock<ISecurityPolicyRuntimeState>(MockBehavior.Strict);
        var queryRuntime = new Mock<ITypedQueryRuntime>(MockBehavior.Strict);
        var audit = new Mock<IAuditService>();
        audit.Setup(x => x.WriteEventAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<AuditEventContext>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => auditUnavailable
                ? Task.FromException(new IOException("Audit failure: Password=super-secret"))
                : Task.CompletedTask);

        var tool = new SqlAgentTool(
            http.Object, providers.Object, audit.Object,
            Mock.Of<IDbSemanticService>(), policy.Object, limiter.Object,
            queryRuntime.Object);

        return new Fixture(tool, providers, limiter, policy, queryRuntime);
    }

    private sealed record Fixture(
        SqlAgentTool Tool,
        Mock<ISqlProviderFactory> Providers,
        Mock<ISqlExecutionConcurrencyLimiter> Limiter,
        Mock<ISecurityPolicyRuntimeState> SecurityPolicy,
        Mock<ITypedQueryRuntime> QueryRuntime);
}
