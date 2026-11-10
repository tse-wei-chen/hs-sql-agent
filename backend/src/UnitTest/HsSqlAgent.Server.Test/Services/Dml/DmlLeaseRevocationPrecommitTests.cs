using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore.SqlParsing;
using Moq;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Services.Dml;

/// <summary>
/// Redis admission revocation must abort read-only DML preflight and preview
/// before a physical SQL operation or human approval is created. A started
/// native COMMIT has a different, unknown-outcome contract.
/// </summary>
public sealed class DmlLeaseRevocationPrecommitTests
{
    private const string Insert = "INSERT INTO public.users (id) VALUES (7)";
    private static readonly DmlApprovalExecutionContext ApprovalContext =
        new("mcp-key:7", "db-management:11", SqlAgentToolType.Postgres, "appdb");

    [Fact]
    public async Task ProfileDiscovery_AlreadyRevokedRedisLease_DeniesBeforeOpeningConnection()
    {
        using var revoked = new CancellationTokenSource();
        revoked.Cancel();
        var limiter = RevokedLimiter(revoked.Token);
        var connections = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        provider.SetupGet(x => x.Connections).Returns(connections.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TypedDmlRuntime().ParseDmlBatchWithVerifiedRuntimeProfileUnderLeaseAsync(
                provider.Object, "Host=unused;Database=unused",
                Insert, SqlAgentToolType.Postgres,
                limiter.Object, TestContext.Current.CancellationToken));

        connections.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_AlreadyRevokedRedisLease_CannotGenerateHumanApproval(bool batch)
    {
        using var revoked = new CancellationTokenSource();
        revoked.Cancel();
        var limiter = RevokedLimiter(revoked.Token);
        var provider = new Mock<ISqlProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.Type).Returns(SqlAgentToolType.Postgres);
        var connections = new Mock<IDbConnectionFactory>(MockBehavior.Strict);
        provider.SetupGet(x => x.Connections).Returns(connections.Object);
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel { DmlMaxAffectedRows = 10 });
        var approval = new Mock<IDmlApprovalProvider>(MockBehavior.Strict);
        var parsedBatch = CoreDmlBatchTextParser.ParseDmlBatch(Insert, SqlAgentToolType.Postgres);

        if (batch)
        {
            var flow = new TypedDmlTransactionApprovalFlow(
                new TypedDmlRuntime(), state.Object, limiter.Object, () => null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                flow.ExecuteAsync(
                    provider.Object, "connection", parsedBatch, ApprovalContext,
                    approval.Object, "Approval", TestContext.Current.CancellationToken));
        }
        else
        {
            var flow = new TypedDmlApprovalFlow(
                new TypedDmlRuntime(), state.Object, limiter.Object, () => null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                flow.ExecuteAsync(
                    provider.Object, "connection", parsedBatch.Statements[0], ApprovalContext,
                    approval.Object, "Approval", TestContext.Current.CancellationToken));
        }

        connections.VerifyNoOtherCalls();
        approval.VerifyNoOtherCalls();
    }

    private static Mock<ISqlExecutionConcurrencyLimiter> RevokedLimiter(CancellationToken token)
    {
        var limiter = new Mock<ISqlExecutionConcurrencyLimiter>();
        limiter.Setup(x => x.TryAcquireAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RevocableTestLease(token));
        return limiter;
    }

    private sealed class RevocableTestLease(CancellationToken token) : IRevocableSqlExecutionLease
    {
        public CancellationToken RevocationToken => token;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
