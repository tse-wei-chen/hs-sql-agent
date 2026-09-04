namespace HsSqlAgent.Server.Models;

/// <summary>
/// Legacy aggregate options kept for source compatibility with AddHsSqlAgent(...).
/// New integrations should configure the capability-specific option types instead.
/// </summary>
public class HsSqlAgentServiceOptions
{
    public McpOptions Mcp { get; } = new();
    public BootstrapOptions Bootstrap { get; } = new();
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
    public string OutboundDeliverySyncProvider { get; set; } = "Memory";
    public string OutboundDeliverySyncConnectionString { get; set; } = string.Empty;
    public string OutboundDeliverySyncKeyPrefix { get; set; } = "hsqlagent:outbound-delivery:";
    public string SqlConcurrencyProvider { get; set; } = "Memory";
    public string SqlConcurrencyConnectionString { get; set; } = string.Empty;
    public string SqlConcurrencyFailureMode { get; set; } = "FailClosed";
    public string SqlConcurrencyKey { get; set; } = "hsqlagent:sql-concurrency";
    public int SqlConcurrencyLeaseSeconds { get; set; } = 30;
    public string DmlApprovalStoreProvider { get; set; } = "Memory";
    public string DmlApprovalStoreConnectionString { get; set; } = string.Empty;
    public string DmlApprovalStoreKeyPrefix { get; set; } = "hsqlagent:dml-approval:";

    public string CacheProvider { get; set; } = "Memory";
    public string CacheConnectionString { get; set; } = string.Empty;
    public string CacheKeyPrefix { get; set; } = "hsqlagent:cache:";
}

public sealed class HsSqlAgentAdminStoreOptions
{
    public string Provider { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=hsagent.db";

    internal static HsSqlAgentAdminStoreOptions FromLegacy(HsSqlAgentServiceOptions legacy) => new()
    {
        Provider = legacy.AdminDatabaseProvider,
        ConnectionString = legacy.AdminConnectionString
    };
}

public sealed class HsSqlAgentRuntimeOptions
{
    public BootstrapOptions Bootstrap { get; } = new();
    public OperabilityOptions Operability { get; } = new();
    public CacheOptions Cache { get; } = new();
    public RateLimiterOptions RateLimiter { get; } = new();
    public SecurityPolicySyncOptions SecurityPolicySync { get; } = new();
    public OutboundDeliverySyncOptions OutboundDeliverySync { get; } = new();
    public SqlConcurrencyOptions SqlConcurrency { get; } = new();
    public DmlApprovalStoreOptions DmlApprovalStore { get; } = new();

    internal static HsSqlAgentRuntimeOptions FromLegacy(HsSqlAgentServiceOptions legacy)
    {
        var options = new HsSqlAgentRuntimeOptions();
        options.Bootstrap.Enabled = legacy.Bootstrap.Enabled;
        options.Bootstrap.Databases = legacy.Bootstrap.Databases;
        CopyOperability(legacy.Operability, options.Operability);
        options.Cache.Provider = legacy.CacheProvider;
        options.Cache.ConnectionString = legacy.CacheConnectionString;
        options.Cache.KeyPrefix = legacy.CacheKeyPrefix;
        options.RateLimiter.PermitLimit = legacy.RateLimitPermitLimit;
        options.RateLimiter.WindowSeconds = legacy.RateLimitWindowSeconds;
        options.RateLimiter.Provider = legacy.RateLimiterProvider;
        options.RateLimiter.ConnectionString = legacy.RateLimiterConnectionString;
        options.RateLimiter.FailureMode = legacy.RateLimiterFailureMode;
        options.RateLimiter.KeyPrefix = legacy.RateLimiterKeyPrefix;
        options.SecurityPolicySync.Provider = legacy.SecurityPolicySyncProvider;
        options.SecurityPolicySync.ConnectionString = legacy.SecurityPolicySyncConnectionString;
        options.SecurityPolicySync.KeyPrefix = legacy.SecurityPolicySyncKeyPrefix;
        options.SecurityPolicySync.RefreshIntervalSeconds = legacy.SecurityPolicySyncRefreshIntervalSeconds;
        options.OutboundDeliverySync.Provider = legacy.OutboundDeliverySyncProvider;
        options.OutboundDeliverySync.ConnectionString = legacy.OutboundDeliverySyncConnectionString;
        options.OutboundDeliverySync.KeyPrefix = legacy.OutboundDeliverySyncKeyPrefix;
        options.SqlConcurrency.Provider = legacy.SqlConcurrencyProvider;
        options.SqlConcurrency.ConnectionString = legacy.SqlConcurrencyConnectionString;
        options.SqlConcurrency.FailureMode = legacy.SqlConcurrencyFailureMode;
        options.SqlConcurrency.Key = legacy.SqlConcurrencyKey;
        options.SqlConcurrency.LeaseSeconds = legacy.SqlConcurrencyLeaseSeconds;
        options.DmlApprovalStore.Provider = legacy.DmlApprovalStoreProvider;
        options.DmlApprovalStore.ConnectionString = legacy.DmlApprovalStoreConnectionString;
        options.DmlApprovalStore.KeyPrefix = legacy.DmlApprovalStoreKeyPrefix;
        return options;
    }

    private static void CopyOperability(OperabilityOptions source, OperabilityOptions target)
    {
        target.HealthProbeEnabled = source.HealthProbeEnabled;
        target.HealthProbeIntervalSeconds = source.HealthProbeIntervalSeconds;
        target.HealthProbeTimeoutSeconds = source.HealthProbeTimeoutSeconds;
        target.HealthProbeMaxConcurrency = source.HealthProbeMaxConcurrency;
        target.SlowQueryThresholdMs = source.SlowQueryThresholdMs;
        target.AlertWebhookUrl = source.AlertWebhookUrl;
        target.AlertWebhookSecret = source.AlertWebhookSecret;
        target.SiemWebhookUrl = source.SiemWebhookUrl;
        target.SiemWebhookSecret = source.SiemWebhookSecret;
        target.DeliveryMaxAttempts = source.DeliveryMaxAttempts;
        target.DeliveryMaxConcurrency = source.DeliveryMaxConcurrency;
        target.AuditRetentionDays = source.AuditRetentionDays;
        target.AuditRetentionMode = source.AuditRetentionMode;
        target.AuditArchivePath = source.AuditArchivePath;
        target.AuditFallbackPath = source.AuditFallbackPath;
        target.AuditRetentionRunHourUtc = source.AuditRetentionRunHourUtc;
    }
}

public sealed class HsSqlAgentBuiltInAuthOptions
{
    public HsSqlAgentJwtOptions Jwt { get; } = new();
    public HsSqlAgentPasswordResetOptions PasswordReset { get; } = new();
    public EnterpriseIdentityOptions EnterpriseIdentity { get; } = new();

    internal static HsSqlAgentBuiltInAuthOptions FromLegacy(HsSqlAgentServiceOptions legacy)
    {
        var options = new HsSqlAgentBuiltInAuthOptions();
        options.Jwt.SecretKey = legacy.JwtSecretKey;
        options.Jwt.Issuer = legacy.JwtIssuer;
        options.Jwt.Audience = legacy.JwtAudience;
        options.Jwt.AccessTokenExpirationMinutes = legacy.JwtAccessTokenExpirationMinutes;
        options.Jwt.RefreshTokenExpirationDays = legacy.JwtRefreshTokenExpirationDays;
        options.Jwt.SignInLockoutThreshold = legacy.SignInLockoutThreshold;
        options.Jwt.SignInLockoutMinutes = legacy.SignInLockoutMinutes;
        options.PasswordReset.BaseUrl = legacy.PasswordResetBaseUrl;
        options.PasswordReset.ExpirationMinutes = legacy.PasswordResetExpirationMinutes;
        options.PasswordReset.SmtpHost = legacy.SmtpHost;
        options.PasswordReset.SmtpPort = legacy.SmtpPort;
        options.PasswordReset.SmtpEnableSsl = legacy.SmtpEnableSsl;
        options.PasswordReset.SmtpUsername = legacy.SmtpUsername;
        options.PasswordReset.SmtpPassword = legacy.SmtpPassword;
        options.PasswordReset.SmtpFrom = legacy.SmtpFrom;
        CopyEnterpriseIdentity(legacy.EnterpriseIdentity, options.EnterpriseIdentity);
        return options;
    }

    private static void CopyEnterpriseIdentity(EnterpriseIdentityOptions source, EnterpriseIdentityOptions target)
    {
        target.OidcEnabled = source.OidcEnabled;
        target.Authority = source.Authority;
        target.ClientId = source.ClientId;
        target.ClientSecret = source.ClientSecret;
        target.RequireHttpsMetadata = source.RequireHttpsMetadata;
        target.EmailClaim = source.EmailClaim;
        target.NameClaim = source.NameClaim;
        target.RoleClaim = source.RoleClaim;
        target.EmailVerifiedClaim = source.EmailVerifiedClaim;
        target.RequireVerifiedEmail = source.RequireVerifiedEmail;
        target.Scopes = [.. source.Scopes];
        target.RoleMappings = new(source.RoleMappings, StringComparer.OrdinalIgnoreCase);
        target.DefaultRoleNames = [.. source.DefaultRoleNames];
        target.AutoProvision = source.AutoProvision;
        target.FrontendCallbackUrl = source.FrontendCallbackUrl;
        target.LoginCodeExpirationMinutes = source.LoginCodeExpirationMinutes;
        target.RequireMfaForRoles = [.. source.RequireMfaForRoles];
        target.TotpIssuer = source.TotpIssuer;
        target.DataProtectionKeyPath = source.DataProtectionKeyPath;
    }
}

public sealed class HsSqlAgentJwtOptions
{
    public string SecretKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "HS-Agent";
    public string Audience { get; set; } = "HS-Agent-Users";
    public int AccessTokenExpirationMinutes { get; set; } = 1;
    public int RefreshTokenExpirationDays { get; set; } = 30;
    public int SignInLockoutThreshold { get; set; } = 5;
    public int SignInLockoutMinutes { get; set; } = 15;
}

public sealed class HsSqlAgentPasswordResetOptions
{
    public string BaseUrl { get; set; } = "http://localhost:3000/reset-password";
    public int ExpirationMinutes { get; set; } = 30;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpEnableSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string SmtpFrom { get; set; } = string.Empty;
}

public class McpOptions
{
    public string PublicEndpoint { get; set; } = "http://localhost:8080/mcp";
    public string HmacSecretKey { get; set; } = string.Empty;

    internal static McpOptions FromLegacy(HsSqlAgentServiceOptions legacy) => new()
    {
        PublicEndpoint = legacy.Mcp.PublicEndpoint,
        HmacSecretKey = legacy.HmacSecretKey
    };
}

public sealed class CacheOptions
{
    public string Provider { get; set; } = "Memory";
    public string ConnectionString { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = "hsqlagent:cache:";
}

public sealed class RateLimiterOptions
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }
    public string Provider { get; set; } = "Memory";
    public string ConnectionString { get; set; } = string.Empty;
    public string FailureMode { get; set; } = "FailClosed";
    public string KeyPrefix { get; set; } = "hsqlagent:ratelimit:";
}

public sealed class SecurityPolicySyncOptions
{
    public string Provider { get; set; } = "Memory";
    public string ConnectionString { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = "hsqlagent:security-policy:";
    public int RefreshIntervalSeconds { get; set; } = 30;
}

public sealed class OutboundDeliverySyncOptions
{
    public string Provider { get; set; } = "Memory";
    public string ConnectionString { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = "hsqlagent:outbound-delivery:";
}

public sealed class SqlConcurrencyOptions
{
    public string Provider { get; set; } = "Memory";
    public string ConnectionString { get; set; } = string.Empty;
    public string FailureMode { get; set; } = "FailClosed";
    public string Key { get; set; } = "hsqlagent:sql-concurrency";
    public int LeaseSeconds { get; set; } = 30;
}

public sealed class DmlApprovalStoreOptions
{
    public string Provider { get; set; } = "Memory";
    public string ConnectionString { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = "hsqlagent:dml-approval:";
}

public class BootstrapOptions
{
    public bool Enabled { get; set; } = true;
    public List<BootstrapDatabaseOptions> Databases { get; set; } = [];
}

public class BootstrapDatabaseOptions
{
    public string? BootstrapId { get; set; }
    public string? Name { get; set; }
    public string? Provider { get; set; }
    public string? Host { get; set; }
    public string? Port { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ExtraSettings { get; set; }
    public List<BootstrapMcpKeyOptions> McpKeys { get; set; } = [];
}

public class BootstrapMcpKeyOptions
{
    public string? BootstrapId { get; set; }
    public string? Name { get; set; }
    public string? Key { get; set; }
    public string? AllowedTools { get; set; }
}

public class TelemetryOptions
{
    public bool PrometheusEnabled { get; set; } = true;
    public string PrometheusHost { get; set; } = "localhost";
    public int PrometheusPort { get; set; } = 9000;
    public string OtlpEndpoint { get; set; } = string.Empty;
    public string ServiceName { get; set; } = "hs-sql-agent";

    internal static TelemetryOptions FromLegacy(HsSqlAgentServiceOptions legacy) => new()
    {
        PrometheusEnabled = legacy.Telemetry.PrometheusEnabled,
        PrometheusHost = legacy.Telemetry.PrometheusHost,
        PrometheusPort = legacy.Telemetry.PrometheusPort,
        OtlpEndpoint = legacy.Telemetry.OtlpEndpoint,
        ServiceName = legacy.Telemetry.ServiceName
    };
}

public class OperabilityOptions
{
    public bool HealthProbeEnabled { get; set; } = true;
    public int HealthProbeIntervalSeconds { get; set; } = 60;
    public int HealthProbeTimeoutSeconds { get; set; } = 10;
    public int HealthProbeMaxConcurrency { get; set; } = 4;
    public int SlowQueryThresholdMs { get; set; } = 1000;
    public string AlertWebhookUrl { get; set; } = string.Empty;
    public string AlertWebhookSecret { get; set; } = string.Empty;
    public string SiemWebhookUrl { get; set; } = string.Empty;
    public string SiemWebhookSecret { get; set; } = string.Empty;
    public int DeliveryMaxAttempts { get; set; } = 6;
    public int DeliveryMaxConcurrency { get; set; } = 4;
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
/// Pipeline configuration retained for fixed public HTTP surfaces and legacy fluent compatibility.
/// </summary>
public class HsSqlAgentPipelineOptions
{
    public string McpEndpoint { get; set; } = "/mcp";
    public string AdminApiPrefix { get; set; } = "/api";
    public string AdminUiRequestPath { get; set; } = "/";
    public string AdminUiRootPath { get; set; } = "wwwroot";
    public bool ServeAdminUi { get; set; }
}
