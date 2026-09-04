using Admin.Service.Interfaces;
using Admin.Service.Services;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentAdminStoreServiceExtensions
{
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentAdminStore(this HsSqlAgentRegistrationBuilder builder)
    {
        builder.AddHsSqlAgentRuntime();
        if (!builder.TryRegister("admin-store")) return builder;

        var services = builder.Services;
        var options = builder.Options;
        if (string.IsNullOrWhiteSpace(options.AdminDatabaseProvider))
            throw new InvalidOperationException("AdminDatabaseProvider is required.");
        if (string.IsNullOrWhiteSpace(options.AdminConnectionString))
            throw new InvalidOperationException("AdminConnectionString is required.");

        services.AddAdminDatabase(options.AdminDatabaseProvider, options.AdminConnectionString);
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
