using Admin.Service.Interfaces;
using Admin.Service.Services;
using HsSqlAgent.Server.Models;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentAdminStoreServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentAdminStore(
        this HsSqlAgentRegistrationBuilder builder,
        Action<HsSqlAgentAdminStoreOptions>? configure = null)
    {
        builder.AddHsSqlAgentRuntime();
        builder.ThrowIfAlreadyConfigured("admin-store", configure);
        if (builder.IsRegistered("admin-store")) return builder;

        var options = builder.GetOrCreateOptions(() => builder.LegacyOptions is { } legacy
            ? HsSqlAgentAdminStoreOptions.FromLegacy(legacy)
            : new HsSqlAgentAdminStoreOptions());
        configure?.Invoke(options);
        if (!builder.TryRegister("admin-store")) return builder;

        if (string.IsNullOrWhiteSpace(options.Provider))
            throw new InvalidOperationException("AdminStore Provider is required.");
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("AdminStore ConnectionString is required.");

        var services = builder.Services;
        services.AddAdminDatabase(options.Provider, options.ConnectionString);
        services.AddScoped<ISecurityPolicyService, SecurityPolicyService>();
        services.AddScoped<IMcpAccessKeyService, McpAccessKeyService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IOperabilityService, OperabilityService>();
        services.AddScoped<IAuditRetentionService, AuditRetentionService>();
        services.AddScoped<ICustomSqlToolService, CustomSqlToolService>();
        services.AddScoped<IDbManagementService, DbManagementService>();
        services.AddScoped<IDbSemanticService, DbSemanticService>();

        return builder;
    }
}
