using HsSqlAgent.Server.Services;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Extensions;

internal static class DmlApprovalChallengeStoreServiceCollectionExtensions
{
    public static IServiceCollection AddDmlApprovalChallengeStore(
        this IServiceCollection services,
        string provider,
        string? connectionString,
        string keyPrefix)
    {
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IDmlApprovalChallengeStore, InMemoryDmlApprovalChallengeStore>();
            return services;
        }

        if (!string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported DML approval store provider '{provider}'. Supported providers: Memory, Redis.");
        }
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "DML approval store connection string is required when provider is Redis.");
        if (string.IsNullOrWhiteSpace(keyPrefix))
            throw new InvalidOperationException("DML approval store key prefix is required.");

        services.AddSingleton(new RedisDmlApprovalChallengeOptions(connectionString, keyPrefix));
        services.AddSingleton<IDmlApprovalChallengeStore, RedisDmlApprovalChallengeStore>();
        return services;
    }
}
