using HsSqlAgent.Server.Models;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentServiceExtensions
{
    /// <summary>
    /// Registers the full standalone HsSqlAgent server preset. Existing callers keep the same behavior,
    /// while embedders can opt into individual capabilities through AddHsSqlAgentCore().
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
    /// Registers the full standalone HsSqlAgent server preset.
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

        return services;
    }

    /// <summary>
    /// Starts a composable HsSqlAgent registration. Core registration intentionally does not install
    /// authentication, MVC, MCP, telemetry exporters, background services, or the admin store.
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
    /// Starts a composable HsSqlAgent registration using an existing options object.
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
