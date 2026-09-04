using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authentication;

namespace HsSqlAgent.Server.Middleware;

/// <summary>
/// Dispatches only HsSqlAgent's namespaced OIDC remote callback handler. This avoids installing
/// AuthenticationMiddleware across the host's /api surface while still allowing the OpenID Connect
/// handler to consume its callback request.
/// </summary>
internal sealed class HsSqlAgentOidcCallbackMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context, IAuthenticationHandlerProvider handlerProvider)
    {
        var handler = await handlerProvider.GetHandlerAsync(context, HsSqlAgentAuthenticationSchemes.Oidc);
        if (handler is IAuthenticationRequestHandler requestHandler && await requestHandler.HandleRequestAsync())
            return;

        await _next(context);
    }
}
