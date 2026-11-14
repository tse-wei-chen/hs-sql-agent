using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class RedisSqlDelayedAdmissionTests
{
    [Fact]
    public async Task RedisAcquireReplyArrivesAfterPhysicalTtl_ExpiredAdmissionIsNeverHandedToSql()
    {
        // The Lua script may execute successfully while its network ACK is
        // delayed. Redis begins TTL at script execution, not at ACK receipt.
        var issued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgement = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = 0;
        var database = new Mock<IDatabase>();
        database.Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags) =>
            {
                if (script.Contains("ZREMRANGEBYSCORE", StringComparison.Ordinal))
                {
                    issued.TrySetResult();
                    return acknowledgement.Task;
                }
                if (script.Contains("ZREM", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref releaseCount);
                    return Task.FromResult(RedisResult.Create((RedisValue)1));
                }
                throw new InvalidOperationException("Unexpected Redis command in delayed-admission proof.");
            });

        var policy = new Mock<ISecurityPolicyRuntimeState>();
        policy.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel { MaxConcurrentSql = 1 });
        await using var limiter = new RedisSqlExecutionConcurrencyLimiter(
            policy.Object,
            new RedisSqlConcurrencyOptions("localhost:6379", "delayed-ack:test",
                TimeSpan.FromSeconds(1), RateLimiterFailureMode.FailClosed),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance,
            database.Object);
        var ct = TestContext.Current.CancellationToken;
        var acquiring = limiter.TryAcquireAsync(ct).AsTask();
        await issued.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(1200), ct);
        acknowledgement.TrySetResult(RedisResult.Create((RedisValue)1));

        var lease = await acquiring;
        if (lease is not null)
            await lease.DisposeAsync();
        Assert.Null(lease);
        Assert.Equal(1, Volatile.Read(ref releaseCount));
    }
}
