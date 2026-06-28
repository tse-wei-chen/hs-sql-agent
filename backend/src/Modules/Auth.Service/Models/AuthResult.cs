namespace Auth.Service.Models;

public class AuthResult
{
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public IReadOnlyCollection<string> Roles { get; set; } = [];
    public IReadOnlyCollection<PermissionGrant> Permissions { get; set; } = [];
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
}

public class PermissionGrant
{
    public int PermissionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public IReadOnlyCollection<ActionGrant> Actions { get; set; } = [];
}

public class ActionGrant
{
    public int ActionId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
