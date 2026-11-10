using HsSqlAgent.Server.Services;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class SqlExecutionLeaseCancellationTests
{
    [Fact]
    public void RevokedAdmission_CancelsQueryToken_WithoutCancelingCaller()
    {
        using var caller = new CancellationTokenSource();
        using var revocation = new CancellationTokenSource();
        using var linked = new SqlExecutionLeaseCancellation(
            new RevocableLease(revocation.Token), caller.Token);

        Assert.False(linked.Token.IsCancellationRequested);
        revocation.Cancel();

        Assert.True(linked.Token.IsCancellationRequested);
        Assert.False(caller.IsCancellationRequested);
        Assert.ThrowsAny<OperationCanceledException>(
            () => linked.Token.ThrowIfCancellationRequested());
    }

    [Fact]
    public void CanceledCaller_StillCancelsLinkedQuery()
    {
        using var caller = new CancellationTokenSource();
        using var revocation = new CancellationTokenSource();
        using var linked = new SqlExecutionLeaseCancellation(
            new RevocableLease(revocation.Token), caller.Token);

        caller.Cancel();

        Assert.True(linked.Token.IsCancellationRequested);
        Assert.False(revocation.IsCancellationRequested);
    }

    [Fact]
    public void OrdinaryLease_PreservesOriginalCancellationToken()
    {
        using var caller = new CancellationTokenSource();
        using var linked = new SqlExecutionLeaseCancellation(
            new OrdinaryLease(), caller.Token);

        Assert.Equal(caller.Token, linked.Token);
        caller.Cancel();
        Assert.True(linked.Token.IsCancellationRequested);
    }

    private sealed class RevocableLease(CancellationToken revoked) : IRevocableSqlExecutionLease
    {
        public CancellationToken RevocationToken => revoked;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OrdinaryLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
