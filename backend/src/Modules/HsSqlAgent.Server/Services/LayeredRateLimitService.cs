using Admin.Service.Interfaces;
using Admin.Service.Models;

namespace HsSqlAgent.Server.Services;

public interface ILayeredRateLimitService
{
    ValueTask<RateLimitResult> AcquireKeyAsync(
        int keyId,
        McpKeyRateLimitMode mode,
        int? permitLimitOverride,
        int? windowSecondsOverride,
        CancellationToken cancellationToken = default);
}

public sealed class LayeredRateLimitService(
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    IRequestRateLimiter requestRateLimiter) : ILayeredRateLimitService
{
    private readonly ISecurityPolicyRuntimeState _securityPolicyRuntimeState = securityPolicyRuntimeState;
    private readonly IRequestRateLimiter _requestRateLimiter = requestRateLimiter;

    public ValueTask<RateLimitResult> AcquireKeyAsync(
        int keyId,
        McpKeyRateLimitMode mode,
        int? permitLimitOverride,
        int? windowSecondsOverride,
        CancellationToken cancellationToken = default)
    {
        if (mode == McpKeyRateLimitMode.Unlimited)
            return ValueTask.FromResult(RateLimitResult.Allowed);

        var policy = _securityPolicyRuntimeState.GetCurrent();
        var permitLimit = mode == McpKeyRateLimitMode.Custom
            ? permitLimitOverride ?? policy.KeyPermitLimit
            : policy.KeyPermitLimit;
        var windowSeconds = mode == McpKeyRateLimitMode.Custom
            ? windowSecondsOverride ?? policy.KeyWindowSeconds
            : policy.KeyWindowSeconds;

        return _requestRateLimiter.AcquireAsync(
            new RateLimitRequest(
                $"mcp-key:{keyId}",
                permitLimit,
                TimeSpan.FromSeconds(windowSeconds)),
            cancellationToken);
    }
}
