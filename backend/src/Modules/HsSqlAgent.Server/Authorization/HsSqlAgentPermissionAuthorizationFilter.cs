using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HsSqlAgent.Server.Authorization;

internal sealed class HsSqlAgentPermissionAuthorizationFilter(
    IHsSqlAgentPermissionAuthorizer authorizer,
    IReadOnlyCollection<string> permissions) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return;

        if (await authorizer.AuthorizeAsync(httpContext, permissions, httpContext.RequestAborted))
            return;

        var scheme = authorizer.AuthenticationScheme;
        var authenticated = httpContext.User.Identity?.IsAuthenticated == true;
        context.Result = authenticated
            ? scheme is null ? new ForbidResult() : new ForbidResult(scheme)
            : scheme is null ? new ChallengeResult() : new ChallengeResult(scheme);
    }
}
