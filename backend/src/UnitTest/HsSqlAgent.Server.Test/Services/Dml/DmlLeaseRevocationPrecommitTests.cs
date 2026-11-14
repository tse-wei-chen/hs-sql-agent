using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore.SqlParsing;
using Microsoft.Data.Sqlite;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedSqliteDml_CommitLeaseAlreadyRevoked_RejectsWithoutPhysicalWrite(bool batch)
    {
        var databaseFile = Path.Combine(Path.GetTempPath(),
            $"hsa-dml-commit-revocation-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databaseFile};Pooling=False";
        try
        {
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = setup.CreateCommand();
                command.CommandText =
                    "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            using var revokedCommit = new CancellationTokenSource();
            revokedCommit.Cancel();
            var limiter = new PreviewThenRevokedCommitLimiter(revokedCommit.Token);
            var state = new Mock<ISecurityPolicyRuntimeState>();
            state.Setup(x => x.GetCurrent())
                .Returns(new SecurityPolicyModel { DmlMaxAffectedRows = 2 });
            var approver = new ApprovingProvider();
            var runtime = new TypedDmlRuntime();
            var parsed = CoreDmlBatchTextParser.ParseDmlBatch(
                "INSERT INTO main.users (id, name) VALUES (7, 'Alice')",
                SqlAgentToolType.Sqlite);
            var provider = new SqliteProvider();
            var approvalContext = new DmlApprovalExecutionContext(
                "mcp-key:7", "db-management:11", SqlAgentToolType.Sqlite, "main");
            var outcome = batch
                ? await new TypedDmlTransactionApprovalFlow(
                    runtime, state.Object, limiter, () => null).ExecuteAsync(
                    provider, connectionString, parsed, approvalContext,
                    approver, "Approve batch", TestContext.Current.CancellationToken)
                : await new TypedDmlApprovalFlow(
                    runtime, state.Object, limiter, () => null).ExecuteAsync(
                    provider, connectionString, parsed.Statements[0], approvalContext,
                    approver, "Approve insert", TestContext.Current.CancellationToken);

            Assert.Equal(1, approver.CallCount);
            Assert.Equal(2, limiter.Acquisitions);
            Assert.False(outcome.Committed);
            Assert.Equal("dml.lease_revoked_before_commit", outcome.ErrorCode);
            Assert.False(outcome.Retryable);
            Assert.Equal(DmlApprovalDecision.Approved, outcome.ApprovalDecision);
            await using var witness = new SqliteConnection(connectionString);
            await witness.OpenAsync(TestContext.Current.CancellationToken);
            await using var verify = witness.CreateCommand();
            verify.CommandText = "SELECT COUNT(*) FROM users";
            Assert.Equal(0L, Convert.ToInt64(
                await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }
        finally
        {
            if (File.Exists(databaseFile))
                File.Delete(databaseFile);
        }
    }

    private sealed class PreviewThenRevokedCommitLimiter(CancellationToken commitRevoked)
        : ISqlExecutionConcurrencyLimiter
    {
        public int Acquisitions { get; private set; }

        public ValueTask<IAsyncDisposable?> TryAcquireAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Acquisitions++;
            return ValueTask.FromResult<IAsyncDisposable?>(
                Acquisitions == 1
                    ? new PlainLease()
                    : new RevocableTestLease(commitRevoked));
        }
    }

    private sealed class PlainLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ApprovingProvider : IDmlApprovalProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<DmlApprovalResult> RequestApprovalAsync(
            DmlApprovalRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(DmlApprovalResult.Approve(request));
        }
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
