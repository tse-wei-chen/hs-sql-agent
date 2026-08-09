namespace HsSqlAgent.Server.Models;

/// <summary>
/// for AddHsSqlAgent service registration and validation, used in Program.cs
/// </summary>
public class HsSqlAgentServiceOptions
{
    public McpOptions Mcp { get; } = new();
    public EnterpriseIdentityOptions EnterpriseIdentity { get; } = new();
    public OperabilityOptions Operability { get; } = new();
    public TelemetryOptions Telemetry { get; } = new();
    public string AdminDatabaseProvider { get; set; } = "Sqlite";
    public string AdminConnectionString { get; set; } = "Data Source=hsagent.db";
    public string HmacSecretKey { get; set; } = string.Empty;
    public string JwtSecretKey { get; set; } = string.Empty;
    public string JwtIssuer { get; set; } = "HS-Agent";
    public string JwtAudience { get; set; } = "HS-Agent-Users";
    public int JwtAccessTokenExpirationMinutes { get; set; } = 1;
    public int JwtRefreshTokenExpirationDays { get; set; } = 30;
    public int SignInLockoutThreshold { get; set; } = 5;
    public int SignInLockoutMinutes { get; set; } = 15;
    public string PasswordResetBaseUrl { get; set; } = "http://localhost:3000/reset-password";
    public int PasswordResetExpirationMinutes { get; set; } = 30;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpEnableSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string SmtpFrom { get; set; } = string.Empty;

    public int RateLimitPermitLimit { get; set; }
    public int RateLimitWindowSeconds { get; set; }
    public string RateLimiterProvider { get; set; } = "Memory";
    public string RateLimiterConnectionString { get; set; } = string.Empty;
    public string RateLimiterFailureMode { get; set; } = "FailClosed";
    public string RateLimiterKeyPrefix { get; set; } = "hsqlagent:ratelimit:";
    public string SecurityPolicySyncProvider { get; set; } = "Memory";
    public string SecurityPolicySyncConnectionString { get; set; } = string.Empty;
    public string SecurityPolicySyncKeyPrefix { get; set; } = "hsqlagent:security-policy:";
    public int SecurityPolicySyncRefreshIntervalSeconds { get; set; } = 30;
    public string SqlConcurrencyProvider { get; set; } = "Memory";
    public string SqlConcurrencyConnectionString { get; set; } = string.Empty;
    public string SqlConcurrencyFailureMode { get; set; } = "FailClosed";
    public string SqlConcurrencyKey { get; set; } = "hsqlagent:sql-concurrency";
    public int SqlConcurrencyLeaseSeconds { get; set; } = 30;

    public string CacheProvider { get; set; } = "Memory";
    public string CacheConnectionString { get; set; } = string.Empty;
    public string CacheKeyPrefix { get; set; } = "hsqlagent:cache:";
}

public class McpOptions
{
    public string PublicEndpoint { get; set; } = "http://localhost:8080/mcp";
}

public class TelemetryOptions
{
    public bool PrometheusEnabled { get; set; } = true;
    public string PrometheusHost { get; set; } = "localhost";
    public int PrometheusPort { get; set; } = 9000;
    public string OtlpEndpoint { get; set; } = string.Empty;
    public string ServiceName { get; set; } = "hs-sql-agent";
}

public class OperabilityOptions
{
    public bool HealthProbeEnabled { get; set; } = true;
    public int HealthProbeIntervalSeconds { get; set; } = 60;
    public int HealthProbeTimeoutSeconds { get; set; } = 10;
    public int SlowQueryThresholdMs { get; set; } = 1000;
    public string AlertWebhookUrl { get; set; } = string.Empty;
    public string AlertWebhookSecret { get; set; } = string.Empty;
    public string SiemWebhookUrl { get; set; } = string.Empty;
    public string SiemWebhookSecret { get; set; } = string.Empty;
    public int DeliveryMaxAttempts { get; set; } = 6;
    public int AuditRetentionDays { get; set; }
    public string AuditRetentionMode { get; set; } = "Purge";
    public string AuditArchivePath { get; set; } = "data/audit-archive";
    public string AuditFallbackPath { get; set; } = "data/audit-fallback.jsonl";
    public int AuditRetentionRunHourUtc { get; set; } = 2;
}

public class EnterpriseIdentityOptions
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
    public string DataProtectionKeyPath { get; set; } = string.Empty;
}

/// <summary>
/// for HsSqlAgentBuilder and pipeline configuration, used in UseHsSqlAgent and MapAdminEndpoint
/// </summary>
public class HsSqlAgentPipelineOptions
{
    public string McpEndpoint { get; set; } = "/mcp";
    public string AdminApiPrefix { get; set; } = "/api";
    public string AdminUiRequestPath { get; set; } = "/";
    public string AdminUiRootPath { get; set; } = "wwwroot";
    public bool ServeAdminUi { get; set; }
}
