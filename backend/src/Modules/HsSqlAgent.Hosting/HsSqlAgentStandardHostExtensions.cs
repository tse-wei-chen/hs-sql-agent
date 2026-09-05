using HsSqlAgent.Approvals;
using HsSqlAgent.Approvals.Webhook;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HsSqlAgent.Hosting;

/// <summary>
/// Provides the opinionated first-party HsSqlAgent composition used by the standalone ToolBox/Docker host.
/// Applications that need to replace individual capabilities should reference HsSqlAgent.Server directly instead.
/// </summary>
public static class HsSqlAgentStandardHostExtensions
{
    private const string DmlApprovalProviderKey = "DmlApproval:Provider";
    private const string McpElicitationProvider = "McpElicitation";
    private const string WebhookProvider = "Webhook";

    /// <summary>
    /// Registers the same HsSqlAgent capability composition used by the official standalone host.
    /// Host-owned concerns such as URL binding and logging remain under the caller's ASP.NET Core configuration.
    /// </summary>
    public static WebApplicationBuilder AddHsSqlAgentStandardHost(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(HsSqlAgentStandardHostState)))
            throw new InvalidOperationException("HsSqlAgent standard hosting is already registered for this application.");

        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDmlApprovalProvider)))
        {
            throw new InvalidOperationException(
                "HsSqlAgent standard hosting owns DML approval provider selection. " +
                "Use HsSqlAgent.Server modular registration when composing a custom approval provider.");
        }

        var configuration = builder.Configuration;
        if (!builder.Environment.IsDevelopment()
            && string.IsNullOrWhiteSpace(configuration["Mcp:PublicEndpoint"]))
        {
            throw new InvalidOperationException(
                "Mcp:PublicEndpoint is required outside Development so generated client configuration uses the externally reachable MCP URL.");
        }

        var hs = builder.Services.AddHsSqlAgentCore();

        hs.AddHsSqlAgentRuntime(options => ConfigureRuntime(options, configuration));

        hs.AddHsSqlAgentAdminStore(options =>
        {
            options.Provider = configuration["AdminDatabase:Provider"] ?? options.Provider;
            options.ConnectionString = configuration["AdminDatabase:ConnectionString"]
                ?? configuration["AppConnectionString"]
                ?? throw new InvalidOperationException("Missing AdminDatabase:ConnectionString or AppConnectionString in configuration.");
        });

        hs.AddHsSqlAgentBuiltInAuth(options =>
        {
            options.Jwt.SecretKey = configuration["JwtSettings:SecretKey"] ?? string.Empty;
            options.Jwt.Issuer = configuration["JwtSettings:Issuer"] ?? options.Jwt.Issuer;
            options.Jwt.Audience = configuration["JwtSettings:Audience"] ?? options.Jwt.Audience;

            if (int.TryParse(configuration["JwtSettings:AccessTokenExpirationMinutes"], out var accessTokenExpiration))
                options.Jwt.AccessTokenExpirationMinutes = accessTokenExpiration;
            if (int.TryParse(configuration["JwtSettings:RefreshTokenExpirationDays"], out var refreshTokenExpiration))
                options.Jwt.RefreshTokenExpirationDays = refreshTokenExpiration;
            if (int.TryParse(configuration["Authentication:LockoutThreshold"], out var lockoutThreshold))
                options.Jwt.SignInLockoutThreshold = lockoutThreshold;
            if (int.TryParse(configuration["Authentication:LockoutMinutes"], out var lockoutMinutes))
                options.Jwt.SignInLockoutMinutes = lockoutMinutes;

            options.PasswordReset.BaseUrl = configuration["PasswordReset:BaseUrl"] ?? options.PasswordReset.BaseUrl;
            if (int.TryParse(configuration["PasswordReset:ExpirationMinutes"], out var resetExpiration))
                options.PasswordReset.ExpirationMinutes = resetExpiration;
            options.PasswordReset.SmtpHost = configuration["PasswordReset:SmtpHost"] ?? string.Empty;
            if (int.TryParse(configuration["PasswordReset:SmtpPort"], out var smtpPort))
                options.PasswordReset.SmtpPort = smtpPort;
            if (bool.TryParse(configuration["PasswordReset:SmtpEnableSsl"], out var smtpSsl))
                options.PasswordReset.SmtpEnableSsl = smtpSsl;
            options.PasswordReset.SmtpUsername = configuration["PasswordReset:SmtpUsername"] ?? string.Empty;
            options.PasswordReset.SmtpPassword = configuration["PasswordReset:SmtpPassword"] ?? string.Empty;
            options.PasswordReset.SmtpFrom = configuration["PasswordReset:SmtpFrom"] ?? string.Empty;

            configuration.GetSection("EnterpriseIdentity").Bind(options.EnterpriseIdentity);
        });

        hs.AddHsSqlAgentMcp(options =>
        {
            configuration.GetSection("Mcp").Bind(options);
            options.HmacSecretKey = configuration["McpKeySettings:HmacSecretKey"] ?? string.Empty;
        });

        var approvalProvider = ConfigureDmlApproval(builder.Services, configuration);

        hs.AddHsSqlAgentAdminApi();

        hs.AddHsSqlAgentTelemetry(options =>
        {
            configuration.GetSection("Telemetry").Bind(options);
        });

        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(new HsSqlAgentStandardHostState(approvalProvider));

        return builder;
    }

    /// <summary>
    /// Maps the same HsSqlAgent middleware/endpoints used by the official standalone host.
    /// Call this after mapping any host-owned endpoints that must take precedence over the packaged Admin UI.
    /// </summary>
    public static WebApplication UseHsSqlAgentStandardHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var state = app.Services.GetService<HsSqlAgentStandardHostState>()
            ?? throw new InvalidOperationException(
                "HsSqlAgent standard hosting was not registered. Call AddHsSqlAgentStandardHost() before builder.Build().");

        ValidateApprovalComposition(app.Services, state);

        app.UseExceptionHandler();
        app.UseHsSqlAgentMcp();
        app.UseHsSqlAgentAdminApi();
        app.MapControllers();

        if (state.ApprovalProvider == StandardDmlApprovalProvider.Webhook)
            app.MapHsSqlAgentWebhookApprovalCallback();

        app.UseHsSqlAgentAdminUi();
        return app;
    }

    private static void ConfigureRuntime(
        HsSqlAgent.Server.Models.HsSqlAgentRuntimeOptions options,
        IConfiguration configuration)
    {
        configuration.GetSection("Bootstrap").Bind(options.Bootstrap);
        configuration.GetSection("Operability").Bind(options.Operability);

        if (int.TryParse(configuration["RateLimiting:PermitLimit"], out var permitLimit))
            options.RateLimiter.PermitLimit = permitLimit;
        if (int.TryParse(configuration["RateLimiting:WindowSeconds"], out var windowSeconds))
            options.RateLimiter.WindowSeconds = windowSeconds;

        options.RateLimiter.Provider = configuration["RateLimiter:Provider"] ?? options.RateLimiter.Provider;
        options.RateLimiter.ConnectionString = configuration["RateLimiter:ConnectionString"]
            ?? configuration["CacheConfig:ConnectionString"]
            ?? options.RateLimiter.ConnectionString;
        options.RateLimiter.FailureMode = configuration["RateLimiter:FailureMode"] ?? options.RateLimiter.FailureMode;
        options.RateLimiter.KeyPrefix = configuration["RateLimiter:KeyPrefix"] ?? options.RateLimiter.KeyPrefix;

        options.SecurityPolicySync.Provider = configuration["SecurityPolicySync:Provider"] ?? options.SecurityPolicySync.Provider;
        options.SecurityPolicySync.ConnectionString = configuration["SecurityPolicySync:ConnectionString"]
            ?? configuration["RateLimiter:ConnectionString"]
            ?? configuration["CacheConfig:ConnectionString"]
            ?? options.SecurityPolicySync.ConnectionString;
        options.SecurityPolicySync.KeyPrefix = configuration["SecurityPolicySync:KeyPrefix"]
            ?? options.SecurityPolicySync.KeyPrefix;
        if (int.TryParse(configuration["SecurityPolicySync:RefreshIntervalSeconds"], out var refreshInterval))
            options.SecurityPolicySync.RefreshIntervalSeconds = refreshInterval;

        options.OutboundDeliverySync.Provider = configuration["OutboundDeliverySync:Provider"] ?? options.OutboundDeliverySync.Provider;
        options.OutboundDeliverySync.ConnectionString = configuration["OutboundDeliverySync:ConnectionString"]
            ?? configuration["RateLimiter:ConnectionString"]
            ?? configuration["CacheConfig:ConnectionString"]
            ?? options.OutboundDeliverySync.ConnectionString;
        options.OutboundDeliverySync.KeyPrefix = configuration["OutboundDeliverySync:KeyPrefix"]
            ?? options.OutboundDeliverySync.KeyPrefix;

        options.SqlConcurrency.Provider = configuration["SqlConcurrency:Provider"] ?? options.SqlConcurrency.Provider;
        options.SqlConcurrency.ConnectionString = configuration["SqlConcurrency:ConnectionString"]
            ?? configuration["RateLimiter:ConnectionString"]
            ?? configuration["CacheConfig:ConnectionString"]
            ?? options.SqlConcurrency.ConnectionString;
        options.SqlConcurrency.FailureMode = configuration["SqlConcurrency:FailureMode"] ?? options.SqlConcurrency.FailureMode;
        options.SqlConcurrency.Key = configuration["SqlConcurrency:Key"] ?? options.SqlConcurrency.Key;
        if (int.TryParse(configuration["SqlConcurrency:LeaseSeconds"], out var leaseSeconds))
            options.SqlConcurrency.LeaseSeconds = leaseSeconds;

        options.Cache.Provider = configuration["CacheConfig:Provider"] ?? options.Cache.Provider;
        options.Cache.ConnectionString = configuration["CacheConfig:ConnectionString"] ?? options.Cache.ConnectionString;
        options.Cache.KeyPrefix = configuration["CacheConfig:KeyPrefix"] ?? options.Cache.KeyPrefix;
    }

    private static StandardDmlApprovalProvider ConfigureDmlApproval(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var configuredProvider = configuration[DmlApprovalProviderKey]?.Trim();
        if (string.IsNullOrEmpty(configuredProvider)
            || string.Equals(configuredProvider, McpElicitationProvider, StringComparison.OrdinalIgnoreCase))
        {
            return StandardDmlApprovalProvider.McpElicitation;
        }

        if (string.Equals(configuredProvider, WebhookProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddHsSqlAgentWebhookApproval(options =>
                configuration.GetSection("DmlApproval:Webhook").Bind(options));
            return StandardDmlApprovalProvider.Webhook;
        }

        throw new InvalidOperationException(
            $"Unsupported {DmlApprovalProviderKey} '{configuredProvider}'. " +
            $"Supported values are {McpElicitationProvider} and {WebhookProvider}.");
    }

    private static void ValidateApprovalComposition(
        IServiceProvider services,
        HsSqlAgentStandardHostState state)
    {
        var provider = services.GetService<IDmlApprovalProvider>();

        if (state.ApprovalProvider == StandardDmlApprovalProvider.McpElicitation)
        {
            if (provider is not null)
            {
                throw new InvalidOperationException(
                    "HsSqlAgent standard hosting is configured for MCP Elicitation, " +
                    "but a custom IDmlApprovalProvider was registered. " +
                    "Use HsSqlAgent.Server modular registration for custom provider composition.");
            }

            return;
        }

        if (provider is not WebhookDmlApprovalProvider)
        {
            throw new InvalidOperationException(
                "HsSqlAgent standard hosting is configured for Webhook approval, " +
                "but the expected WebhookDmlApprovalProvider is not registered.");
        }
    }

    private sealed record HsSqlAgentStandardHostState(StandardDmlApprovalProvider ApprovalProvider);

    private enum StandardDmlApprovalProvider
    {
        McpElicitation,
        Webhook
    }
}
