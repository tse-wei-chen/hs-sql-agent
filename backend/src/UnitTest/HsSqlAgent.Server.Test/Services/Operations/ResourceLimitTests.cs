using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class ResourceLimitTests
{
    [Fact]
    public async Task LayeredRateLimiter_ShouldKeepKeyBudgetsIndependent()
    {
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            KeyPermitLimit = 1,
            KeyWindowSeconds = 60
        });
        var limiter = CreateLayeredLimiter(state.Object);

        Assert.True((await limiter.AcquireKeyAsync(10, McpKeyRateLimitMode.Inherit, null, null, TestContext.Current.CancellationToken)).IsAllowed);
        var rejected = await limiter.AcquireKeyAsync(10, McpKeyRateLimitMode.Inherit, null, null, TestContext.Current.CancellationToken);
        Assert.False(rejected.IsAllowed);
        Assert.True(rejected.RetryAfter > TimeSpan.Zero);
        Assert.True((await limiter.AcquireKeyAsync(11, McpKeyRateLimitMode.Inherit, null, null, TestContext.Current.CancellationToken)).IsAllowed);
    }

    [Fact]
    public async Task LayeredRateLimiter_ShouldAllowCustomAndUnlimitedKeyPolicies()
    {
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            KeyPermitLimit = 1,
            KeyWindowSeconds = 60
        });
        var limiter = CreateLayeredLimiter(state.Object);

        Assert.True((await limiter.AcquireKeyAsync(20, McpKeyRateLimitMode.Custom, 2, 60, TestContext.Current.CancellationToken)).IsAllowed);
        Assert.True((await limiter.AcquireKeyAsync(20, McpKeyRateLimitMode.Custom, 2, 60, TestContext.Current.CancellationToken)).IsAllowed);
        Assert.False((await limiter.AcquireKeyAsync(20, McpKeyRateLimitMode.Custom, 2, 60, TestContext.Current.CancellationToken)).IsAllowed);

        for (var i = 0; i < 100; i++)
            Assert.True((await limiter.AcquireKeyAsync(21, McpKeyRateLimitMode.Unlimited, null, null, TestContext.Current.CancellationToken)).IsAllowed);
    }

    [Fact]
    public async Task LayeredRateLimiter_ShouldApplyChangedPolicyImmediately()
    {
        var policy = new SecurityPolicyModel
        {
            KeyPermitLimit = 1,
            KeyWindowSeconds = 60
        };
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(() => policy.Clone());
        var limiter = CreateLayeredLimiter(state.Object);

        Assert.True((await limiter.AcquireKeyAsync(30, McpKeyRateLimitMode.Inherit, null, null, TestContext.Current.CancellationToken)).IsAllowed);
        Assert.False((await limiter.AcquireKeyAsync(30, McpKeyRateLimitMode.Inherit, null, null, TestContext.Current.CancellationToken)).IsAllowed);

        policy.KeyPermitLimit = 2;
        Assert.True((await limiter.AcquireKeyAsync(30, McpKeyRateLimitMode.Inherit, null, null, TestContext.Current.CancellationToken)).IsAllowed);
        Assert.True((await limiter.AcquireKeyAsync(30, McpKeyRateLimitMode.Inherit, null, null, TestContext.Current.CancellationToken)).IsAllowed);
        Assert.False((await limiter.AcquireKeyAsync(30, McpKeyRateLimitMode.Inherit, null, null, TestContext.Current.CancellationToken)).IsAllowed);
    }

    [Fact]
    public async Task RequestRateLimiter_ShouldKeepArbitraryPartitionsIndependent()
    {
        var limiter = new MemoryRequestRateLimiter(TimeProvider.System);
        var firstIp = new RateLimitRequest("ip:192.0.2.1", 1, TimeSpan.FromMinutes(1));
        var secondIp = firstIp with { Partition = "ip:192.0.2.2" };

        Assert.True((await limiter.AcquireAsync(firstIp, TestContext.Current.CancellationToken)).IsAllowed);
        Assert.False((await limiter.AcquireAsync(firstIp, TestContext.Current.CancellationToken)).IsAllowed);
        Assert.True((await limiter.AcquireAsync(secondIp, TestContext.Current.CancellationToken)).IsAllowed);
    }

    [Fact]
    public async Task SqlConcurrencyLimiter_ShouldReleaseLeaseAndHonorDynamicMaximum()
    {
        var policy = new SecurityPolicyModel { MaxConcurrentSql = 1 };
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(() => policy.Clone());
        var limiter = new SqlExecutionConcurrencyLimiter(state.Object);

        var cancellationToken = TestContext.Current.CancellationToken;
        await using var first = await limiter.TryAcquireAsync(cancellationToken);
        Assert.NotNull(first);
        Assert.Equal(1, limiter.ActiveCount);
        Assert.Null(await limiter.TryAcquireAsync(cancellationToken));

        policy.MaxConcurrentSql = 2;
        await using var second = await limiter.TryAcquireAsync(cancellationToken);
        Assert.NotNull(second);
        Assert.Equal(2, limiter.ActiveCount);

        await second.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(0, limiter.ActiveCount);
    }

    private static LayeredRateLimitService CreateLayeredLimiter(ISecurityPolicyRuntimeState state) =>
        new(state, new MemoryRequestRateLimiter(TimeProvider.System));
}
