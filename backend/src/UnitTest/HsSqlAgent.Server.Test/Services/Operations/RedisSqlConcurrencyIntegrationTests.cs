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
