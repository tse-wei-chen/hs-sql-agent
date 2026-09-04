using System.IdentityModel.Tokens.Jwt;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HsSqlAgent.Server.Attributes;

/// <summary>
/// Authenticates one HsSqlAgent-owned scheme directly instead of participating in the host application's
/// authorization-policy provider. This keeps built-in identity endpoints isolated from host defaults and
/// custom IAuthorizationPolicyProvider implementations.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public abstract class HsSqlAgentAuthenticationAttribute(
    string authenticationScheme,
    string? requiredTokenType = null) : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var authentication = await context.HttpContext.AuthenticateAsync(authenticationScheme);
        if (!authentication.Succeeded || authentication.Principal is null)
        {
            context.Result = new ChallengeResult(authenticationScheme);
            return;
        }

        context.HttpContext.User = authentication.Principal;

        if (requiredTokenType is not null &&
            !string.Equals(
                authentication.Principal.FindFirst(JwtRegisteredClaimNames.Typ)?.Value,
                requiredTokenType,
                StringComparison.Ordinal))
        {
            context.Result = new ForbidResult(authenticationScheme);
        }
    }
}

public sealed class AccessAuthorizeAttribute()
    : HsSqlAgentAuthenticationAttribute(HsSqlAgentAuthenticationSchemes.Bearer, "access");

public sealed class MfaChallengeAuthorizeAttribute()
    : HsSqlAgentAuthenticationAttribute(HsSqlAgentAuthenticationSchemes.Bearer, "mfa");

public sealed class ExternalLoginAuthorizeAttribute()
    : HsSqlAgentAuthenticationAttribute(HsSqlAgentAuthenticationSchemes.ExternalCookie);
