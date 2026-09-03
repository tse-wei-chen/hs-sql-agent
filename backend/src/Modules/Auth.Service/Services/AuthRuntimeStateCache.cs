using Auth.Service.Data;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Auth.Service.Services;

public sealed class AuthRuntimeStateCache(ICacheService cache) : IAuthRuntimeStateCache
{
    private const string CacheKeyPrefix = "auth_runtime_v1_member_";
    private static readonly TimeSpan ValidStateTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MissingMemberTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BarrierTtl = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim[] LoadLocks =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly ICacheService _cache = cache;

    public async Task<AuthRuntimeState> GetOrLoadAsync(
        IAuthContext context,
        int memberId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (memberId <= 0) throw new ArgumentOutOfRangeException(nameof(memberId));

        var key = Key(memberId);
        var cached = await _cache.GetAsync<AuthRuntimeState>(key, cancellationToken);
        if (cached is not null)
            return cached;

        var gate = LoadLocks[(memberId & int.MaxValue) % LoadLocks.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            cached = await _cache.GetAsync<AuthRuntimeState>(key, cancellationToken);
            if (cached is not null)
                return cached;

            var now = DateTime.UtcNow;
            var loaded = await context.Members
                .AsNoTracking()
                .Where(member => member.Id == memberId)
                .Select(member => new AuthRuntimeState
                {
                    Exists = true,
                    IsActive = member.IsActive,
                    SecurityVersion = member.SecurityVersion,
                    ActiveSessions = member.AuthSessions
                        .Where(session => session.RevokedAt == null && session.ExpiresAt > now)
                        .Select(session => new AuthRuntimeSessionState
                        {
                            Id = session.Id,
                            ExpiresAt = session.ExpiresAt
                        })
                        .ToArray()
                })
                .FirstOrDefaultAsync(cancellationToken)
                ?? new AuthRuntimeState { Exists = false };

            await _cache.SetAsync(
                key,
                loaded,
                loaded.Exists ? ValidStateTtl : MissingMemberTtl,
                cancellationToken);
            return loaded;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task RunWithBarrierAsync(
        int memberId,
        string reason,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default) =>
        RunWithBarriersAsync([memberId], reason, mutation, cancellationToken);

    public async Task RunWithBarriersAsync(
        IReadOnlyCollection<int> memberIds,
        string reason,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(mutation);

        var ids = memberIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (ids.Length == 0)
        {
            await mutation(cancellationToken);
            return;
        }

        var barrier = new AuthRuntimeState
        {
            Exists = true,
            IsBarrier = true,
            BarrierReason = reason
        };
        var written = new List<string>(ids.Length);
        try
        {
            foreach (var memberId in ids)
            {
                var key = Key(memberId);
                await _cache.SetAsync(key, barrier, BarrierTtl, CancellationToken.None);
                written.Add(key);
            }
        }
        catch
        {
            await BestEffortRemoveAsync(written);
            throw;
        }

        try
        {
            await mutation(cancellationToken);
        }
        catch
        {
            await BestEffortRemoveAsync(written);
            throw;
        }

        // The committed mutation is authoritative. Removing the barrier forces the next request
        // to rebuild from DB; cleanup failure leaves the short-lived barrier fail-closed.
        await BestEffortRemoveAsync(written);
    }

    public async Task InvalidateAsync(
        int memberId,
        CancellationToken cancellationToken = default)
    {
        if (memberId <= 0) return;
        try
        {
            await _cache.RemoveAsync(Key(memberId), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Session creation / refresh invalidation is availability-only: stale state can reject
            // the new token early, but cannot authorize a revoked or security-version-mismatched token.
        }
    }

    private async Task BestEffortRemoveAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await _cache.RemoveAsync(key, CancellationToken.None);
            }
            catch
            {
                // Keep the barrier until TTL expiry rather than mask the original mutation outcome.
            }
        }
    }

    internal static string Key(int memberId) => $"{CacheKeyPrefix}{memberId}";
}
