using System.IdentityModel.Tokens.Jwt;
using Auth.Service.Data;
using Auth.Service.Interfaces;
using Auth.Service.Services;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Middleware;

public class TokenRevocationMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ITokenRevocationService revocationService,
        IAuthContext authContext)
    {
        var jti = context.User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        if (!string.IsNullOrWhiteSpace(jti) && await revocationService.IsRevokedAsync(jti))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var versionClaim = context.User.FindFirst(AuthService.SecurityVersionClaim)?.Value;
            if (!int.TryParse(subject, out var memberId) ||
                !int.TryParse(versionClaim, out var tokenSecurityVersion))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var memberState = await authContext.Members
                .AsNoTracking()
                .Where(x => x.Id == memberId)
                .Select(x => new { x.IsActive, x.SecurityVersion })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (memberState is null || !memberState.IsActive ||
                memberState.SecurityVersion != tokenSecurityVersion)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await _next(context);
    }
}
