using System.Text.Json;
using HsSqlAgent.Server.Services;
using Moq;
using SqlAgent.Service.Core.Execution;
using StackExchange.Redis;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class RedisDmlApprovalChallengeStoreTests
{
    [Fact]
    public async Task RegisterAsync_ShouldUseNxAndChallengeExpiry()
    {
        var now = new DateTimeOffset(2026, 8, 23, 7, 0, 0, TimeSpan.Zero);
        var challenge = CreateChallenge(now, "nonce-register");
        RedisKey capturedKey = default;
        RedisValue capturedValue = default;
        TimeSpan? capturedExpiry = null;
        When capturedWhen = default;
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, TimeSpan?, When, CommandFlags>((key, value, expiry, when, _) =>
            {
                capturedKey = key;
                capturedValue = value;
                capturedExpiry = expiry;
                capturedWhen = when;
            })
            .ReturnsAsync(true);
        var store = CreateStore(database.Object, now);

        await store.RegisterAsync(challenge, TestContext.Current.CancellationToken);

        Assert.Equal("test:dml-approval:nonce-register", capturedKey.ToString());
        Assert.Equal(TimeSpan.FromMinutes(5), capturedExpiry);
        Assert.Equal(When.NotExists, capturedWhen);
        Assert.Equal(challenge, JsonSerializer.Deserialize<DmlApprovalChallenge>(capturedValue.ToString()));
    }

    [Fact]
    public async Task RegisterAsync_ShouldRejectDuplicateNonce()
    {
        var now = DateTimeOffset.UtcNow;
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(false);
        var store = CreateStore(database.Object, now);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.RegisterAsync(CreateChallenge(now, "duplicate"), TestContext.Current.CancellationToken));

        Assert.Contains("Duplicate", exception.Message);
    }

    [Fact]
    public async Task TryConsumeAsync_ShouldAtomicallyGetAndDeleteExactlyOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var challenge = CreateChallenge(now, "consume-once");
        var payload = JsonSerializer.Serialize(challenge);
        var scripts = new List<string>();
        var keys = new List<RedisKey[]>();
        var callCount = 0;
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((script, redisKeys, _, _) =>
            {
                scripts.Add(script);
                keys.Add(redisKeys);
            })
            .ReturnsAsync(() => callCount++ == 0
                ? RedisResult.Create((RedisValue)payload)
                : RedisResult.Create(RedisValue.Null));
        var store = CreateStore(database.Object, now);

        Assert.True(await store.TryConsumeAsync(challenge, TestContext.Current.CancellationToken));
        Assert.False(await store.TryConsumeAsync(challenge, TestContext.Current.CancellationToken));

        Assert.Equal(2, scripts.Count);
        Assert.All(scripts, script =>
        {
            Assert.Contains("redis.call('GET'", script);
            Assert.Contains("redis.call('DEL'", script);
        });
        Assert.All(keys, item => Assert.Equal("test:dml-approval:consume-once", Assert.Single(item).ToString()));
    }

    [Fact]
    public async Task TryConsumeAsync_ModifiedChallenge_ShouldBurnNonceAndFailClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var registered = CreateChallenge(now, "modified");
        var database = new Mock<IDatabase>();
        database
            .Setup(x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)JsonSerializer.Serialize(registered)));
        var store = CreateStore(database.Object, now);

        var accepted = await store.TryConsumeAsync(
            registered with { AffectedRows = registered.AffectedRows + 1 },
            TestContext.Current.CancellationToken);

        Assert.False(accepted);
        database.Verify(x => x.ScriptEvaluateAsync(
            It.Is<string>(script => script.Contains("redis.call('DEL'")),
            It.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0].ToString() == "test:dml-approval:modified"),
            It.IsAny<RedisValue[]>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    private static RedisDmlApprovalChallengeStore CreateStore(IDatabase database, DateTimeOffset now) =>
        new(
            new RedisDmlApprovalChallengeOptions("localhost:6379", "test:dml-approval:"),
            database,
            new FixedTimeProvider(now));

    private static DmlApprovalChallenge CreateChallenge(DateTimeOffset now, string nonce) =>
        new(
            "plan",
            "rows",
            2,
            "policy-v1",
            now,
            now.AddMinutes(5),
            nonce);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
