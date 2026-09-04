using HsSqlAgent.Server.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HsSqlAgent.Server.Extensions;

public static class HsSqlAgentHostAuthorizationServiceExtensions
{
    /// <summary>
    /// Delegates HsSqlAgent administration permission checks to an existing ASP.NET Core authorization policy.
    /// Canonical HsSqlAgent permission keys are supplied to the host policy as HsSqlAgentPermissionResource.
    /// </summary>
    public static HsSqlAgentRegistrationBuilder AddHsSqlAgentHostAuthorization(
        this HsSqlAgentRegistrationBuilder builder,
        string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (builder.IsRegistered("built-in-auth"))
        {
            throw new InvalidOperationException(
                "HsSqlAgent host authorization and built-in authentication are mutually exclusive authorization modes.");
        }
        if (!builder.TryRegister("host-authorization")) return builder;

        var services = builder.Services;
        services.AddAuthorization();
        services.AddSingleton(new HsSqlAgentHostAuthorizationSettings(policyName));
        services.RemoveAll<IHsSqlAgentPermissionAuthorizer>();
        services.AddScoped<IHsSqlAgentPermissionAuthorizer, HostPolicyHsSqlAgentPermissionAuthorizer>();
        return builder;
    }
}
