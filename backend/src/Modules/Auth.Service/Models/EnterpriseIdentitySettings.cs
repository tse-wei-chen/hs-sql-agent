namespace Auth.Service.Models;

public class EnterpriseIdentitySettings
{
    public bool OidcEnabled { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = true;
    public string EmailClaim { get; set; } = "email";
    public string NameClaim { get; set; } = "name";
    public string RoleClaim { get; set; } = "roles";
    public string EmailVerifiedClaim { get; set; } = "email_verified";
    public bool RequireVerifiedEmail { get; set; } = true;
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];
    public Dictionary<string, string> RoleMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DefaultRoleNames { get; set; } = [];
    public bool AutoProvision { get; set; } = true;
    public string FrontendCallbackUrl { get; set; } = "/sso-callback";
    public int LoginCodeExpirationMinutes { get; set; } = 2;
    public List<string> RequireMfaForRoles { get; set; } = [];
    public string TotpIssuer { get; set; } = "HS SQL Agent";
}
