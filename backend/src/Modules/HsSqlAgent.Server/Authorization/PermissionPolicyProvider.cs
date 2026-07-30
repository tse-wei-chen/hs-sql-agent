using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Authorization;

public class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : IAuthorizationPolicyProvider
{
    private const string Prefix = "__perm__";
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(HasAnyPermissionAttribute.Prefix))
        {
            var permissions = policyName[HasAnyPermissionAttribute.Prefix.Length..]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (permissions.Length == 0)
                return _fallback.GetPolicyAsync(policyName);

            var anyPermissionPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim("typ", "access")
                .AddRequirements(new PermissionRequirement(permissions))
                .Build();

            return Task.FromResult<AuthorizationPolicy?>(anyPermissionPolicy);
        }

        if (!policyName.StartsWith(Prefix))
            return _fallback.GetPolicyAsync(policyName);

        var value = policyName[Prefix.Length..];
        var lastDot = value.LastIndexOf('.');
        if (lastDot <= 0)
            return _fallback.GetPolicyAsync(policyName);

        var path = value[..lastDot];
        var action = value[(lastDot + 1)..];

        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim("typ", "access")
            .AddRequirements(new PermissionRequirement(path, action))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        => _fallback.GetFallbackPolicyAsync();
}
