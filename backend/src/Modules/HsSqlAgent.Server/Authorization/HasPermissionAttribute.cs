using Auth.Service.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace HsSqlAgent.Server.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class HasPermissionAttribute : AuthorizeAttribute
{
    private const string Prefix = "__perm__";

    public HasPermissionAttribute(string path, string? action = null)
    {
        PermissionCanonicalPaths.RequireCanonical(path, nameof(path));
        if (action is not null)
            PermissionCanonicalPaths.RequirePermissionKey($"{path}.{action}", nameof(action));

        Policy = action is not null
            ? $"{Prefix}{path}.{action}"
            : $"{Prefix}{path}";
    }
}
