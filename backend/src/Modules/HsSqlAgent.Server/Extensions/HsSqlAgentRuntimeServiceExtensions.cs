using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <summary>
    /// Opt into the built-in six-provider single-target DML snapshot reader and
    /// commit-transaction schema freshness verifier. Register on IServiceCollection
    /// before resolving TypedDmlRuntime. Unsupported shapes are rejected, not skipped.
    /// </summary>
    public static IServiceCollection AddHsSqlAgentNativeDmlSchemaFreshness(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<NativeDmlSchemaSnapshotService>();
        services.TryAddSingleton<IDmlSchemaSnapshotSource>(
            sp => sp.GetRequiredService<NativeDmlSchemaSnapshotService>());
        services.TryAddSingleton<IDmlSchemaSnapshotFreshnessVerifier>(
            sp => sp.GetRequiredService<NativeDmlSchemaSnapshotService>());
        return services;
    }

    /// <summary>
    /// Explicit PostgreSQL-only DDL opt-in. Never registered by AddHsSqlAgentRuntime
    /// or AddHsSqlAgentMcp by default; DDL approval also needs its OWN
    /// independently registered IDdlApprovalProvider and per-key tool grant.
    /// </summary>
    public static IServiceCollection AddHsSqlAgentPostgresDdl(
        this IServiceCollection services, PostgresDdlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled
            || options.AllowedSchemas.Count == 0
            || !(options.AllowCreateTable || options.AllowAddColumn || options.AllowCreateIndex))
            throw new ArgumentException(
                "PostgreSQL DDL opt-in requires Enabled=true, explicit schema allowlist and at least one operation.");
        services.TryAddSingleton(options);
        services.TryAddSingleton<PostgresDdlRuntime>();
        return services;
    }

    /// <summary>
    /// Explicit file-backed SQLite-only DDL Host. Shares only the existing
    /// DDL approval interface and MCP tool; transactional semantics are
    /// proven separately from PostgreSQL. Default runtime never registers it.
    /// </summary>
    public static IServiceCollection AddHsSqlAgentSqliteDdl(
        this IServiceCollection services, SqliteDdlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled
            || !(options.AllowCreateTable || options.AllowAddColumn || options.AllowCreateIndex))
            throw new ArgumentException("SQLite DDL requires Enabled=true and an explicit operation allowlist.");
        services.TryAddSingleton(options);
        services.TryAddSingleton<SqliteDdlRuntime>();
        return services;
    }

    /// <summary>
    /// Explicit SQL Server 2022 CREATE TABLE-only Host opt-in. No automatic
    /// registration, and no implicit inheritance from Query or DML grants.
    /// </summary>
    public static IServiceCollection AddHsSqlAgentSqlServerDdl(
        this IServiceCollection services, SqlServerDdlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || !options.AllowCreateTable || options.AllowedSchemas.Count == 0)
            throw new ArgumentException(
                "SQL Server DDL requires Enabled=true, an explicit CREATE TABLE operation and a schema allowlist.");
        services.TryAddSingleton(options);
        services.TryAddSingleton<SqlServerDdlRuntime>();
        return services;
    }

    /// <summary>
    /// Explicit Firebird 5 CREATE TABLE-only Host. A real physical GUID allowlist,
    /// independent human approval and per-key execute_ddl_sql grant are mandatory.
    /// </summary>
    public static IServiceCollection AddHsSqlAgentFirebirdDdl(
        this IServiceCollection services, FirebirdDdlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || !options.AllowCreateTable
            || options.AllowedDatabaseGuids.Count == 0)
            throw new ArgumentException(
                "Firebird DDL requires Enabled=true, CREATE TABLE opt-in and physical GUID allowlist.");
        services.TryAddSingleton(options);
        services.TryAddSingleton<FirebirdDdlRuntime>();
        return services;
    }

    /// <summary>
    /// MySQL 8.4 InnoDB CREATE TABLE implicit-commit Host. Requires explicit
    /// selected database allowlist, independent DDL approval and per-key grant.
    /// </summary>
    public static IServiceCollection AddHsSqlAgentMySqlDdl(
        this IServiceCollection services, MySqlDdlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || !options.AllowCreateTable
            || options.AllowedDatabases.Count == 0)
            throw new ArgumentException(
                "MySQL DDL requires Enabled=true, CREATE TABLE opt-in and database allowlist.");
        services.TryAddSingleton(options);
        services.TryAddSingleton<MySqlDdlRuntime>();
        return services;
    }

    /// <summary>
    /// Explicit Oracle 26ai implicit-commit CREATE TABLE Host. Does not register
    /// under ordinary runtime/MCP and never inherits DML authorization.
    /// </summary>
    public static IServiceCollection AddHsSqlAgentOracleDdl(
        this IServiceCollection services, OracleDdlExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || !options.AllowCreateTable
            || options.AllowedPdbNames.Count == 0 || options.AllowedSchemas.Count == 0)
            throw new ArgumentException(
                "Oracle DDL requires Enabled, CREATE TABLE, PDB and schema allowlists.");
        services.TryAddSingleton(options);
        services.TryAddSingleton<OracleDdlRuntime>();
        return services;
    }

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

        services.AddSingleton(options.SecretProtection);
        services.AddSingleton<ICryptoService>(new CryptoService(options.SecretProtection));
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
            compileEvidenceObserver: provider.GetRequiredService<ISqlCompileEvidenceObserver>(),
            schemaFreshnessVerifier: provider.GetService<IDmlSchemaSnapshotFreshnessVerifier>(),
            schemaSnapshotSource: provider.GetService<IDmlSchemaSnapshotSource>(),
            targetCommitWitness: provider.GetService<IDmlTargetCommitWitness>(),
            targetExecutionFence: provider.GetService<IDmlTargetExecutionFence>()));
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
