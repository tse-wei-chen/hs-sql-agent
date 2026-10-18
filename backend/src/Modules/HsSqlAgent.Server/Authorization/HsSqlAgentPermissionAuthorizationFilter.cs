using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HsSqlAgent.Server.Authorization;

internal sealed class HsSqlAgentPermissionAuthorizationFilter(
    IHsSqlAgentPermissionAuthorizer authorizer,
    IManagementApiKeyService managementKeys,
    IReadOnlyCollection<string> permissions) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var httpContext = context.HttpContext;
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return;

        // The Management API uses an explicit API-key header, not the human JWT Bearer scheme.
        // Reject malformed or mixed credentials; never silently fall back to human authorization.
        if (httpContext.Request.Headers.TryGetValue("X-API-Key", out var apiKeyHeader))
        {
            if (apiKeyHeader.Count != 1
                || string.IsNullOrWhiteSpace(apiKeyHeader[0])
                || !string.IsNullOrEmpty(httpContext.Request.Headers.Authorization.ToString()))
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            var valid = await managementKeys.ValidateAsync(apiKeyHeader[0]!, httpContext.RequestAborted);
            if (valid is null)
            {
                context.Result = new UnauthorizedResult();
                return;
            }
            var allowed = permissions.Any(p => ManagementApiKeyPermissions.IsGrantable(p)
                && valid.Permissions.Contains(p, StringComparer.Ordinal));
            if (!allowed)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
                return;
            }
            var identity = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, $"management-api-key:{valid.Id}"),
                new Claim(ClaimTypes.NameIdentifier, $"management-api-key:{valid.Id}"),
                new Claim("hsa_actor_type", "management-api-key")
            ], authenticationType: "HsSqlAgent.ManagementApiKey");
            httpContext.User = new ClaimsPrincipal(identity);
            return;
        }

        if (await authorizer.AuthorizeAsync(httpContext, permissions, httpContext.RequestAborted))
            return;

        var scheme = authorizer.AuthenticationScheme;
        var authenticated = httpContext.User.Identity?.IsAuthenticated == true;
        context.Result = authenticated
            ? scheme is null ? new ForbidResult() : new ForbidResult(scheme)
            : scheme is null ? new ChallengeResult() : new ChallengeResult(scheme);
    }
}
