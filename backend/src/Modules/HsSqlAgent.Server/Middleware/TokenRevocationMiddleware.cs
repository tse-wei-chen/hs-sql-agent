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
            await WriteAuthFailureAsync(context, "session_revoked", "This session has been revoked.");
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var versionClaim = context.User.FindFirst(AuthService.SecurityVersionClaim)?.Value;
            var sessionClaim = context.User.FindFirst(AuthService.SessionIdClaim)?.Value;
            if (!int.TryParse(subject, out var memberId) ||
                !int.TryParse(versionClaim, out var tokenSecurityVersion) ||
                !Guid.TryParse(sessionClaim, out var sessionId))
            {
                await WriteAuthFailureAsync(context, "invalid_token", "The authentication token is invalid.");
                return;
            }

            var memberState = await authContext.Members
                .AsNoTracking()
                .Where(x => x.Id == memberId)
                .Select(x => new { x.IsActive, x.SecurityVersion })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (memberState is null)
            {
                await WriteAuthFailureAsync(context, "session_invalid", "The account no longer exists.");
                return;
            }
            if (!memberState.IsActive)
            {
                await WriteAuthFailureAsync(context, "account_disabled", "This account has been disabled.");
                return;
            }
            if (memberState.SecurityVersion != tokenSecurityVersion)
            {
                await WriteAuthFailureAsync(context, "permissions_changed", "Account permissions changed. Sign in again.");
                return;
            }

            var now = DateTime.UtcNow;
            var sessionIsActive = await authContext.AuthSessions
                .AsNoTracking()
                .AnyAsync(x => x.Id == sessionId && x.MemberId == memberId &&
                               x.RevokedAt == null && x.ExpiresAt > now,
                    context.RequestAborted);
            if (!sessionIsActive)
            {
                await WriteAuthFailureAsync(context, "session_expired", "This session expired or was revoked.");
                return;
            }
        }

        await _next(context);
    }

    private static async Task WriteAuthFailureAsync(HttpContext context, string code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { code, message }, context.RequestAborted);
    }
}
