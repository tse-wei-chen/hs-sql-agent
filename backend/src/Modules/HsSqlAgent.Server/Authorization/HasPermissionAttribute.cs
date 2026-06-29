using Microsoft.AspNetCore.Authorization;

namespace HsSqlAgent.Server.Authorization;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class HasPermissionAttribute : AuthorizeAttribute
{
    private const string Prefix = "__perm__";

    public HasPermissionAttribute(string path, string? action = null)
    {
        Policy = action is not null
            ? $"{Prefix}{path}.{action}"
            : $"{Prefix}{path}";
    }
}
