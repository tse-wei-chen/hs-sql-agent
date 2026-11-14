using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// A confirmed target COMMIT is not uncommitted by a lost Redis ZREM ACK.
/// Releasing a SQL admission is best-effort cleanup, not a commit gate.
/// </summary>
public sealed class RedisSqlStalledReleaseTests
{
    [Fact]
    public async Task NativeSqliteCommitThenRedisReleaseAckStalls_DisposalMustFinish()
    {
        var releaseIssued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var redis = new Mock<IDatabase>();
        redis.Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags) =>
            {
                if (script.Contains("ZREMRANGEBYSCORE", StringComparison.Ordinal))
                    return Task.FromResult(RedisResult.Create((RedisValue)1));
                if (script.Contains("ZSCORE", StringComparison.Ordinal))
                    return Task.FromResult(RedisResult.Create((RedisValue)1));
                Assert.Contains("ZREM", script, StringComparison.Ordinal);
                releaseIssued.TrySetResult();
                return releaseAck.Task;
            });

        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(
            new SecurityPolicyModel { MaxConcurrentSql = 1 });
        await using var limiter = new RedisSqlExecutionConcurrencyLimiter(
            state.Object,
            new RedisSqlConcurrencyOptions("localhost:6379", "test:release-ack",
                TimeSpan.FromSeconds(2), RateLimiterFailureMode.FailClosed),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance,
            redis.Object);

        var lease = await limiter.TryAcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(lease);

        await using var db = new SqliteConnection("Data Source=:memory:");
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "CREATE TABLE writes (id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using (var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await using var command = db.CreateCommand();
            command.Transaction = (SqliteTransaction)tx;
            command.CommandText = "INSERT INTO writes(id) VALUES (9)";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }

        Task? disposing = null;
        try
        {
            disposing = lease.DisposeAsync().AsTask();
            await releaseIssued.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            // Old implementation never completes until the ACK is returned.
            // A completed native COMMIT must be reportable even when Redis
            // never confirms its *cleanup* operation.
            await disposing.WaitAsync(TimeSpan.FromSeconds(4),
                TestContext.Current.CancellationToken);
            Assert.False(releaseAck.Task.IsCompleted);
            await using var verify = db.CreateCommand();
            verify.CommandText = "SELECT COUNT(*) FROM writes WHERE id = 9";
            Assert.Equal(1L, Convert.ToInt64(
                await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        }
        finally
        {
            // RED must clean up the test even if disposal was incorrectly hung.
            releaseAck.TrySetResult(RedisResult.Create((RedisValue)1));
            if (disposing is null)
                await lease.DisposeAsync();
            else
                await disposing.WaitAsync(TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CanceledLateAcquire_StalledRedisReleaseCannotMaskOriginalCancellation()
    {
        using var canceled = new CancellationTokenSource();
        var acquireIssued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acquireReply = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIssued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReply = new TaskCompletionSource<RedisResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var redis = new Mock<IDatabase>();
        redis.Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns((string script, RedisKey[] keys, RedisValue[] args, CommandFlags flags) =>
            {
                if (script.Contains("ZREMRANGEBYSCORE", StringComparison.Ordinal))
                {
                    acquireIssued.TrySetResult();
                    return acquireReply.Task;
                }

                Assert.StartsWith("return redis.call('ZREM'", script, StringComparison.Ordinal);
                releaseIssued.TrySetResult();
                return releaseReply.Task;
            });

        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(
            new SecurityPolicyModel { MaxConcurrentSql = 1 });
        await using var limiter = new RedisSqlExecutionConcurrencyLimiter(
            state.Object,
            new RedisSqlConcurrencyOptions("localhost:6379", "test:late-acquire-zrem",
                TimeSpan.FromSeconds(2), RateLimiterFailureMode.FailClosed),
            NullLogger<RedisSqlExecutionConcurrencyLimiter>.Instance, redis.Object);

        var pending = limiter.TryAcquireAsync(canceled.Token).AsTask();
        try
        {
            await acquireIssued.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            canceled.Cancel();
            // Redis may have admitted the request despite caller cancellation.
            acquireReply.TrySetResult(RedisResult.Create((RedisValue)1));
            await releaseIssued.Task.WaitAsync(TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            // The original cancellation must win over missing Redis cleanup
            // acknowledgement. No lease is ever returned to the caller.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(4),
                    TestContext.Current.CancellationToken));
            Assert.False(releaseReply.Task.IsCompleted);
        }
        finally
        {
            releaseReply.TrySetResult(RedisResult.Create((RedisValue)1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken));
        }
    }
}
