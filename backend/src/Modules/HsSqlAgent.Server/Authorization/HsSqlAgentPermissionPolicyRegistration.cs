using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace HsSqlAgent.Server.Authorization;

internal static class HsSqlAgentPermissionPolicyRegistration
{
    public static AuthorizationBuilder AddHsSqlAgentPermissionPolicies(this AuthorizationBuilder authorization)
    {
        foreach (var policyName in DiscoverPermissionPolicyNames())
        {
            if (policyName.StartsWith(HasAnyPermissionAttribute.Prefix, StringComparison.Ordinal))
            {
                var permissions = policyName[HasAnyPermissionAttribute.Prefix.Length..]
                    .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                authorization.AddPolicy(policyName, policy => ConfigurePermissionPolicy(policy, new PermissionRequirement(permissions)));
                continue;
            }

            if (!policyName.StartsWith(HasPermissionAttribute.Prefix, StringComparison.Ordinal))
                continue;

            var permission = policyName[HasPermissionAttribute.Prefix.Length..];
            var dot = permission.LastIndexOf('.');
            if (dot <= 0 || dot == permission.Length - 1)
                throw new InvalidOperationException($"Invalid HsSqlAgent permission policy '{policyName}'.");

            authorization.AddPolicy(policyName, policy => ConfigurePermissionPolicy(
                policy,
                new PermissionRequirement(permission[..dot], permission[(dot + 1)..])));
        }

        return authorization;
    }

    private static void ConfigurePermissionPolicy(AuthorizationPolicyBuilder policy, PermissionRequirement requirement)
    {
        policy.AddAuthenticationSchemes(HsSqlAgentAuthenticationSchemes.Bearer);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("typ", "access");
        policy.AddRequirements(requirement);
    }

    private static IReadOnlyCollection<string> DiscoverPermissionPolicyNames()
    {
        var assembly = typeof(HsSqlAgentPermissionPolicyRegistration).Assembly;
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in assembly.GetTypes().Where(type =>
                     type.Namespace?.StartsWith("HsSqlAgent.Server.Controllers", StringComparison.Ordinal) == true))
        {
            AddPolicies(type.GetCustomAttributes<AuthorizeAttribute>(inherit: true), names);
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                AddPolicies(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true), names);
        }

        return names;
    }

    private static void AddPolicies(IEnumerable<AuthorizeAttribute> attributes, HashSet<string> names)
    {
        foreach (var attribute in attributes)
        {
            var policy = attribute.Policy;
            if (policy is not null &&
                (policy.StartsWith(HasPermissionAttribute.Prefix, StringComparison.Ordinal) ||
                 policy.StartsWith(HasAnyPermissionAttribute.Prefix, StringComparison.Ordinal)))
            {
                names.Add(policy);
            }
        }
    }
}
