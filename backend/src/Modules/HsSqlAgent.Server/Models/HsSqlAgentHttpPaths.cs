namespace HsSqlAgent.Server.Models;

/// <summary>
/// Stable HTTP surfaces exposed by the current HsSqlAgent.Server embedding contract.
/// Authorization canonical paths are a separate concept and must not be derived from these routes.
/// </summary>
public static class HsSqlAgentHttpPaths
{
    public const string AdminUi = "/";
    public const string AdminApi = "/api";
    public const string Mcp = "/mcp";
    internal const string OidcSignInCallback = AdminApi + "/auth/oidc/signin";
}
