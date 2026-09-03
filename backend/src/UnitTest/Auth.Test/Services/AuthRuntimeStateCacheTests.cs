using System.Collections.Concurrent;
using System.Data.Common;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Models;
using Auth.Service.Services;
using Common.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Auth.Test.Services;

public sealed class AuthRuntimeStateCacheTests
{
    [Fact]
    public async Task GetOrLoadAsync_CacheMissUsesOneSqlCommand_ThenCacheHitUsesNone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var counter = new CommandCountingInterceptor();
        var options = new DbContextOptionsBuilder<AuthContext>()
            .UseSqlite(connection)
            .AddInterceptors(counter)
            .Options;
        await using var context = new AuthContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var member = new Member
        {
            Id = 7,
            Username = "cached",
            Mail = "cached@test.com",
            NormalizedMail = "CACHED@TEST.COM",
            PasswordHash = "hash",
            IsActive = true,
            SecurityVersion = 4
        };
        context.Members.Add(member);
        context.AuthSessions.AddRange(
            new AuthSession
            {
                Id = Guid.NewGuid(),
                MemberId = member.Id,
                CurrentRefreshTokenHash = new string('a', 64),
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            },
            new AuthSession
            {
                Id = Guid.NewGuid(),
                MemberId = member.Id,
                CurrentRefreshTokenHash = new string('b', 64),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
            },
            new AuthSession
            {
                Id = Guid.NewGuid(),
                MemberId = member.Id,
                CurrentRefreshTokenHash = new string('c', 64),
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                RevokedAt = DateTime.UtcNow
            });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ChangeTracker.Clear();
        counter.Reset();

        var backingCache = new RecordingCacheService();
        var cache = new AuthRuntimeStateCache(backingCache);

        var first = await cache.GetOrLoadAsync(context, member.Id, TestContext.Current.CancellationToken);
        var second = await cache.GetOrLoadAsync(context, member.Id, TestContext.Current.CancellationToken);

        Assert.True(first.Exists);
        Assert.True(first.IsActive);
        Assert.Equal(4, first.SecurityVersion);
        Assert.Single(first.ActiveSessions);
        Assert.Same(first, second);
        Assert.Equal(1, counter.ReaderCommandCount);
    }

    [Fact]
    public async Task RunWithBarrierAsync_PublishesFailClosedStateBeforeMutation()
    {
        var backingCache = new RecordingCacheService();
        var cache = new AuthRuntimeStateCache(backingCache);
        var barrierObserved = false;

        await cache.RunWithBarrierAsync(
            9,
            "security mutation",
            _ =>
            {
                var state = Assert.Single(backingCache.Values.OfType<AuthRuntimeState>());
                barrierObserved = state.IsBarrier && state.BarrierReason == "security mutation";
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.True(barrierObserved);
        Assert.Empty(backingCache.Values);
    }

    [Fact]
    public async Task RunWithBarrierAsync_MutationFailureClearsBarrierAndRethrows()
    {
        var backingCache = new RecordingCacheService();
        var cache = new AuthRuntimeStateCache(backingCache);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.RunWithBarrierAsync(
                9,
                "security mutation",
                _ => throw new InvalidOperationException("persistence failed"),
                TestContext.Current.CancellationToken));

        Assert.Empty(backingCache.Values);
    }

    private sealed class RecordingCacheService : ICacheService
    {
        private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);

        public IReadOnlyCollection<object> Values => [.. _values.Values];

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _values.TryGetValue(key, out var value) && value is T typed ? typed : default);
        }

        public Task SetAsync<T>(
            string key,
            T value,
            TimeSpan? absoluteExpireTime = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key] = value!;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _readerCommandCount;

        public int ReaderCommandCount => Volatile.Read(ref _readerCommandCount);

        public void Reset() => Interlocked.Exchange(ref _readerCommandCount, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readerCommandCount);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
