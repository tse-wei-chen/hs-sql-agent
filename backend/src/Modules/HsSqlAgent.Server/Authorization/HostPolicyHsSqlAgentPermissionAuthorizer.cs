using Microsoft.AspNetCore.Authorization;

namespace HsSqlAgent.Server.Authorization;

internal sealed record HsSqlAgentHostAuthorizationSettings(string PolicyName);

internal sealed class HostPolicyHsSqlAgentPermissionAuthorizer(
    IAuthorizationService authorizationService,
    HsSqlAgentHostAuthorizationSettings settings) : IHsSqlAgentPermissionAuthorizer
{
    public string? AuthenticationScheme => null;

    public async ValueTask<bool> AuthorizeAsync(
        HttpContext httpContext,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken = default)
    {
        var resource = new HsSqlAgentPermissionResource(httpContext, permissions);
        var result = await authorizationService.AuthorizeAsync(
            httpContext.User,
            resource,
            settings.PolicyName);
        return result.Succeeded;
    }
}

internal sealed class MissingHsSqlAgentPermissionAuthorizer : IHsSqlAgentPermissionAuthorizer
{
    public string? AuthenticationScheme => null;

    public ValueTask<bool> AuthorizeAsync(
        HttpContext httpContext,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(
            "HsSqlAgent Admin API requires an authorization mode. Call AddHsSqlAgentBuiltInAuth() for standalone identity or AddHsSqlAgentHostAuthorization(policyName) to delegate authorization to the host application.");
}
