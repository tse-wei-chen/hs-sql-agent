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
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, DmlApprovalChallenge> _challenges =
        new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ValueTask RegisterAsync(
        DmlApprovalChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            PurgeExpired();
            if (challenge.ExpiresAt <= _timeProvider.GetUtcNow())
                throw new InvalidOperationException("Cannot register an expired DML approval challenge.");
            if (!_challenges.TryAdd(challenge.Nonce, challenge))
                throw new InvalidOperationException("Duplicate DML approval challenge nonce.");
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryConsumeAsync(
        DmlApprovalChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            PurgeExpired();
            if (!_challenges.TryRemove(challenge.Nonce, out var registered))
                return ValueTask.FromResult(false);
            return ValueTask.FromResult(registered == challenge);
        }
    }

    public ValueTask<bool> TryPromoteAsync(
        IReadOnlyList<DmlApprovalChallenge> children,
        DmlApprovalChallenge aggregate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(aggregate);
        cancellationToken.ThrowIfCancellationRequested();
        if (children.Count == 0)
            throw new InvalidOperationException("Cannot promote an empty DML approval batch.");
        if (string.IsNullOrWhiteSpace(aggregate.Nonce)
            || children.Any(x => x is null || string.IsNullOrWhiteSpace(x.Nonce))
            || children.Select(x => x.Nonce).Distinct(StringComparer.Ordinal).Count() != children.Count
            || children.Any(x => string.Equals(x.Nonce, aggregate.Nonce, StringComparison.Ordinal)))
            throw new InvalidOperationException("Atomic DML approval promotion requires distinct valid nonces.");

        lock (_gate)
        {
            PurgeExpired();
            var now = _timeProvider.GetUtcNow();
            if (aggregate.ExpiresAt <= now
                || children.Any(x => x.ExpiresAt <= now)
                || _challenges.ContainsKey(aggregate.Nonce))
                return ValueTask.FromResult(false);
            foreach (var child in children)
                if (!_challenges.TryGetValue(child.Nonce, out var registered)
                    || registered != child)
                    return ValueTask.FromResult(false);

            _challenges[aggregate.Nonce] = aggregate;
            foreach (var child in children)
                _challenges.TryRemove(child.Nonce, out _);
            return ValueTask.FromResult(true);
        }
    }

    private void PurgeExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var pair in _challenges)
            if (pair.Value.ExpiresAt <= now)
                _challenges.TryRemove(pair.Key, out _);
    }
}
