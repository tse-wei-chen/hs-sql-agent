using System.Data.Common;
using System.Text.Json;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using Microsoft.AspNetCore.Http;
using Moq;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Tools;

public sealed class CustomToolTransactionProxyTests
{
    [Fact]
    public async Task Execute_MultiStatementDml_UsesOnePluggableApprovalProviderWithoutMcpElicitation()
    {
        var toolService = new Mock<ICustomSqlToolService>();
        toolService.Setup(x => x.GetPublishedToolByNameAsync(
                "archive_order",
                42,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomSqlTool
            {
                Name = "archive_order",
                Type = "DML",
                SqlTemplate =
                    "INSERT INTO public.audit_log (id, note) VALUES (1, 'details');" +
                    "INSERT INTO public.audit_log (id, note) VALUES (2, 'order')"
            });

        var context = new DefaultHttpContext();
        context.Items[Common.Models.McpContextItemKeys.SqlProvider] = "Postgres";
        context.Items[Common.Models.McpContextItemKeys.SqlConnectionString] = "Host=localhost;Database=testdb";
        context.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 7;
        context.Items[Common.Models.McpContextItemKeys.DbManagementId] = 42;
        context.Items[Common.Models.McpContextItemKeys.DatabaseName] = "testdb";
        context.Items[Common.Models.McpContextItemKeys.AllowedTools] = string.Empty;
        context.Items[Common.Models.McpContextItemKeys.TableWhitelist] = string.Empty;
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.SetupGet(x => x.HttpContext).Returns(context);

        var verificationConnection = new Mock<DbConnection>();
        verificationConnection.SetupGet(x => x.State).Returns(System.Data.ConnectionState.Open);
        verificationConnection.SetupGet(x => x.ServerVersion).Returns("17.5");
        verificationConnection.Setup(x => x.OpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var connections = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        connections.Setup(x => x.Create("Host=localhost;Database=testdb"))
            .Returns(verificationConnection.Object);
        var metadata = new Mock<IProviderMetadataReader>(MockBehavior.Strict);
        var provider = new Mock<ISqlProvider>();
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Metadata).Returns(metadata.Object);
        provider.SetupGet(x => x.Connections).Returns(connections.Object);
        var providerFactory = new Mock<ISqlProviderFactory>();
        providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Postgres)).Returns(provider.Object);

        var policyState = new Mock<ISecurityPolicyRuntimeState>();
        policyState.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            DmlMaxAffectedRows = 10,
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true
        });
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        limiter.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IAsyncDisposable>());

        var approval = new CapturingDecliningApprovalProvider();
        var proxy = new CustomToolProxy(
            "archive_order",
            toolService.Object,
            httpContextAccessor.Object,
            providerFactory.Object,
            Mock.Of<IAuditService>(),
            Mock.Of<IQueryValueParserService>(),
            policyState.Object,
            limiter.Object,
            dmlApprovalProvider: approval);

        var result = await proxy.Execute(
            JsonSerializer.SerializeToElement(new { }),
            server: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("cancelled by user", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, approval.RequestCount);
        Assert.NotNull(approval.LastRequest);
        Assert.True(approval.LastRequest!.IsTransaction);
        Assert.Equal(2, approval.LastRequest.Statements.Count);
        Assert.Equal(2, approval.LastRequest.TotalAffectedRows);
        Assert.Equal("mcp-key:7", approval.LastRequest.RequesterIdentity);
        Assert.Equal("testdb", approval.LastRequest.DatabaseIdentity);
        Assert.All(approval.LastRequest.Statements, statement => Assert.Equal("INSERT", statement.Operation));
        connections.Verify(
            x => x.Create("Host=localhost;Database=testdb"),
            Times.Exactly(3));
        metadata.VerifyNoOtherCalls();
    }

    private sealed class CapturingDecliningApprovalProvider : IDmlApprovalProvider
    {
        public int RequestCount { get; private set; }
        public DmlApprovalRequest? LastRequest { get; private set; }

        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            LastRequest = request;
            return ValueTask.FromResult(DmlApprovalResult.Reject(
                request,
                "DML transaction execution cancelled by user."));
        }
    }
}
