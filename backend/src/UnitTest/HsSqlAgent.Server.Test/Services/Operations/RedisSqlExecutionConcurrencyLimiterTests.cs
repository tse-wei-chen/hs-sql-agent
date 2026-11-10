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


    [Fact]
    public async Task TryAcquireAsync_CancelledDuringSuccessfulRedisReply_ReleasesTokenAndThrows()
    {
        using var cancelled = new CancellationTokenSource();
        var redisEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redisReply = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTokens = new List<string>();
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags) =>
            {
                if (script.Contains("ZREMRANGEBYSCORE", StringComparison.Ordinal))
                {
                    redisEntered.TrySetResult();
                    return redisReply.Task;
                }

                Assert.Contains("ZREM", script, StringComparison.Ordinal);
                releaseTokens.Add(args[0].ToString());
                return Task.FromResult(RedisResult.Create((RedisValue)1));
            });
        var limiter = CreateLimiter(database.Object, RateLimiterFailureMode.FailClosed);

        var pending = limiter.TryAcquireAsync(cancelled.Token).AsTask();
        await redisEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        redisReply.SetResult(RedisResult.Create((RedisValue)1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(releaseTokens);
    }

    [Fact]
    public async Task TryAcquireAsync_CancelledDuringDeniedRedisReply_ThrowsCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        var redisEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redisReply = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(() =>
            {
                redisEntered.TrySetResult();
                return redisReply.Task;
            });
        var limiter = CreateLimiter(database.Object, RateLimiterFailureMode.FailClosed);

        var pending = limiter.TryAcquireAsync(cancelled.Token).AsTask();
        await redisEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        redisReply.SetResult(RedisResult.Create((RedisValue)0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task TryAcquireAsync_CancelledDuringRedisFailure_DoesNotUseFailureMode()
    {
        using var cancelled = new CancellationTokenSource();
        var redisEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redisReply = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(() =>
            {
                redisEntered.TrySetResult();
                return redisReply.Task;
            });
        var limiter = CreateLimiter(database.Object, RateLimiterFailureMode.FailOpen);
        var pending = limiter.TryAcquireAsync(cancelled.Token).AsTask();

        await redisEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancelled.Cancel();
        redisReply.SetException(new RedisException("simulated Redis interruption"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task RenewalRefused_RevokesAlreadyAdmittedSqlExecution()
    {
        var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new Mock<IDatabase>();
        database.Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags) =>
            {
                if (script.Contains("ZREMRANGEBYSCORE", StringComparison.Ordinal))
                    return Task.FromResult(RedisResult.Create((RedisValue)1));
                if (script.Contains("ZSCORE", StringComparison.Ordinal))
                {
                    renewalObserved.TrySetResult();
                    return Task.FromResult(RedisResult.Create((RedisValue)0));
                }
                return Task.FromResult(RedisResult.Create((RedisValue)1));
            });

        await using var limiter = CreateLimiter(database.Object, RateLimiterFailureMode.FailClosed);
        await using var lease = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        var revocable = Assert.IsAssignableFrom<IRevocableSqlExecutionLease>(lease);

        await renewalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Task.Delay(Timeout.InfiniteTimeSpan, revocable.RevocationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StalledRedisRenewal_LocalDeadlineRevokesSqlBeforeReply()
    {
        var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalReply = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new Mock<IDatabase>();
        database.Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags) =>
            {
                if (script.Contains("ZREMRANGEBYSCORE", StringComparison.Ordinal))
                    return Task.FromResult(RedisResult.Create((RedisValue)1));
                if (script.Contains("ZSCORE", StringComparison.Ordinal))
                {
                    renewalObserved.TrySetResult();
                    return renewalReply.Task;
                }
                return Task.FromResult(RedisResult.Create((RedisValue)1));
            });
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            MaxConcurrentSql = 1
        });
        await using var limiter = new RedisSqlExecutionConcurrencyLimiter(
            state.Object,
            new RedisSqlConcurrencyOptions("localhost:6379", "test:watchdog",
                TimeSpan.FromSeconds(1), RateLimiterFailureMode.FailClosed),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance,
            database.Object);

        var lease = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
        var revocable = Assert.IsAssignableFrom<IRevocableSqlExecutionLease>(lease);
        try
        {
            await renewalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await Task.Delay(Timeout.InfiniteTimeSpan, revocable.RevocationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(renewalReply.Task.IsCompleted);
        }
        finally
        {
            renewalReply.TrySetResult(RedisResult.Create((RedisValue)0));
            await lease.DisposeAsync();
        }
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
