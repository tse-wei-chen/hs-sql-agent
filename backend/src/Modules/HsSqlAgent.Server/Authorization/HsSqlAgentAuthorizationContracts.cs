namespace HsSqlAgent.Server.Authorization;

/// <summary>
/// Authentication scheme names owned by HsSqlAgent built-in identity. They are deliberately namespaced
/// so installing HsSqlAgent never changes or collides with the host application's default schemes.
/// </summary>
public static class HsSqlAgentAuthenticationSchemes
{
    public const string Bearer = "HsSqlAgent.Jwt";
    public const string ExternalCookie = "HsSqlAgent.ExternalCookie";
    public const string Oidc = "HsSqlAgent.Oidc";
}

/// <summary>
/// Named policies used by HsSqlAgent built-in identity endpoints.
/// </summary>
public static class HsSqlAgentAuthorizationPolicies
{
    public const string Access = "HsSqlAgent.Access";
    public const string RefreshToken = "HsSqlAgent.RefreshToken";
    public const string MfaChallenge = "HsSqlAgent.MfaChallenge";
    public const string ExternalLogin = "HsSqlAgent.ExternalLogin";
}
