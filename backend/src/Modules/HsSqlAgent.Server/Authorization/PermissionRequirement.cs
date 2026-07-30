using Microsoft.AspNetCore.Authorization;

namespace HsSqlAgent.Server.Authorization;

public class PermissionRequirement(string path, string action) : IAuthorizationRequirement
{
    public IReadOnlyCollection<string> Permissions { get; } = [$"{path}.{action}"];

    public PermissionRequirement(IEnumerable<string> permissions)
        : this(string.Empty, string.Empty)
    {
        Permissions = permissions.ToArray();
    }
}
