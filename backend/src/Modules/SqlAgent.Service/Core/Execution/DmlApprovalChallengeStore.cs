using System.Collections.Concurrent;

namespace SqlAgent.Service.Core.Execution;

public interface IDmlApprovalChallengeStore
{
    void Register(DmlApprovalChallenge challenge);
    bool TryConsume(DmlApprovalChallenge challenge);
}

/// <summary>
/// Process-local one-time challenge store. The interface allows a distributed implementation for
/// multi-instance deployments. Exact record equality prevents nonce reuse with modified fields.
/// </summary>
public sealed class InMemoryDmlApprovalChallengeStore(TimeProvider? timeProvider = null)
    : IDmlApprovalChallengeStore
{
    private readonly ConcurrentDictionary<string, DmlApprovalChallenge> _challenges =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public void Register(DmlApprovalChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        PurgeExpired();
        if (!_challenges.TryAdd(challenge.Nonce, challenge))
            throw new InvalidOperationException("Duplicate DML approval challenge nonce.");
    }

    public bool TryConsume(DmlApprovalChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        PurgeExpired();
        if (!_challenges.TryRemove(challenge.Nonce, out var registered))
            return false;
        return registered == challenge;
    }

    private void PurgeExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _challenges)
            if (pair.Value.ExpiresAt <= now)
                _challenges.TryRemove(pair.Key, out _);
    }
}
