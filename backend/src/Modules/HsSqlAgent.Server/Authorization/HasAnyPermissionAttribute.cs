using Auth.Service.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace HsSqlAgent.Server.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HasAnyPermissionAttribute : AuthorizeAttribute
{
    internal const string Prefix = "__perm_any__";

    public HasAnyPermissionAttribute(params string[] permissions)
    {
        if (permissions.Length == 0 || permissions.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one permission is required.", nameof(permissions));

        foreach (var permission in permissions)
            PermissionCanonicalPaths.RequirePermissionKey(permission, nameof(permissions));

        Policy = Prefix + string.Join('|', permissions);
    }
}
