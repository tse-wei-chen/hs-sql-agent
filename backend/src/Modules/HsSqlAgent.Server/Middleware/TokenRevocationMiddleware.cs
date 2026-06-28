using System.IdentityModel.Tokens.Jwt;
using Auth.Service.Interfaces;

namespace HsSqlAgent.Server.Middleware;

public class TokenRevocationMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context, ITokenRevocationService revocationService)
    {
        var jti = context.User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        if (!string.IsNullOrWhiteSpace(jti) && await revocationService.IsRevokedAsync(jti))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }
}
