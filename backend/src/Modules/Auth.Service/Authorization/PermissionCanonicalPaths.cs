namespace Auth.Service.Authorization;

/// <summary>
/// Stable authorization resource identifiers shared by persistence and server authorization.
/// These are canonical permission paths, not HTTP route or frontend mount paths.
/// </summary>
public static class PermissionCanonicalPaths
{
    public const string Overview = "/home";
    public const string McpKeys = "/runtime/mcp-keys";
    public const string CustomTools = "/runtime/custom-tools";
    public const string DbManagement = "/runtime/db-management";
    public const string Audit = "/runtime/audit";
    public const string Roles = "/auth/role";
    public const string Users = "/auth/user";
    public const string DbSemantic = "/runtime/db-management/semantic";
    public const string Security = "/runtime/security";
    public const string Operability = "/runtime/operability";

    public static IReadOnlyList<string> All { get; } =
    [
        Overview,
        McpKeys,
        CustomTools,
        DbManagement,
        Audit,
        Roles,
        Users,
        DbSemantic,
        Security,
        Operability
    ];

    public static string RequireCanonical(string path, string? parameterName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var canonical = Normalize(path);
        if (!string.Equals(path, canonical, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Permission path '{path}' is not canonical. Use '{canonical}'.",
                parameterName);
        }

        return path;
    }

    public static string RequirePermissionKey(string permission, string? parameterName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission, parameterName);
        var lastDot = permission.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == permission.Length - 1)
            throw new ArgumentException("Permission must use the canonical '<path>.<action>' format.", parameterName);

        var path = permission[..lastDot];
        var action = permission[(lastDot + 1)..];
        RequireCanonical(path, parameterName);
        if (!string.Equals(action, action.Trim().ToLowerInvariant(), StringComparison.Ordinal) || action.Contains('/'))
            throw new ArgumentException($"Permission action '{action}' must be a lowercase action code.", parameterName);

        return permission;
    }

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Trim().Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);
        if (value.Length > 1) value = value.TrimEnd('/');
        return value.ToLowerInvariant();
    }
}
