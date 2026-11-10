using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Redis 7 physical proof: independent limiter instances, independent Redis
/// connections, shared admission key, and actual lease renewals after TTL.
/// This does not claim protection during Redis partition/renewal loss.
/// </summary>
public sealed class RedisSqlConcurrencyIntegrationTests
{
    [Fact]
    public async Task IndependentInstances_RenewOneAdmissionAndReject24Competitors()
    {
        var endpoint = RequireRedis();
        var ct = TestContext.Current.CancellationToken;
        var key = "hs-sql-agent:physical-concurrency:" + Guid.NewGuid().ToString("N");

        await using var witness = await ConnectionMultiplexer.ConnectAsync(endpoint);
        var physical = witness.GetDatabase();
        await using var first = Limiter(endpoint, key);
        await using var second = Limiter(endpoint, key);

        try
        {
            var held = await first.TryAcquireAsync(ct);
            Assert.NotNull(held);
            await using (held)
            {
                // Two-second TTL and one-second renewal. The held first lease
                // must still occupy the Redis slot after several TTL periods.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                Assert.Equal(1, await physical.SortedSetLengthAsync(key));

                var start = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var competitors = Enumerable.Range(0, 24)
                    .Select(async i =>
                    {
                        await start.Task.WaitAsync(ct);
                        var contender = i % 2 == 0 ? first : second;
                        var lease = await contender.TryAcquireAsync(ct);
                        if (lease is null)
                            return false;
                        await lease.DisposeAsync();
                        return true;
                    }).ToArray();

                start.SetResult();
                Assert.All(await Task.WhenAll(competitors), admitted =>
                    Assert.False(admitted, "An independent Redis limiter admitted SQL while the first lease was held."));
                Assert.Equal(1, await physical.SortedSetLengthAsync(key));
            }

            Assert.Equal(0, await physical.SortedSetLengthAsync(key));
            await using (var resumed = await second.TryAcquireAsync(ct))
            {
                Assert.NotNull(resumed);
                Assert.Equal(1, await physical.SortedSetLengthAsync(key));
            }
            Assert.Equal(0, await physical.SortedSetLengthAsync(key));
        }
        finally
        {
            await physical.KeyDeleteAsync(key);
        }
    }

    [Fact]
    public async Task ExpiredRedisLease_CannotBeResurrectedByLateRenewal()
    {
        var endpoint = RequireRedis();
        var ct = TestContext.Current.CancellationToken;
        var key = "hs-sql-agent:expired-renew:" + Guid.NewGuid().ToString("N");

        await using var witness = await ConnectionMultiplexer.ConnectAsync(endpoint);
        var redis = witness.GetDatabase();
        await using var limiter = Limiter(endpoint, key);
        try
        {
            var lease = await limiter.TryAcquireAsync(ct);
            Assert.NotNull(lease);
            await using (lease)
            {
                var token = Assert.Single(await redis.SortedSetRangeByRankAsync(key));
                // Simulate a lease whose deadline has elapsed while its
                // sorted-set member remains: Redis does not prune by score
                // unless the acquisition script executes.
                var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds();
                await redis.SortedSetAddAsync(key, token, expiredAt, When.Always);
                Assert.Equal((double)expiredAt, await redis.SortedSetScoreAsync(key, token));
                await Task.Delay(TimeSpan.FromMilliseconds(1600), ct);

                var score = await redis.SortedSetScoreAsync(key, token);
                Assert.True(score is null || score <=
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    "An already expired Redis admission must not be resurrected by background renewal.");
                var revocable = Assert.IsAssignableFrom<IRevocableSqlExecutionLease>(lease);
                Assert.True(revocable.RevocationToken.IsCancellationRequested,
                    "Live Query must be notified when physical Redis rejects its expired token.");
            }
        }
        finally
        {
            await redis.KeyDeleteAsync(key);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalRedis_LateRenewAckAfterLeaseDisposal_NeverResurrectsReleasedToken(
        bool deferLuaExecution)
    {
        var endpoint = RequireRedis();
        var ct = TestContext.Current.CancellationToken;
        var key = "hs-sql-agent:delayed-renew-release:" + Guid.NewGuid().ToString("N");
        var renewRequestIssued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renewPhysicallyExecuted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedAck = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var witness = await ConnectionMultiplexer.ConnectAsync(endpoint);
        var physical = witness.GetDatabase();
        var delayedClient = new Mock<IDatabase>();
        delayedClient.Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags) =>
                script.Contains("ZSCORE", StringComparison.Ordinal)
                    ? DelayRenewAcknowledgementAsync(script, keys, args, flags)
                    : physical.ScriptEvaluateAsync(script, keys, args, flags));
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel { MaxConcurrentSql = 1 });
        await using var limiter = new RedisSqlExecutionConcurrencyLimiter(
            state.Object,
            new RedisSqlConcurrencyOptions(endpoint, key, TimeSpan.FromSeconds(2),
                RateLimiterFailureMode.FailClosed),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance,
            delayedClient.Object);
        await using var otherInstance = Limiter(endpoint, key);

        async Task<RedisResult> DelayRenewAcknowledgementAsync(
            string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags)
        {
            // Exercise both Redis processing orders: Lua executes before
            // disposal but the ACK is lost, or Lua is queued until AFTER
            // disposal. Both must leave the exact released token absent.
            renewRequestIssued.TrySetResult();
            if (deferLuaExecution)
                await delayedAck.Task;
            var result = await physical.ScriptEvaluateAsync(script, keys, args, flags);
            renewPhysicallyExecuted.TrySetResult();
            if (!deferLuaExecution)
                await delayedAck.Task;
            return result;
        }

        try
        {
            var lease = await limiter.TryAcquireAsync(ct);
            Assert.NotNull(lease);
            Task? disposing = null;
            try
            {
                var token = Assert.Single(await physical.SortedSetRangeByRankAsync(key));
                await (deferLuaExecution ? renewRequestIssued.Task : renewPhysicallyExecuted.Task)
                    .WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.Equal(1, await physical.SortedSetLengthAsync(key));
                disposing = lease.DisposeAsync().AsTask();
                // Disposal must release its token without awaiting the
                // unbounded Redis renewal reply.
                await disposing.WaitAsync(TimeSpan.FromSeconds(1), ct);
                Assert.False(delayedAck.Task.IsCompleted);
                Assert.Empty(await physical.SortedSetRangeByRankAsync(key));

                await using var admittedAgain = await otherInstance.TryAcquireAsync(ct);
                Assert.NotNull(admittedAgain);
                var differentToken = Assert.Single(
                    await physical.SortedSetRangeByRankAsync(key));
                Assert.NotEqual(token, differentToken);
            }
            finally
            {
                delayedAck.TrySetResult();
                if (disposing is null)
                    await lease.DisposeAsync();
                else
                    await disposing.WaitAsync(TimeSpan.FromSeconds(5), ct);
                if (renewRequestIssued.Task.IsCompleted)
                    await renewPhysicallyExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }

            // A late response for a previously executed renewal must not
            // resurrect the released owner or remove another owner's token.
            Assert.Empty(await physical.SortedSetRangeByRankAsync(key));
        }
        finally
        {
            await physical.KeyDeleteAsync(key);
        }
    }

    private static RedisSqlExecutionConcurrencyLimiter Limiter(string endpoint, string key)
    {
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            MaxConcurrentSql = 1
        });

        return new RedisSqlExecutionConcurrencyLimiter(
            state.Object,
            new RedisSqlConcurrencyOptions(
                endpoint, key, TimeSpan.FromSeconds(2),
                RateLimiterFailureMode.FailClosed),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance);
    }

    private static string RequireRedis()
    {
        var endpoint = Environment.GetEnvironmentVariable(
            "HS_SQL_AGENT_REDIS_INTEGRATION_CONNECTION");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(endpoint),
            "Real Redis 7 is required; CI runs this in redis-approval-integration.");
        return endpoint!;
    }
}
