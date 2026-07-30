using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Services;
using Moq;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public class ResourceLimitTests
{
    [Fact]
    public void LayeredRateLimiter_ShouldKeepIpAndKeyBudgetsIndependent()
    {
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            IpPermitLimit = 2,
            IpWindowSeconds = 60,
            KeyPermitLimit = 1,
            KeyWindowSeconds = 60
        });
        var limiter = new LayeredRateLimitService(state.Object);

        Assert.True(limiter.TryAcquireIp("192.0.2.1", out _));
        Assert.True(limiter.TryAcquireIp("192.0.2.1", out _));
        Assert.False(limiter.TryAcquireIp("192.0.2.1", out var ipRetry));
        Assert.True(ipRetry > TimeSpan.Zero);
        Assert.True(limiter.TryAcquireIp("192.0.2.2", out _));

        Assert.True(limiter.TryAcquireKey(10, McpKeyRateLimitMode.Inherit, null, null, out _));
        Assert.False(limiter.TryAcquireKey(10, McpKeyRateLimitMode.Inherit, null, null, out var keyRetry));
        Assert.True(keyRetry > TimeSpan.Zero);
        Assert.True(limiter.TryAcquireKey(11, McpKeyRateLimitMode.Inherit, null, null, out _));
    }

    [Fact]
    public void LayeredRateLimiter_ShouldAllowCustomAndUnlimitedKeyPolicies()
    {
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            KeyPermitLimit = 1,
            KeyWindowSeconds = 60
        });
        var limiter = new LayeredRateLimitService(state.Object);

        Assert.True(limiter.TryAcquireKey(20, McpKeyRateLimitMode.Custom, 2, 60, out _));
        Assert.True(limiter.TryAcquireKey(20, McpKeyRateLimitMode.Custom, 2, 60, out _));
        Assert.False(limiter.TryAcquireKey(20, McpKeyRateLimitMode.Custom, 2, 60, out _));

        for (var i = 0; i < 100; i++)
            Assert.True(limiter.TryAcquireKey(21, McpKeyRateLimitMode.Unlimited, null, null, out _));
    }

    [Fact]
    public void LayeredRateLimiter_ShouldApplyChangedPolicyImmediately()
    {
        var policy = new SecurityPolicyModel
        {
            IpPermitLimit = 1,
            IpWindowSeconds = 60,
            KeyPermitLimit = 1,
            KeyWindowSeconds = 60
        };
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(() => policy.Clone());
        var limiter = new LayeredRateLimitService(state.Object);

        Assert.True(limiter.TryAcquireIp("192.0.2.1", out _));
        Assert.False(limiter.TryAcquireIp("192.0.2.1", out _));

        policy.IpPermitLimit = 2;
        Assert.True(limiter.TryAcquireIp("192.0.2.1", out _));
        Assert.True(limiter.TryAcquireIp("192.0.2.1", out _));
        Assert.False(limiter.TryAcquireIp("192.0.2.1", out _));
    }

    [Fact]
    public void SqlConcurrencyLimiter_ShouldReleaseLeaseAndHonorDynamicMaximum()
    {
        var policy = new SecurityPolicyModel { MaxConcurrentSql = 1 };
        var state = new Mock<ISecurityPolicyRuntimeState>();
        state.Setup(x => x.GetCurrent()).Returns(() => policy.Clone());
        var limiter = new SqlExecutionConcurrencyLimiter(state.Object);

        using var first = limiter.TryAcquire();
        Assert.NotNull(first);
        Assert.Equal(1, limiter.ActiveCount);
        Assert.Null(limiter.TryAcquire());

        policy.MaxConcurrentSql = 2;
        using var second = limiter.TryAcquire();
        Assert.NotNull(second);
        Assert.Equal(2, limiter.ActiveCount);

        second.Dispose();
        first.Dispose();
        Assert.Equal(0, limiter.ActiveCount);
    }
}
