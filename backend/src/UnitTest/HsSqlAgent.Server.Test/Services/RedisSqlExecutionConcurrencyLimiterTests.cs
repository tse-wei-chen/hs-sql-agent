using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class RedisSqlExecutionConcurrencyLimiterTests
{
    [Fact]
    public async Task TryAcquireAsync_ShouldAcquireAndReleaseUniqueLease()
    {
        var scripts = new List<string>();
        var values = new List<RedisValue[]>();
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((script, _, args, _) =>
            {
                scripts.Add(script);
                values.Add(args);
            })
            .ReturnsAsync(() => RedisResult.Create((RedisValue)1));
        var limiter = CreateLimiter(database.Object, RateLimiterFailureMode.FailClosed);

        var lease = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        await lease.DisposeAsync();

        Assert.Equal(2, scripts.Count);
        Assert.Contains("ZREMRANGEBYSCORE", scripts[0]);
        Assert.Contains("ZADD", scripts[0]);
        Assert.Contains("ZREM", scripts[1]);
        Assert.Equal(2, (int)values[0][0]);
        Assert.Equal(2_000, (long)values[0][1]);
        Assert.Equal(values[0][2].ToString(), values[1][0].ToString());
    }

    [Fact]
    public async Task TryAcquireAsync_ShouldReturnNullWhenClusterLimitReached()
    {
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)0));
        var limiter = CreateLimiter(database.Object, RateLimiterFailureMode.FailClosed);

        var lease = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);

        Assert.Null(lease);
    }

    [Theory]
    [InlineData(RateLimiterFailureMode.FailOpen, true)]
    [InlineData(RateLimiterFailureMode.FailClosed, false)]
    public async Task TryAcquireAsync_ShouldHonorFailureMode(
        RateLimiterFailureMode failureMode,
        bool shouldReturnLease)
    {
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisException("unavailable"));
        var limiter = CreateLimiter(database.Object, failureMode);

        var lease = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);

        Assert.Equal(shouldReturnLease, lease is not null);
        if (lease is not null)
            await lease.DisposeAsync();
    }

    private static RedisSqlExecutionConcurrencyLimiter CreateLimiter(
        IDatabase database,
        RateLimiterFailureMode failureMode)
    {
        var runtimeState = new Mock<ISecurityPolicyRuntimeState>();
        runtimeState.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            MaxConcurrentSql = 2
        });
        return new RedisSqlExecutionConcurrencyLimiter(
            runtimeState.Object,
            new RedisSqlConcurrencyOptions(
                "localhost:6379",
                "test:sql-concurrency",
                TimeSpan.FromSeconds(2),
                failureMode),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance,
            database);
    }
}
