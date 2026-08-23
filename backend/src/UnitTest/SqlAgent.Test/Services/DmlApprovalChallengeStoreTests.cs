using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlApprovalChallengeStoreTests
{
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
            now,
            now.AddMinutes(5),
            Guid.NewGuid().ToString("N"));

        await store.RegisterAsync(challenge);

        Assert.True(await store.TryConsumeAsync(challenge));
        Assert.False(await store.TryConsumeAsync(challenge));
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
            now,
            now.AddMinutes(5),
            Guid.NewGuid().ToString("N"));
        await store.RegisterAsync(challenge);

        Assert.False(await store.TryConsumeAsync(challenge with { AffectedRows = 3 }));
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
            now,
            now.AddMinutes(5),
            Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.RegisterAsync(challenge, cancellation.Token));

        Assert.False(await store.TryConsumeAsync(challenge));
    }

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
