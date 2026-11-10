using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real Redis 7: a released token must not be revived or erase another
/// process's admission, irrespective of whether ZREM executes before or
/// after an unresponsive release acknowledgement.
/// </summary>
public sealed class RedisSqlStalledReleaseIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisZremAckLost_DisposalBoundedAndLateZremCannotEraseNextOwner(
        bool delayPhysicalRelease)
    {
        var endpoint = Environment.GetEnvironmentVariable(
            "HS_SQL_AGENT_REDIS_INTEGRATION_CONNECTION");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(endpoint),
            "Real Redis 7 is required; CI runs this in redis-approval-integration.");
        var ct = TestContext.Current.CancellationToken;
        var key = "hs-sql-agent:stalled-zrem:" + Guid.NewGuid().ToString("N");

        var releaseIssued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePhysicallyExecuted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = await ConnectionMultiplexer.ConnectAsync(endpoint!);
        var physical = connection.GetDatabase();
        var intercepted = new Mock<IDatabase>();
        intercepted.Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags) =>
                script.StartsWith("return redis.call('ZREM'", StringComparison.Ordinal)
                    ? ReleaseWithUnacknowledgedReply(script, keys, args, flags)
                    : physical.ScriptEvaluateAsync(script, keys, args, flags));

        async Task<RedisResult> ReleaseWithUnacknowledgedReply(
            string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags)
        {
            releaseIssued.TrySetResult();
            if (delayPhysicalRelease)
                await releaseAck.Task;
            var result = await physical.ScriptEvaluateAsync(script, keys, args, flags);
            releasePhysicallyExecuted.TrySetResult();
            if (!delayPhysicalRelease)
                await releaseAck.Task;
            return result;
        }

        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(
            new SecurityPolicyModel { MaxConcurrentSql = 1 });
        await using var first = new RedisSqlExecutionConcurrencyLimiter(
            state.Object,
            new RedisSqlConcurrencyOptions(endpoint!, key, TimeSpan.FromSeconds(2),
                RateLimiterFailureMode.FailClosed),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance,
            intercepted.Object);
        await using var next = new RedisSqlExecutionConcurrencyLimiter(
            state.Object,
            new RedisSqlConcurrencyOptions(endpoint!, key, TimeSpan.FromSeconds(2),
                RateLimiterFailureMode.FailClosed),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance);

        IAsyncDisposable? firstLease = null;
        IAsyncDisposable? nextLease = null;
        Task? disposing = null;
        try
        {
            firstLease = await first.TryAcquireAsync(ct);
            Assert.NotNull(firstLease);
            var oldToken = Assert.Single(await physical.SortedSetRangeByRankAsync(key));
            disposing = firstLease.DisposeAsync().AsTask();
            await releaseIssued.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // Release ACK is deliberately absent. The old code blocks forever.
            await disposing.WaitAsync(TimeSpan.FromSeconds(4), ct);
            Assert.False(releaseAck.Task.IsCompleted);

            if (delayPhysicalRelease)
            {
                // ZREM has not reached Redis; honor the 2-second Redis TTL.
                // A second process may only acquire after the old score expires.
                await Task.Delay(TimeSpan.FromMilliseconds(2400), ct);
            }
            else
            {
                await releasePhysicallyExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                Assert.Empty(await physical.SortedSetRangeByRankAsync(key));
            }

            nextLease = await next.TryAcquireAsync(ct);
            Assert.NotNull(nextLease);
            var nextToken = Assert.Single(await physical.SortedSetRangeByRankAsync(key));
            Assert.NotEqual(oldToken, nextToken);

            // Complete the original release after the new owner exists.
            // ZREM must only affect the first owner's exact token.
            releaseAck.TrySetResult();
            await releasePhysicallyExecuted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(nextToken, Assert.Single(
                await physical.SortedSetRangeByRankAsync(key)));
        }
        finally
        {
            releaseAck.TrySetResult();
            if (disposing is null && firstLease is not null)
                await firstLease.DisposeAsync();
            else if (disposing is not null)
                await disposing.WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (nextLease is not null)
                await nextLease.DisposeAsync();
            await physical.KeyDeleteAsync(key);
        }
    }
}
