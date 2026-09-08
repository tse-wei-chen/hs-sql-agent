using System.Text;
using HsSqlAgent.Server.Models;
using Microsoft.Extensions.Configuration;

namespace HsSqlAgent.Server.Services;

internal static class RuntimeDoctorAnalyzer
{
    private static readonly string[] CoordinationSections =
    [
        "CacheConfig",
        "RateLimiter",
        "SecurityPolicySync",
        "OutboundDeliverySync",
        "SqlConcurrency"
    ];

    internal static RuntimeDoctorResponse Analyze(
        IConfiguration configuration,
        string environmentName,
        int databaseCount,
        int activeKeyCount,
        int usedActiveKeyCount)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var checks = new List<RuntimeDoctorCheck>();
        var isProduction = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);

        AddSecretCheck(
            checks,
            "security.mcp-hmac",
            "Security",
            "MCP key HMAC secret",
            configuration["McpKeySettings:HmacSecretKey"],
            "Set McpKeySettings:HmacSecretKey to at least 32 UTF-8 bytes using a non-example secret.");
        AddSecretCheck(
            checks,
            "security.jwt",
            "Security",
            "JWT signing secret",
            configuration["JwtSettings:SecretKey"],
            "Set JwtSettings:SecretKey to at least 32 UTF-8 bytes using a non-example secret.");

        AddPublicEndpointCheck(checks, configuration["Mcp:PublicEndpoint"], isProduction);

        var coordinationProviders = CoordinationSections
            .Select(section => new CoordinationProvider(
                section,
                configuration[$"{section}:Provider"] ?? "Memory",
                configuration[$"{section}:ConnectionString"] ?? string.Empty))
            .ToArray();
        var redisCount = coordinationProviders.Count(item => IsRedis(item.Provider));
        var deploymentMode = redisCount switch
        {
            0 => "SingleNode",
            var count when count == coordinationProviders.Length => "Distributed",
            _ => "Mixed"
        };

        AddAdminStoreCheck(
            checks,
            configuration["AdminDatabase:Provider"],
            configuration["AdminDatabase:ConnectionString"],
            deploymentMode);
        AddDataProtectionCheck(
            checks,
            configuration["EnterpriseIdentity:DataProtectionKeyPath"],
            isProduction);
        AddCoordinationChecks(checks, configuration, coordinationProviders, deploymentMode);
        AddApprovalCheck(checks, configuration, isProduction);
        AddOidcCheck(checks, configuration, isProduction);
        AddTelemetryCheck(checks, configuration);
        AddOutboundChecks(checks, configuration, isProduction);
        AddRuntimeReadinessChecks(checks, databaseCount, activeKeyCount, usedActiveKeyCount);

        var overallStatus = checks.Any(check => check.Status == "Error")
            ? "Error"
            : checks.Any(check => check.Status == "Warning")
                ? "Warning"
                : "Healthy";

        return new RuntimeDoctorResponse(
            overallStatus,
            environmentName,
            deploymentMode,
            DateTime.UtcNow,
            checks);
    }

    private static void AddSecretCheck(
        ICollection<RuntimeDoctorCheck> checks,
        string id,
        string category,
        string title,
        string? value,
        string action)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            checks.Add(Error(id, category, title, "Required secret is missing.", action));
            return;
        }

        if (Encoding.UTF8.GetByteCount(value) < 32)
        {
            checks.Add(Error(id, category, title, "Configured secret is shorter than 32 UTF-8 bytes.", action));
            return;
        }

        if (LooksLikePlaceholder(value))
        {
            checks.Add(Error(id, category, title, "Configured secret still looks like an example or placeholder value.", action));
            return;
        }

        checks.Add(Healthy(id, category, title, "Secret is configured and passes the minimum length/placeholder checks."));
    }

    private static void AddPublicEndpointCheck(
        ICollection<RuntimeDoctorCheck> checks,
        string? value,
        bool isProduction)
    {
        const string id = "network.mcp-public-endpoint";
        const string category = "Network";
        const string title = "MCP public endpoint";
        if (string.IsNullOrWhiteSpace(value))
        {
            checks.Add(Warning(
                id,
                category,
                title,
                "Mcp:PublicEndpoint is not configured; generated client configuration may not describe the externally reachable MCP URL.",
                "Set Mcp:PublicEndpoint to the externally reachable /mcp URL."));
            return;
        }

        if (!TryHttpUri(value, out var uri))
        {
            checks.Add(Error(id, category, title, "Public endpoint is not an absolute HTTP/HTTPS URL.", "Set a valid absolute MCP public endpoint."));
            return;
        }

        if (isProduction && uri!.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            checks.Add(Warning(id, category, title, "Production MCP endpoint uses plain HTTP.", "Use HTTPS at the public edge, or confirm TLS termination is handled by a trusted reverse proxy."));
            return;
        }

        checks.Add(Healthy(id, category, title, "Public MCP endpoint is configured as an absolute HTTP/HTTPS URL."));
    }

    private static void AddAdminStoreCheck(
        ICollection<RuntimeDoctorCheck> checks,
        string? provider,
        string? connectionString,
        string deploymentMode)
    {
        const string id = "storage.admin-store";
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(connectionString))
        {
            checks.Add(Error(
                id,
                "Storage",
                "Admin Store",
                "AdminDatabase provider or connection string is missing.",
                "Configure AdminDatabase:Provider and AdminDatabase:ConnectionString."));
            return;
        }

        if (deploymentMode == "Distributed" && provider.Contains("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Warning(
                id,
                "Storage",
                "Admin Store",
                "Distributed coordination is enabled while the Admin Store is SQLite, which is node-local.",
                "Use the shared PostgreSQL Admin Store for a multi-node deployment."));
            return;
        }

        checks.Add(Healthy(id, "Storage", "Admin Store", $"Admin Store provider '{provider}' is configured."));
    }

    private static void AddDataProtectionCheck(
        ICollection<RuntimeDoctorCheck> checks,
        string? path,
        bool isProduction)
    {
        const string id = "security.data-protection";
        if (string.IsNullOrWhiteSpace(path))
        {
            var detail = "EnterpriseIdentity:DataProtectionKeyPath is not configured.";
            var action = "Persist ASP.NET Core Data Protection keys to stable storage so protected approval/auth state survives restarts.";
            checks.Add(isProduction
                ? Error(id, "Security", "Data Protection keys", detail, action)
                : Warning(id, "Security", "Data Protection keys", detail, action));
            return;
        }

        checks.Add(Healthy(id, "Security", "Data Protection keys", "A persistent Data Protection key path is configured."));
    }

    private static void AddCoordinationChecks(
        ICollection<RuntimeDoctorCheck> checks,
        IConfiguration configuration,
        IReadOnlyList<CoordinationProvider> providers,
        string deploymentMode)
    {
        foreach (var item in providers)
        {
            if (!item.Provider.Equals("Memory", StringComparison.OrdinalIgnoreCase) && !IsRedis(item.Provider))
            {
                checks.Add(Error(
                    $"coordination.{item.Section.ToLowerInvariant()}",
                    "Coordination",
                    $"{item.Section} provider",
                    $"Unknown provider '{item.Provider}'.",
                    "Use Memory for single-node operation or Redis for shared multi-node coordination."));
                continue;
            }

            if (IsRedis(item.Provider) && string.IsNullOrWhiteSpace(item.ConnectionString))
            {
                checks.Add(Error(
                    $"coordination.{item.Section.ToLowerInvariant()}",
                    "Coordination",
                    $"{item.Section} Redis configuration",
                    $"{item.Section} selects Redis but has no connection string.",
                    $"Set {item.Section}:ConnectionString or switch the provider back to Memory for single-node use."));
            }
        }

        if (deploymentMode == "Mixed")
        {
            checks.Add(Warning(
                "coordination.mode",
                "Coordination",
                "Coordination mode",
                "Runtime coordination providers mix Memory and Redis. Some limits/state will be node-local while others are shared.",
                "For single-node use keep all coordination providers on Memory; for multi-node use configure all required providers on Redis."));
        }
        else
        {
            checks.Add(Healthy(
                "coordination.mode",
                "Coordination",
                "Coordination mode",
                deploymentMode == "Distributed"
                    ? "All runtime coordination providers use Redis."
                    : "All runtime coordination providers use in-process Memory for single-node operation."));
        }

        foreach (var section in new[] { "RateLimiter", "SqlConcurrency" })
        {
            var item = providers.First(provider => provider.Section == section);
            if (!IsRedis(item.Provider)) continue;
            var failureMode = configuration[$"{section}:FailureMode"] ?? "FailClosed";
            if (!failureMode.Equals("FailClosed", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(Warning(
                    $"coordination.{section.ToLowerInvariant()}-failure-mode",
                    "Coordination",
                    $"{section} failure mode",
                    $"{section} uses Redis with failure mode '{failureMode}'.",
                    "Use FailClosed when shared coordination is a security or concurrency boundary."));
            }
        }
    }

    private static void AddApprovalCheck(
        ICollection<RuntimeDoctorCheck> checks,
        IConfiguration configuration,
        bool isProduction)
    {
        var provider = configuration["DmlApproval:Provider"] ?? "McpElicitation";
        if (provider.Equals("McpElicitation", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Healthy("approval.provider", "DML Approval", "Approval provider", "MCP Elicitation is configured as the DML approval provider."));
            return;
        }

        if (!provider.Equals("Webhook", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(Error("approval.provider", "DML Approval", "Approval provider", $"Unknown DML approval provider '{provider}'.", "Use McpElicitation or Webhook."));
            return;
        }

        var endpoint = configuration["DmlApproval:Webhook:Endpoint"];
        var callback = configuration["DmlApproval:Webhook:CallbackUrl"];
        var signingSecret = configuration["DmlApproval:Webhook:SigningSecret"];
        var issues = new List<string>();
        if (!TryHttpUri(endpoint, out var endpointUri)) issues.Add("Endpoint must be an absolute HTTP/HTTPS URL");
        if (!TryHttpUri(callback, out var callbackUri)) issues.Add("CallbackUrl must be an absolute HTTP/HTTPS URL");
        if (string.IsNullOrWhiteSpace(signingSecret) || Encoding.UTF8.GetByteCount(signingSecret) < 32 || LooksLikePlaceholder(signingSecret))
            issues.Add("SigningSecret must be a non-placeholder value of at least 32 UTF-8 bytes");
        if (isProduction && endpointUri is { Scheme: "http", IsLoopback: false }) issues.Add("production Webhook Endpoint should use HTTPS");
        if (isProduction && callbackUri is { Scheme: "http", IsLoopback: false }) issues.Add("production Webhook CallbackUrl should use HTTPS");

        checks.Add(issues.Count == 0
            ? Healthy("approval.provider", "DML Approval", "Webhook approval", "Webhook approval endpoint, callback, and signing-secret prerequisites are configured.")
            : Error("approval.provider", "DML Approval", "Webhook approval", string.Join("; ", issues) + ".", "Complete DmlApproval:Webhook configuration before enabling Webhook approval."));
    }

    private static void AddOidcCheck(
        ICollection<RuntimeDoctorCheck> checks,
        IConfiguration configuration,
        bool isProduction)
    {
        if (!GetBool(configuration, "EnterpriseIdentity:OidcEnabled"))
        {
            checks.Add(Healthy("identity.oidc", "Identity", "OIDC", "OIDC is disabled; local identity remains the active path."));
            return;
        }

        var authority = configuration["EnterpriseIdentity:Authority"];
        var clientId = configuration["EnterpriseIdentity:ClientId"];
        var clientSecret = configuration["EnterpriseIdentity:ClientSecret"];
        var callback = configuration["EnterpriseIdentity:FrontendCallbackUrl"];
        var issues = new List<string>();
        if (!TryHttpUri(authority, out var authorityUri)) issues.Add("Authority must be an absolute HTTP/HTTPS URL");
        if (string.IsNullOrWhiteSpace(clientId)) issues.Add("ClientId is missing");
        if (string.IsNullOrWhiteSpace(clientSecret)) issues.Add("ClientSecret is missing");
        if (string.IsNullOrWhiteSpace(callback)) issues.Add("FrontendCallbackUrl is missing");
        if (isProduction && authorityUri is { Scheme: "http", IsLoopback: false }) issues.Add("production OIDC Authority should use HTTPS");
        if (isProduction && !GetBool(configuration, "EnterpriseIdentity:RequireHttpsMetadata", true)) issues.Add("RequireHttpsMetadata is disabled in Production");

        checks.Add(issues.Count == 0
            ? Healthy("identity.oidc", "Identity", "OIDC", "OIDC prerequisites are configured.")
            : Error("identity.oidc", "Identity", "OIDC", string.Join("; ", issues) + ".", "Complete EnterpriseIdentity OIDC settings before enabling OIDC."));
    }

    private static void AddTelemetryCheck(
        ICollection<RuntimeDoctorCheck> checks,
        IConfiguration configuration)
    {
        var endpoint = configuration["Telemetry:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            checks.Add(Healthy("telemetry.export", "Telemetry", "Telemetry export", "OTLP export is optional and currently not configured."));
            return;
        }

        checks.Add(TryHttpUri(endpoint, out _)
            ? Healthy("telemetry.export", "Telemetry", "Telemetry export", "OTLP endpoint is configured as an absolute HTTP/HTTPS URL.")
            : Warning("telemetry.export", "Telemetry", "Telemetry export", "Telemetry:OtlpEndpoint is not an absolute HTTP/HTTPS URL.", "Correct the OTLP endpoint or clear it to disable OTLP export."));
    }

    private static void AddOutboundChecks(
        ICollection<RuntimeDoctorCheck> checks,
        IConfiguration configuration,
        bool isProduction)
    {
        AddOptionalWebhookCheck(checks, configuration, "Alert", "Operability:AlertWebhookUrl", "Operability:AlertWebhookSecret", isProduction);
        AddOptionalWebhookCheck(checks, configuration, "SIEM", "Operability:SiemWebhookUrl", "Operability:SiemWebhookSecret", isProduction);
    }

    private static void AddOptionalWebhookCheck(
        ICollection<RuntimeDoctorCheck> checks,
        IConfiguration configuration,
        string name,
        string urlKey,
        string secretKey,
        bool isProduction)
    {
        var url = configuration[urlKey];
        if (string.IsNullOrWhiteSpace(url)) return;
        var secret = configuration[secretKey];
        var issues = new List<string>();
        if (!TryHttpUri(url, out var uri)) issues.Add("URL is not an absolute HTTP/HTTPS URL");
        if (string.IsNullOrWhiteSpace(secret)) issues.Add("signing secret is missing");
        if (isProduction && uri is { Scheme: "http", IsLoopback: false }) issues.Add("production URL should use HTTPS");
        checks.Add(issues.Count == 0
            ? Healthy($"operability.{name.ToLowerInvariant()}-webhook", "Operability", $"{name} webhook", $"{name} webhook prerequisites are configured.")
            : Warning($"operability.{name.ToLowerInvariant()}-webhook", "Operability", $"{name} webhook", string.Join("; ", issues) + ".", $"Review {name} webhook URL and signing-secret configuration."));
    }

    private static void AddRuntimeReadinessChecks(
        ICollection<RuntimeDoctorCheck> checks,
        int databaseCount,
        int activeKeyCount,
        int usedActiveKeyCount)
    {
        checks.Add(databaseCount > 0
            ? Healthy("readiness.database", "Readiness", "Target database", $"{databaseCount} database connection(s) are configured.")
            : Warning("readiness.database", "Readiness", "Target database", "No target database is configured.", "Add and test a database connection before onboarding an agent."));

        checks.Add(activeKeyCount > 0
            ? Healthy("readiness.mcp-key", "Readiness", "Active MCP key", $"{activeKeyCount} active MCP key(s) are available.")
            : Warning("readiness.mcp-key", "Readiness", "Active MCP key", "No active MCP key is available.", "Issue an MCP key after configuring a target database."));

        if (activeKeyCount > 0)
        {
            checks.Add(usedActiveKeyCount > 0
                ? Healthy("readiness.agent-traffic", "Readiness", "Observed agent traffic", $"{usedActiveKeyCount} active key(s) have been used by an agent.")
                : Warning("readiness.agent-traffic", "Readiness", "Observed agent traffic", "Active keys exist, but no agent request has been observed yet.", "Connect a client and run a governed read query to complete the readiness path."));
        }
    }

    private static bool LooksLikePlaceholder(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains("yourmcphmacsecretkeyhere", StringComparison.Ordinal)
               || normalized.Contains("yoursupersecretkeyhere", StringComparison.Ordinal)
               || normalized is "changeme" or "change-me" or "replace-me"
               || normalized.StartsWith("<", StringComparison.Ordinal);
    }

    private static bool TryHttpUri(string? value, out Uri? uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return true;
        uri = null;
        return false;
    }

    private static bool IsRedis(string provider) => provider.Equals("Redis", StringComparison.OrdinalIgnoreCase);

    private static bool GetBool(IConfiguration configuration, string key, bool defaultValue = false) =>
        bool.TryParse(configuration[key], out var value) ? value : defaultValue;

    private static RuntimeDoctorCheck Healthy(string id, string category, string title, string detail) =>
        new(id, category, "Healthy", title, detail);

    private static RuntimeDoctorCheck Warning(string id, string category, string title, string detail, string? action = null) =>
        new(id, category, "Warning", title, detail, action);

    private static RuntimeDoctorCheck Error(string id, string category, string title, string detail, string? action = null) =>
        new(id, category, "Error", title, detail, action);

    private sealed record CoordinationProvider(string Section, string Provider, string ConnectionString);
}
