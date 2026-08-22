using SqlAgent.Service.Core.Execution;
using Xunit;

namespace SqlAgent.Test.Services;

public class DmlApprovalChallengeStoreTests
{
    [Fact]
    public void Challenge_CanBeConsumedExactlyOnce()
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

        store.Register(challenge);

        Assert.True(store.TryConsume(challenge));
        Assert.False(store.TryConsume(challenge));
    }

    [Fact]
    public void ModifiedChallenge_DoesNotConsumeAsValidApproval()
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
        store.Register(challenge);

        Assert.False(store.TryConsume(challenge with { AffectedRows = 3 }));
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
