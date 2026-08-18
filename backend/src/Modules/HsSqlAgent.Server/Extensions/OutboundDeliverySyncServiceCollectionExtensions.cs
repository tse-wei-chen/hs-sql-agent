using Admin.Service.Interfaces;
using Admin.Service.Services;
using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Extensions;

internal static class OutboundDeliverySyncServiceCollectionExtensions
{
    public static IServiceCollection AddOutboundDeliverySync(
        this IServiceCollection services,
        string provider,
        string? connectionString,
        string keyPrefix)
    {
        if (string.Equals(provider, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IOutboundDeliverySignal, OutboundDeliverySignal>();
            return services;
        }

        if (!string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported outbound delivery sync provider '{provider}'. Supported providers: Memory, Redis.");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "OutboundDeliverySync connection string is required when provider is Redis.");
        if (string.IsNullOrWhiteSpace(keyPrefix))
            throw new InvalidOperationException("OutboundDeliverySync key prefix is required.");

        services.AddSingleton(new RedisOutboundDeliverySignalOptions(
            connectionString,
            $"{keyPrefix}notify"));
        services.AddSingleton<RedisOutboundDeliverySignal>();
        services.AddSingleton<IOutboundDeliverySignal>(sp => sp.GetRequiredService<RedisOutboundDeliverySignal>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RedisOutboundDeliverySignal>());
        return services;
    }
}
