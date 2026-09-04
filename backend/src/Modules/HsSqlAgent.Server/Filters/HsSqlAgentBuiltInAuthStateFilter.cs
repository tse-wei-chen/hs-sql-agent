using Auth.Service.Data;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HsSqlAgent.Server.Filters;

/// <summary>
/// Applies HsSqlAgent built-in token/session state checks only to HsSqlAgent controllers.
/// This intentionally replaces the old /api-wide middleware branch so host API endpoints are never
/// subjected to HsSqlAgent's identity/session lifecycle.
/// </summary>
internal sealed class HsSqlAgentBuiltInAuthStateFilter(
    ITokenRevocationService revocationService,
    IAuthContext authContext,
    IAuthRuntimeStateCache? authRuntimeStateCache = null) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return;

        var authentication = await context.HttpContext.AuthenticateAsync(HsSqlAgentAuthenticationSchemes.Bearer);
        if (!authentication.Succeeded || authentication.Principal is null)
            return;

        context.HttpContext.User = authentication.Principal;

        var allowed = false;
        var gate = new TokenRevocationMiddleware(_ =>
        {
            allowed = true;
            return Task.CompletedTask;
        });

        await gate.InvokeAsync(
            context.HttpContext,
            revocationService,
            authContext,
            authRuntimeStateCache);

        if (!allowed)
            context.Result = new EmptyResult();
    }
}
