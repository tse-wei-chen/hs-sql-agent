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
/// Negative physical control for #575: two disconnected Redis primaries are
/// NOT a quorum lease coordinator. Both may grant one operation with the
/// same sorted-set key. No real SQL operation is executed in this probe.
/// Explicit Sentinel/multi-endpoint SQL configurations are unsupported at DI.
/// </summary>
public sealed class RedisIndependentPrimariesTestcontainersTests
{
    [Fact]
    public async Task SplitBrainRedisPrimariesCannotProveGlobalSqlConcurrencyCeiling()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var primaryA = new ContainerBuilder("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(6379))
            .Build();
        await using var primaryB = new ContainerBuilder("redis:7-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(6379))
            .Build();

        await primaryA.StartAsync(ct);
        await primaryB.StartAsync(ct);

        static string Endpoint(string hostname, int port) =>
            hostname + ":" + port +
            ",abortConnect=false,connectTimeout=1000,asyncTimeout=1000,syncTimeout=1000";

        var endpointA = Endpoint(primaryA.Hostname, primaryA.GetMappedPublicPort(6379));
        var endpointB = Endpoint(primaryB.Hostname, primaryB.GetMappedPublicPort(6379));
        Assert.NotEqual(endpointA, endpointB);
        var key = "hsql:negative-split-brain:" + Guid.NewGuid().ToString("N");
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(
            new SecurityPolicyModel { MaxConcurrentSql = 1 });
        var logger = NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance;

        await using var witnessA = await ConnectionMultiplexer.ConnectAsync(endpointA);
        await using var witnessB = await ConnectionMultiplexer.ConnectAsync(endpointB);
        await using var limiterA = new RedisSqlExecutionConcurrencyLimiter(
            state.Object, new RedisSqlConcurrencyOptions(
                endpointA, key, TimeSpan.FromSeconds(5),
                RateLimiterFailureMode.FailClosed), logger);
        await using var limiterB = new RedisSqlExecutionConcurrencyLimiter(
            state.Object, new RedisSqlConcurrencyOptions(
                endpointB, key, TimeSpan.FromSeconds(5),
                RateLimiterFailureMode.FailClosed), logger);

        await using var leaseA = await limiterA.TryAcquireAsync(ct);
        Assert.NotNull(leaseA);
        // A second client using the same live primary is correctly rejected.
        await using var samePrimary = new RedisSqlExecutionConcurrencyLimiter(
            state.Object, new RedisSqlConcurrencyOptions(
                endpointA, key, TimeSpan.FromSeconds(5),
                RateLimiterFailureMode.FailClosed), logger);
        Assert.Null(await samePrimary.TryAcquireAsync(ct));

        // Distinct, independently healthy primaries both admit: this proves
        // why async Redis failover and split brain cannot be advertised as a
        // strict global SQL admission/lease guarantee.
        await using var leaseB = await limiterB.TryAcquireAsync(ct);
        Assert.NotNull(leaseB);
        Assert.Equal(1L, await witnessA.GetDatabase().SortedSetLengthAsync(key));
        Assert.Equal(1L, await witnessB.GetDatabase().SortedSetLengthAsync(key));
    }
}
