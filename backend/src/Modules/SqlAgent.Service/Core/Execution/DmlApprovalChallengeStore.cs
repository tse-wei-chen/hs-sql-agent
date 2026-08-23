using System.Collections.Concurrent;

namespace SqlAgent.Service.Core.Execution;

public interface IDmlApprovalChallengeStore
{
    ValueTask RegisterAsync(
        DmlApprovalChallenge challenge,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryConsumeAsync(
        DmlApprovalChallenge challenge,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-local one-time challenge store. The async interface allows distributed implementations
/// without blocking threads. Exact record equality prevents nonce reuse with modified fields.
/// </summary>
public sealed class InMemoryDmlApprovalChallengeStore(TimeProvider? timeProvider = null)
    : IDmlApprovalChallengeStore
{
    private readonly ConcurrentDictionary<string, DmlApprovalChallenge> _challenges =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ValueTask RegisterAsync(
        DmlApprovalChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();
        PurgeExpired();
        if (!_challenges.TryAdd(challenge.Nonce, challenge))
            throw new InvalidOperationException("Duplicate DML approval challenge nonce.");
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryConsumeAsync(
        DmlApprovalChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();
        PurgeExpired();
        if (!_challenges.TryRemove(challenge.Nonce, out var registered))
            return ValueTask.FromResult(false);
        return ValueTask.FromResult(registered == challenge);
    }

    private void PurgeExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _challenges)
            if (pair.Value.ExpiresAt <= now)
                _challenges.TryRemove(pair.Key, out _);
    }
}
