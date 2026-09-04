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
        return new HsSqlAgentRegistrationBuilder(services);
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
        return new HsSqlAgentRegistrationBuilder(services, options);
    }
}
