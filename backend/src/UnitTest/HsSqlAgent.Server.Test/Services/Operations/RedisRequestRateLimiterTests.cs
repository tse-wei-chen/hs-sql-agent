using HsSqlAgent.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class RedisRequestRateLimiterTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task AcquireAsync_ShouldParseAtomicScriptResult(long allowed, bool expected)
    {
        string? executedScript = null;
        RedisKey[]? executedKeys = null;
        RedisValue[]? executedValues = null;
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((script, keys, values, _) =>
            {
                executedScript = script;
                executedKeys = keys;
                executedValues = values;
            })
            .ReturnsAsync(RedisResult.Create(
            [
                RedisResult.Create((RedisValue)allowed),
                RedisResult.Create((RedisValue)2500)
            ]));
        var limiter = CreateLimiter(database.Object, RateLimiterFailureMode.FailClosed);

        var result = await limiter.AcquireAsync(
            new RateLimitRequest("ip:192.0.2.1", 3, TimeSpan.FromSeconds(10)),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.IsAllowed);
        Assert.True(result.IsAvailable);
        Assert.Contains("INCR", executedScript);
        Assert.Contains("PEXPIRE", executedScript);
        Assert.Equal("test:ip:192.0.2.1:3:10000", executedKeys![0].ToString());
        Assert.Equal(10_000, (long)executedValues![0]);
        Assert.Equal(3, (int)executedValues[1]);
        Assert.Equal(expected ? TimeSpan.Zero : TimeSpan.FromMilliseconds(2500), result.RetryAfter);
    }

    [Theory]
    [InlineData(RateLimiterFailureMode.FailOpen, true, true)]
    [InlineData(RateLimiterFailureMode.FailClosed, false, false)]
    public async Task AcquireAsync_ShouldHonorFailureMode(
        RateLimiterFailureMode failureMode,
        bool expectedAllowed,
        bool expectedAvailable)
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

        var result = await limiter.AcquireAsync(
            new RateLimitRequest("key", 1, TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedAllowed, result.IsAllowed);
        Assert.Equal(expectedAvailable, result.IsAvailable);
    }

    private static RedisRequestRateLimiter CreateLimiter(
        IDatabase database,
        RateLimiterFailureMode failureMode)
    {
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database);
        return new RedisRequestRateLimiter(
            multiplexer.Object,
            new RedisRequestRateLimiterOptions(failureMode, "test:"),
            NullLogger<RedisRequestRateLimiter>.Instance);
    }
}
