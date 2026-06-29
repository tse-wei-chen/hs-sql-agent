using Microsoft.AspNetCore.Authorization;

namespace HsSqlAgent.Server.Authorization;

public class PermissionRequirement(string path, string action) : IAuthorizationRequirement
{
    public string Path { get; } = path;
    public string Action { get; } = action;
}
