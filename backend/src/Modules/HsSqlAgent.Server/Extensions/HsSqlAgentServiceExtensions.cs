using HsSqlAgent.Server.Middleware;
using HsSqlAgent.Server.Models;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentServiceExtensions
{
    /// <summary>
    /// Registers the legacy full-server preset. New integrations should prefer AddHsSqlAgentCore()
    /// plus explicit capability registrations.
    /// </summary>
    public static IServiceCollection AddHsSqlAgent(
        this IServiceCollection services,
        Action<HsSqlAgentServiceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new HsSqlAgentServiceOptions();
        configure(options);
        return services.AddHsSqlAgent(options);
    }

    /// <summary>
    /// Registers the legacy full-server preset.
    /// </summary>
    public static IServiceCollection AddHsSqlAgent(
        this IServiceCollection services,
        HsSqlAgentServiceOptions options)
    {
        services.AddHsSqlAgentCore(options)
            .AddHsSqlAgentRuntime()
            .AddHsSqlAgentAdminStore()
            .AddHsSqlAgentBuiltInAuth()
            .AddHsSqlAgentMcp()
            .AddHsSqlAgentAdminApi()
            .AddHsSqlAgentTelemetry();

        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddProblemDetails();

        return services;
    }

    /// <summary>
    /// Starts composable HsSqlAgent registration without allocating unrelated capability options.
    /// </summary>
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return GetOrCreateCoreBuilder(services, null);
    }

    /// <summary>
    /// Legacy bridge for callers that still configure the aggregate options object.
    /// </summary>
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentCore(
        this IServiceCollection services,
        Action<HsSqlAgentServiceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new HsSqlAgentServiceOptions();
        configure(options);
        return services.AddHsSqlAgentCore(options);
    }

    /// <summary>
    /// Legacy bridge for callers that still provide the aggregate options object.
    /// </summary>
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentCore(
        this IServiceCollection services,
        HsSqlAgentServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        return GetOrCreateCoreBuilder(services, options);
    }

    private static HsSqlAgentRegistrationBuilder GetOrCreateCoreBuilder(
        IServiceCollection services,
        HsSqlAgentServiceOptions? legacyOptions)
    {
        // Module registration can be spread across different NuGet libraries.
        // The dependency-injection collection is the unit of composition:
        // independent fluent builder instances must share the same state.
        var state = services
            .LastOrDefault(d => d.ServiceType == typeof(HsSqlAgentRegistrationState))
            ?.ImplementationInstance as HsSqlAgentRegistrationState;
        if (state is null)
        {
            state = new HsSqlAgentRegistrationState(legacyOptions);
            services.AddSingleton(state);
        }
        else if (legacyOptions is not null && !ReferenceEquals(state.LegacyOptions, legacyOptions))
        {
            throw new InvalidOperationException(
                "HsSqlAgent is already configured for this IServiceCollection. " +
                "Do not reinitialize legacy options from a different module; " +
                "reuse the existing AddHsSqlAgentCore() registration.");
        }

        return new HsSqlAgentRegistrationBuilder(services, state);
    }
}
