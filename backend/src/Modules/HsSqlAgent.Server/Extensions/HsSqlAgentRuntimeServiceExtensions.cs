using System.Text;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Models;
using HsSqlAgent.Server.Services;
using Infrastructure.Caching;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Services;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentRuntimeServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentRuntime(
        this HsSqlAgentRegistrationBuilder builder,
        Action<HsSqlAgentRuntimeOptions>? configure = null)
    {
        builder.ThrowIfAlreadyConfigured("runtime", configure);
        if (builder.IsRegistered("runtime")) return builder;

        var options = builder.GetOrCreateOptions(() => builder.LegacyOptions is { } legacy
            ? HsSqlAgentRuntimeOptions.FromLegacy(legacy)
            : new HsSqlAgentRuntimeOptions());
        configure?.Invoke(options);
        if (!builder.TryRegister("runtime")) return builder;

        var services = builder.Services;

        ValidateWebhook("Operability Alert", options.Operability.AlertWebhookUrl, options.Operability.AlertWebhookSecret);
        ValidateWebhook("Operability SIEM", options.Operability.SiemWebhookUrl, options.Operability.SiemWebhookSecret);
        if (string.IsNullOrWhiteSpace(options.Operability.AuditFallbackPath))
            throw new InvalidOperationException("Operability AuditFallbackPath is required.");

        services.AddCacheProvider(options.Cache.Provider, options.Cache.ConnectionString, options.Cache.KeyPrefix);
        services.AddSingleton<IRateLimitingRuntimeState, RateLimitingRuntimeState>();
        services.AddSingleton<ISecurityPolicyRuntimeState, SecurityPolicyRuntimeState>();
        services.AddSecurityPolicySync(
            options.SecurityPolicySync.Provider,
            options.SecurityPolicySync.ConnectionString,
            options.SecurityPolicySync.KeyPrefix,
            options.SecurityPolicySync.RefreshIntervalSeconds);
        services.AddRequestRateLimiter(
            options.RateLimiter.Provider,
            options.RateLimiter.ConnectionString,
            options.RateLimiter.FailureMode,
            options.RateLimiter.KeyPrefix);
        services.AddSingleton<ILayeredRateLimitService, LayeredRateLimitService>();
        services.AddSqlConcurrencyLimiter(
            options.SqlConcurrency.Provider,
            options.SqlConcurrency.ConnectionString,
            options.SqlConcurrency.FailureMode,
            options.SqlConcurrency.Key,
            options.SqlConcurrency.LeaseSeconds);
        services.AddDmlApprovalChallengeStore(
            options.DmlApprovalStore.Provider,
            options.DmlApprovalStore.ConnectionString,
            options.DmlApprovalStore.KeyPrefix);
        services.AddOutboundDeliverySync(
            options.OutboundDeliverySync.Provider,
            options.OutboundDeliverySync.ConnectionString,
            options.OutboundDeliverySync.KeyPrefix);

        services.AddSingleton<ICryptoService, CryptoService>();
        services.AddSingleton<IQueryValueParserService, QueryValueParserService>();
        services.AddSingleton<HsSqlAgentMetrics>();
        services.AddSingleton<IHsSqlAgentMetrics>(provider => provider.GetRequiredService<HsSqlAgentMetrics>());
        services.AddSingleton<IAuditMetricSink>(provider => provider.GetRequiredService<HsSqlAgentMetrics>());
        services.AddSingleton<ISqlCompileEvidenceObserver, SqlCompileEvidenceObserver>();

        services.AddScoped<ISqlStrategy, MySqlStrategy>();
        services.AddScoped<ISqlStrategy, PostgresStrategy>();
        services.AddScoped<ISqlStrategy, SqliteStrategy>();
        services.AddScoped<ISqlStrategy, MsSqlServerStrategy>();
        services.AddScoped<ISqlStrategy, OracleStrategy>();
        services.AddScoped<ISqlStrategy, FirebirdStrategy>();
        services.AddScoped<SqlStrategyFactory>();
        services.AddScoped<ISqlProviderFactory>(provider => provider.GetRequiredService<SqlStrategyFactory>());
        services.AddScoped<ISqlConnectionStringFactory>(provider => provider.GetRequiredService<SqlStrategyFactory>());
        services.AddScoped<IDbSetterService, DbSetterService>();
        services.AddScoped<ITypedQueryRuntime, TypedQueryRuntime>();
        services.AddSingleton(provider => new TypedDmlRuntime(
            challengeStore: provider.GetRequiredService<IDmlApprovalChallengeStore>(),
            compileEvidenceObserver: provider.GetRequiredService<ISqlCompileEvidenceObserver>()));
        services.AddScoped<DurableDmlApprovalLifecycle>();
        services.AddScoped<IDmlApprovalCompletionSink>(provider =>
            provider.GetRequiredService<DurableDmlApprovalLifecycle>());

        services.Configure<BootstrapOptions>(bootstrap =>
        {
            bootstrap.Enabled = options.Bootstrap.Enabled;
            bootstrap.Databases = options.Bootstrap.Databases;
        });
        services.Configure<OperabilitySettings>(operability =>
        {
            var source = options.Operability;
            operability.HealthProbeEnabled = source.HealthProbeEnabled;
            operability.HealthProbeIntervalSeconds = source.HealthProbeIntervalSeconds;
            operability.HealthProbeTimeoutSeconds = source.HealthProbeTimeoutSeconds;
            operability.HealthProbeMaxConcurrency = source.HealthProbeMaxConcurrency;
            operability.SlowQueryThresholdMs = source.SlowQueryThresholdMs;
            operability.AlertWebhookUrl = source.AlertWebhookUrl;
            operability.AlertWebhookSecret = source.AlertWebhookSecret;
            operability.SiemWebhookUrl = source.SiemWebhookUrl;
            operability.SiemWebhookSecret = source.SiemWebhookSecret;
            operability.DeliveryMaxAttempts = source.DeliveryMaxAttempts;
            operability.DeliveryMaxConcurrency = source.DeliveryMaxConcurrency;
            operability.AuditRetentionDays = source.AuditRetentionDays;
            operability.AuditRetentionMode = source.AuditRetentionMode;
            operability.AuditArchivePath = source.AuditArchivePath;
            operability.AuditFallbackPath = source.AuditFallbackPath;
            operability.AuditRetentionRunHourUtc = source.AuditRetentionRunHourUtc;
        });
        services.Configure<RateLimitingSettings>(rl =>
        {
            rl.PermitLimit = options.RateLimiter.PermitLimit;
            rl.WindowSeconds = options.RateLimiter.WindowSeconds;
        });

        return builder;
    }

    private static void ValidateWebhook(string name, string url, string secret)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"{name} webhook URL must be an absolute HTTP(S) URL.");
        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException($"{name} webhook secret must be at least 32 bytes when enabled.");
    }
}
