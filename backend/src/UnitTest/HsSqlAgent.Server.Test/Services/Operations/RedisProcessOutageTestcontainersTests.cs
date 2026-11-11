using Admin.Service.Interfaces;
using Admin.Service.Models;
using DotNet.Testcontainers.Builders;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// #575 real Docker-backed Redis 7 process outage: two independent limiter
/// instances on separate multiplexer connections. Does not use mocked Redis
/// acknowledgements, clock manipulation, or an in-memory admission store.
/// </summary>
public sealed class RedisProcessOutageTestcontainersTests
{
    [Fact]
    public async Task RedisProcessStops_HeldPhysicalLeaseRevoked_AndNextInstanceFailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var redis = new ContainerBuilder("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(6379))
            .Build();
        await redis.StartAsync(ct);

        var endpoint = redis.Hostname + ":" + redis.GetMappedPublicPort(6379);
        var connectionString =
            endpoint + ",abortConnect=false,connectTimeout=1000,asyncTimeout=1000,syncTimeout=1000";
        var key = "hsql:real-redis-process-outage:" + Guid.NewGuid().ToString("N");
        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(
            new SecurityPolicyModel { MaxConcurrentSql = 1 });
        var options = new RedisSqlConcurrencyOptions(
            connectionString, key, TimeSpan.FromSeconds(3),
            RateLimiterFailureMode.FailClosed);

        await using var physicalWitness = await ConnectionMultiplexer.ConnectAsync(connectionString);
        await using var first = new RedisSqlExecutionConcurrencyLimiter(
            policy.Object, options, NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance);
        await using var otherInstance = new RedisSqlExecutionConcurrencyLimiter(
            policy.Object, options, NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance);
        var physical = physicalWitness.GetDatabase();
        var lease = await first.TryAcquireAsync(ct);
        Assert.NotNull(lease);
        await using (lease)
        {
            var revocable = Assert.IsAssignableFrom<IRevocableSqlExecutionLease>(lease);
            Assert.Equal(1L, await physical.SortedSetLengthAsync(key));

            var rejected = await otherInstance.TryAcquireAsync(ct);
            Assert.Null(rejected);
            Assert.Equal(1L, await physical.SortedSetLengthAsync(key));

            // Stop the real Redis container. Both clients lose their physical
            // Redis service, not merely a mocked ScriptEvaluateAsync response.
            await redis.StopAsync(ct);

            // The admission's local TTL is a conservative fence even when
            // native Redis renewal cannot be acknowledged. No success may
            // be reported by a still-running Query after lease revocation.
            await Task.Delay(TimeSpan.FromMilliseconds(3500), ct);
            Assert.True(revocable.RevocationToken.IsCancellationRequested,
                "An admitted Query must be revoked after real Redis outage.");

            var later = await otherInstance.TryAcquireAsync(ct)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(8), ct);
            Assert.Null(later);
            Assert.True(revocable.RevocationToken.IsCancellationRequested,
                "The previously revoked admission must not become live again.");
        }
    }
}
