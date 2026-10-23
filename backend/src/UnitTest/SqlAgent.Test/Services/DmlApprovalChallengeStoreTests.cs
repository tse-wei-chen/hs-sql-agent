using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlApprovalChallengeStoreTests
{
    [Fact]
    public void Promotion_IsRequiredByTheV3CustomStoreContract()
    {
        var method = typeof(IDmlApprovalChallengeStore).GetMethod(
            nameof(IDmlApprovalChallengeStore.TryPromoteAsync));
        Assert.NotNull(method);
        Assert.True(method.IsAbstract);
    }

    [Fact]
    public async Task Challenge_CanBeConsumedExactlyOnce()
    {
        var store = new InMemoryDmlApprovalChallengeStore();
        var now = DateTimeOffset.UtcNow;
        var challenge = new DmlApprovalChallenge(
            "plan",
            "rows",
            2,
            "policy-v1",
            "approval-context-v1",
            now,
            now.AddMinutes(5),
            Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;

        await store.RegisterAsync(challenge, cancellationToken);

        Assert.True(await store.TryConsumeAsync(challenge, cancellationToken));
        Assert.False(await store.TryConsumeAsync(challenge, cancellationToken));
    }

    [Fact]
    public async Task ModifiedChallenge_DoesNotConsumeAsValidApproval()
    {
        var store = new InMemoryDmlApprovalChallengeStore();
        var now = DateTimeOffset.UtcNow;
        var challenge = new DmlApprovalChallenge(
            "plan",
            "rows",
            2,
            "policy-v1",
            "approval-context-v1",
            now,
            now.AddMinutes(5),
            Guid.NewGuid().ToString("N"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.RegisterAsync(challenge, cancellationToken);

        Assert.False(await store.TryConsumeAsync(
            challenge with { AffectedRows = 3 },
            cancellationToken));
    }

    [Fact]
    public async Task CancelledStoreOperation_StopsBeforeMutatingState()
    {
        var store = new InMemoryDmlApprovalChallengeStore();
        var now = DateTimeOffset.UtcNow;
        var challenge = new DmlApprovalChallenge(
            "plan",
            "rows",
            1,
            "policy-v1",
            "approval-context-v1",
            now,
            now.AddMinutes(5),
            Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.RegisterAsync(challenge, cancellation.Token));

        Assert.False(await store.TryConsumeAsync(
            challenge,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Promote_AtomicallyExchangesAllChildrenForOneAggregate()
    {
        var store = new InMemoryDmlApprovalChallengeStore();
        var now = DateTimeOffset.UtcNow;
        var children = new[]
        {
            Challenge("first", now),
            Challenge("second", now)
        };
        var aggregate = Challenge("aggregate", now);
        foreach (var child in children)
            await store.RegisterAsync(child);

        Assert.True(await store.TryPromoteAsync(children, aggregate));
        Assert.False(await store.TryConsumeAsync(children[0]));
        Assert.False(await store.TryConsumeAsync(children[1]));
        Assert.True(await store.TryConsumeAsync(aggregate));
        Assert.False(await store.TryConsumeAsync(aggregate));
    }

    [Fact]
    public async Task Promote_MismatchedOrExpiredChildDoesNotConsumeOtherChallenges()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryDmlApprovalChallengeStore();
        var first = Challenge("first", now);
        var second = Challenge("second", now);
        var batch = Challenge("batch", now);
        await store.RegisterAsync(first);
        await store.RegisterAsync(second);
        Assert.False(await store.TryPromoteAsync(
            [first, second with { AffectedRows = 99 }], batch));
        Assert.False(await store.TryConsumeAsync(batch));
        Assert.True(await store.TryConsumeAsync(first));
        Assert.True(await store.TryConsumeAsync(second));
    }

    [Fact]
    public async Task Promote_ConcurrentCallsAllowExactlyOneAggregate()
    {
        var store = new InMemoryDmlApprovalChallengeStore();
        var now = DateTimeOffset.UtcNow;
        var first = Challenge("first", now);
        var second = Challenge("second", now);
        var one = Challenge("aggregate-1", now);
        var two = Challenge("aggregate-2", now);
        await store.RegisterAsync(first);
        await store.RegisterAsync(second);

        var results = await Task.WhenAll(
            Task.Run(async () => await store.TryPromoteAsync([first, second], one)),
            Task.Run(async () => await store.TryPromoteAsync([first, second], two)));

        Assert.Single(results.Where(x => x));
        Assert.False(await store.TryConsumeAsync(first));
        Assert.False(await store.TryConsumeAsync(second));
        var aggregateResults = new[]
        {
            await store.TryConsumeAsync(one),
            await store.TryConsumeAsync(two)
        };
        Assert.Single(aggregateResults.Where(x => x));
    }

    [Fact]
    public async Task Promote_CancellationDoesNotConsumeRegisteredChildren()
    {
        var store = new InMemoryDmlApprovalChallengeStore();
        var now = DateTimeOffset.UtcNow;
        var first = Challenge("first", now);
        await store.RegisterAsync(first);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.TryPromoteAsync([first], Challenge("batch", now), cancellation.Token));
        Assert.True(await store.TryConsumeAsync(first));
    }

    private static DmlApprovalChallenge Challenge(string nonce, DateTimeOffset now) =>
        new("plan", "rows", 1, "policy", "context", now, now.AddMinutes(5), nonce);

    [Fact]
    public void UnorderedRowSetFingerprint_IgnoresOrderButNotIdentity()
    {
        var first = DmlFingerprintService.ComputeUnorderedRowSetFingerprint(
            new IReadOnlyList<object?>[] { new object?[] { 1 }, new object?[] { 2 } });
        var reordered = DmlFingerprintService.ComputeUnorderedRowSetFingerprint(
            new IReadOnlyList<object?>[] { new object?[] { 2 }, new object?[] { 1 } });
        var changed = DmlFingerprintService.ComputeUnorderedRowSetFingerprint(
            new IReadOnlyList<object?>[] { new object?[] { 3 }, new object?[] { 4 } });

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
    }
}
