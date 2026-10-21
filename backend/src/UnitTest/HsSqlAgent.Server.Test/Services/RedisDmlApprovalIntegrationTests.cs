using System.IO;
using HsSqlAgent.Server.Services;
using Moq;
using SqlAgent.Service.Core.Execution;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

/// <summary>
/// Real Redis integration, intentionally separate from the fast mocked Redis tests.
/// GitHub Actions provides Redis 7 and runs this class in a dedicated required job.
/// </summary>
public sealed class RedisDmlApprovalIntegrationTests
{
    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("HS_SQL_AGENT_REDIS_INTEGRATION_CONNECTION");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(value),
            "A real Redis endpoint is required; CI executes this suite in redis-approval-integration.");
        return value!;
    }

    private static RedisDmlApprovalChallengeOptions Options(string connectionString) =>
        new(connectionString, "hs-sql-redis-race-" + Guid.NewGuid().ToString("N") + ":");

    private static RedisKey Key(RedisDmlApprovalChallengeOptions options, string nonce) =>
        options.KeyPrefix + "{dml-approval}:" + nonce;

    private static DmlApprovalChallenge Challenge(string nonce, TimeSpan? ttl = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new DmlApprovalChallenge(
            "compiled-plan-" + nonce, "rowset", 2, "policy-v1",
            "approval-context-v1", now, now.Add(ttl ?? TimeSpan.FromMinutes(5)), nonce)
        {
            ApprovalPlanBinding = new string('a', 64)
        };
    }

    [Fact]
    public async Task ConcurrentPromotions_AcrossIndependentStores_HaveExactlyOneWinner()
    {
        var connection = ConnectionString();
        var options = Options(connection);
        await using var inspector = await ConnectionMultiplexer.ConnectAsync(connection);
        await using var first = new RedisDmlApprovalChallengeStore(options);
        await using var second = new RedisDmlApprovalChallengeStore(options);
        var db = inspector.GetDatabase();
        var children = new[] { Challenge("child-1"), Challenge("child-2") };
        var aggregates = Enumerable.Range(0, 12)
            .Select(i => Challenge("batch-" + i)).ToArray();
        var keys = children.Concat(aggregates).Select(x => Key(options, x.Nonce)).ToArray();

        try
        {
            foreach (var child in children)
                await first.RegisterAsync(child, TestContext.Current.CancellationToken);

            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = aggregates.Select(async (aggregate, index) =>
            {
                await signal.Task;
                var store = index % 2 == 0 ? first : second;
                return await store.TryPromoteAsync(children, aggregate,
                    TestContext.Current.CancellationToken);
            }).ToArray();

            signal.SetResult();
            var outcomes = await Task.WhenAll(attempts);
            var winnerIndex = Assert.Single(Enumerable.Range(0, outcomes.Length),
                i => outcomes[i]);
            var winner = aggregates[winnerIndex];

            Assert.False(await db.KeyExistsAsync(Key(options, children[0].Nonce)));
            Assert.False(await db.KeyExistsAsync(Key(options, children[1].Nonce)));
            Assert.True(await db.KeyExistsAsync(Key(options, winner.Nonce)));
            Assert.Equal(1, (await Task.WhenAll(aggregates
                .Select(async x => await db.KeyExistsAsync(Key(options, x.Nonce)))))
                .Count(exists => exists));

            var consume = await Task.WhenAll(
                first.TryConsumeAsync(winner, TestContext.Current.CancellationToken).AsTask(),
                second.TryConsumeAsync(winner, TestContext.Current.CancellationToken).AsTask());
            Assert.Single(consume, success => success);
            Assert.False(await first.TryConsumeAsync(winner,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await db.KeyDeleteAsync(keys);
        }
    }

    [Fact]
    public async Task FailedPromotion_WrongChildOrOccupiedAggregate_DoesNotDeleteAnyChild()
    {
        var connection = ConnectionString();
        var options = Options(connection);
        await using var inspector = await ConnectionMultiplexer.ConnectAsync(connection);
        await using var store = new RedisDmlApprovalChallengeStore(options);
        var db = inspector.GetDatabase();
        var children = new[] { Challenge("a"), Challenge("b") };
        var aggregate = Challenge("aggregate");
        var keys = children.Append(aggregate).Select(x => Key(options, x.Nonce)).ToArray();

        try
        {
            foreach (var child in children)
                await store.RegisterAsync(child, TestContext.Current.CancellationToken);
            var forged = children[1] with { AffectedRows = 99 };
            Assert.False(await store.TryPromoteAsync(
                [children[0], forged], aggregate, TestContext.Current.CancellationToken));
            Assert.All(children, child =>
                Assert.True(db.KeyExists(Key(options, child.Nonce))));
            Assert.False(await db.KeyExistsAsync(Key(options, aggregate.Nonce)));

            await store.RegisterAsync(aggregate, TestContext.Current.CancellationToken);
            Assert.False(await store.TryPromoteAsync(children, aggregate,
                TestContext.Current.CancellationToken));
            Assert.All(children, child =>
                Assert.True(db.KeyExists(Key(options, child.Nonce))));

            Assert.True(await store.TryConsumeAsync(aggregate,
                TestContext.Current.CancellationToken));
            Assert.True(await store.TryPromoteAsync(children, aggregate,
                TestContext.Current.CancellationToken));
            Assert.True(await store.TryConsumeAsync(aggregate,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await db.KeyDeleteAsync(keys);
        }
    }

    [Fact]
    public async Task RacingSingleChildConsumeVsAggregatePromotion_NeverDoubleAuthorizes()
    {
        var connection = ConnectionString();
        var options = Options(connection);
        await using var inspector = await ConnectionMultiplexer.ConnectAsync(connection);
        await using var first = new RedisDmlApprovalChallengeStore(options);
        await using var second = new RedisDmlApprovalChallengeStore(options);
        var db = inspector.GetDatabase();

        // If the single child consume wins, the batch must be rejected.
        // If batch promotion wins, the child must not be individually spent.
        for (var round = 0; round < 10; round++)
        {
            var children = new[]
            {
                Challenge($"race-{round}-a"),
                Challenge($"race-{round}-b")
            };
            var aggregate = Challenge($"race-{round}-aggregate");
            var keys = children.Append(aggregate).Select(x => Key(options, x.Nonce)).ToArray();

            try
            {
                foreach (var child in children)
                    await first.RegisterAsync(child, TestContext.Current.CancellationToken);

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var consume = Task.Run(async () =>
                {
                    await signal.Task;
                    return await first.TryConsumeAsync(children[0],
                        TestContext.Current.CancellationToken);
                });
                var promote = Task.Run(async () =>
                {
                    await signal.Task;
                    return await second.TryPromoteAsync(children, aggregate,
                        TestContext.Current.CancellationToken);
                });
                signal.SetResult();
                await Task.WhenAll(consume, promote);

                Assert.NotEqual(consume.Result, promote.Result);
                if (consume.Result)
                {
                    Assert.False(await db.KeyExistsAsync(Key(options, children[0].Nonce)));
                    Assert.True(await db.KeyExistsAsync(Key(options, children[1].Nonce)));
                    Assert.False(await db.KeyExistsAsync(Key(options, aggregate.Nonce)));
                    Assert.True(await second.TryConsumeAsync(children[1],
                        TestContext.Current.CancellationToken));
                }
                else
                {
                    Assert.False(await db.KeyExistsAsync(Key(options, children[0].Nonce)));
                    Assert.False(await db.KeyExistsAsync(Key(options, children[1].Nonce)));
                    Assert.True(await db.KeyExistsAsync(Key(options, aggregate.Nonce)));
                    Assert.False(await first.TryConsumeAsync(children[0],
                        TestContext.Current.CancellationToken));
                    Assert.True(await second.TryConsumeAsync(aggregate,
                        TestContext.Current.CancellationToken));
                }
            }
            finally
            {
                await db.KeyDeleteAsync(keys);
            }
        }
    }

    [Fact]
    public async Task LostSingleConsumeAcknowledgement_DoesNotPermitSecondCommit()
    {
        var connection = ConnectionString();
        var options = Options(connection);
        await using var inspector = await ConnectionMultiplexer.ConnectAsync(connection);
        await using var normal = new RedisDmlApprovalChallengeStore(options);
        var db = inspector.GetDatabase();
        var challenge = Challenge("consume-ack-lost");
        var key = Key(options, challenge.Nonce);
        try
        {
            await normal.RegisterAsync(challenge, TestContext.Current.CancellationToken);
            var proxy = new Mock<IDatabase>();
            proxy.Setup(x => x.ScriptEvaluateAsync(
                    It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                    It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .Returns(async (string script, RedisKey[] redisKeys,
                    RedisValue[] args, CommandFlags flags) =>
                {
                    var result = await db.ScriptEvaluateAsync(script, redisKeys, args, flags);
                    Assert.False(result.IsNull);
                    throw new IOException("Simulated lost Redis single-consume acknowledgement.");
                });

            await using (var uncertain = new RedisDmlApprovalChallengeStore(options, proxy.Object))
            {
                await Assert.ThrowsAsync<IOException>(async () =>
                    await uncertain.TryConsumeAsync(challenge,
                        TestContext.Current.CancellationToken));
            }

            Assert.False(await db.KeyExistsAsync(key));
            Assert.False(await normal.TryConsumeAsync(challenge,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await db.KeyDeleteAsync(key);
        }
    }

    [Fact]
    public async Task ExpiredRealRedisChild_CannotBePromotedOrReplayed()
    {
        var connection = ConnectionString();
        var options = Options(connection);
        await using var inspector = await ConnectionMultiplexer.ConnectAsync(connection);
        await using var store = new RedisDmlApprovalChallengeStore(options);
        var db = inspector.GetDatabase();
        var shortLived = Challenge("expiring", TimeSpan.FromMilliseconds(600));
        var aggregate = Challenge("aggregate");
        var keys = new[] { Key(options, shortLived.Nonce), Key(options, aggregate.Nonce) };

        try
        {
            await store.RegisterAsync(shortLived, TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
            Assert.False(await db.KeyExistsAsync(Key(options, shortLived.Nonce)));
            Assert.False(await store.TryPromoteAsync([shortLived], aggregate,
                TestContext.Current.CancellationToken));
            Assert.False(await db.KeyExistsAsync(Key(options, aggregate.Nonce)));
            Assert.False(await store.TryConsumeAsync(shortLived,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await db.KeyDeleteAsync(keys);
        }
    }

    [Fact]
    public async Task LostPromotionAcknowledgement_PreservesOneAggregateWithoutResurrectingChildren()
    {
        var connection = ConnectionString();
        var options = Options(connection);
        await using var inspector = await ConnectionMultiplexer.ConnectAsync(connection);
        await using var normal = new RedisDmlApprovalChallengeStore(options);
        var db = inspector.GetDatabase();
        var children = new[] { Challenge("one"), Challenge("two") };
        var aggregate = Challenge("approved-batch");
        var keys = children.Append(aggregate).Select(x => Key(options, x.Nonce)).ToArray();
        try
        {
            foreach (var child in children)
                await normal.RegisterAsync(child, TestContext.Current.CancellationToken);

            // The real Redis Lua script executes; only the response is lost.
            // This is a fault injection at the client transport boundary, not
            // a fake implementation of Redis's atomic key mutations.
            var proxy = new Mock<IDatabase>();
            proxy.Setup(x => x.ScriptEvaluateAsync(
                    It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                    It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .Returns(async (string script, RedisKey[] redisKeys,
                    RedisValue[] args, CommandFlags flags) =>
                {
                    var result = await db.ScriptEvaluateAsync(script, redisKeys, args, flags);
                    Assert.Equal("1", result.ToString());
                    throw new IOException("Simulated lost Redis promotion acknowledgement.");
                });

            await using (var uncertain = new RedisDmlApprovalChallengeStore(options, proxy.Object))
            {
                await Assert.ThrowsAsync<IOException>(async () =>
                    await uncertain.TryPromoteAsync(children, aggregate,
                        TestContext.Current.CancellationToken));
            }

            Assert.False(await db.KeyExistsAsync(Key(options, children[0].Nonce)));
            Assert.False(await db.KeyExistsAsync(Key(options, children[1].Nonce)));
            Assert.True(await db.KeyExistsAsync(Key(options, aggregate.Nonce)));
            Assert.False(await normal.TryPromoteAsync(children, aggregate,
                TestContext.Current.CancellationToken));
            Assert.True(await normal.TryConsumeAsync(aggregate,
                TestContext.Current.CancellationToken));
            Assert.False(await normal.TryConsumeAsync(aggregate,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await db.KeyDeleteAsync(keys);
        }
    }
}
