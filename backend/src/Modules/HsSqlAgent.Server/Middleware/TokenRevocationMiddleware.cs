using System.IdentityModel.Tokens.Jwt;
using Auth.Service.Data;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Auth.Service.Services;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Middleware;

public class TokenRevocationMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ITokenRevocationService revocationService,
        IAuthContext authContext,
        IAuthRuntimeStateCache? authRuntimeStateCache = null)
    {
        // Built-in identity never relies on the host application's default authentication scheme.
        // Authenticate HsSqlAgent's namespaced bearer explicitly before session/revocation checks.
        var builtInAuthentication = await context.AuthenticateAsync(HsSqlAgentAuthenticationSchemes.Bearer);
        if (builtInAuthentication.Succeeded && builtInAuthentication.Principal is not null)
            context.User = builtInAuthentication.Principal;

        var jti = context.User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

        if (!string.IsNullOrWhiteSpace(jti) && await revocationService.IsRevokedAsync(jti))
        {
            await WriteAuthFailureAsync(context, "session_revoked", "This session has been revoked.");
            return;
        }

        var tokenType = context.User.FindFirst(JwtRegisteredClaimNames.Typ)?.Value;
        if (context.User.Identity?.IsAuthenticated == true &&
            (string.Equals(tokenType, "access", StringComparison.Ordinal) ||
             string.Equals(tokenType, "refresh", StringComparison.Ordinal)))
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

            var now = DateTime.UtcNow;
            if (authRuntimeStateCache is not null)
            {
                var authState = await authRuntimeStateCache.GetOrLoadAsync(
                    authContext,
                    memberId,
                    context.RequestAborted);

                if (authState.IsBarrier)
                {
                    await WriteAuthFailureAsync(context, "session_invalid", "Authentication state is changing. Try again.");
                    return;
                }
                if (!authState.Exists)
                {
                    await WriteAuthFailureAsync(context, "session_invalid", "The account no longer exists.");
                    return;
                }
                if (!authState.IsActive)
                {
                    await WriteAuthFailureAsync(context, "account_disabled", "This account has been disabled.");
                    return;
                }
                if (authState.SecurityVersion != tokenSecurityVersion)
                {
                    await WriteAuthFailureAsync(context, "permissions_changed", "Account permissions changed. Sign in again.");
                    return;
                }
                if (!authState.ActiveSessions.Any(session =>
                        session.Id == sessionId &&
                        session.ExpiresAt > now))
                {
                    await WriteAuthFailureAsync(context, "session_expired", "This session expired or was revoked.");
                    return;
                }
            }
            else
            {
                var authState = await authContext.Members
                    .AsNoTracking()
                    .Where(member => member.Id == memberId)
                    .Select(member => new
                    {
                        member.IsActive,
                        member.SecurityVersion,
                        SessionIsActive = member.AuthSessions.Any(session =>
                            session.Id == sessionId &&
                            session.RevokedAt == null &&
                            session.ExpiresAt > now)
                    })
                    .FirstOrDefaultAsync(context.RequestAborted);

                if (authState is null)
                {
                    await WriteAuthFailureAsync(context, "session_invalid", "The account no longer exists.");
                    return;
                }
                if (!authState.IsActive)
                {
                    await WriteAuthFailureAsync(context, "account_disabled", "This account has been disabled.");
                    return;
                }
                if (authState.SecurityVersion != tokenSecurityVersion)
                {
                    await WriteAuthFailureAsync(context, "permissions_changed", "Account permissions changed. Sign in again.");
                    return;
                }
                if (!authState.SessionIsActive)
                {
                    await WriteAuthFailureAsync(context, "session_expired", "This session expired or was revoked.");
                    return;
                }
            }

            var passwordChangeRequired = context.User.FindFirst(AuthService.PasswordChangeRequiredClaim)?.Value;
            var passwordChangePath = context.Request.Path.Equals("/api/auth/account/password", StringComparison.OrdinalIgnoreCase);
            var accountReadPath = HttpMethods.IsGet(context.Request.Method) &&
                                  context.Request.Path.Equals("/api/auth/account", StringComparison.OrdinalIgnoreCase);
            var signOutPath = context.Request.Path.Equals("/api/auth/sign-out", StringComparison.OrdinalIgnoreCase);
            var mfaPath = context.Request.Path.StartsWithSegments("/api/auth/mfa");
            if (string.Equals(passwordChangeRequired, "true", StringComparison.OrdinalIgnoreCase) &&
                !passwordChangePath && !accountReadPath && !mfaPath && !signOutPath)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "password_change_required",
                    message = "Change your password before continuing."
                }, context.RequestAborted);
                return;
            }

            var mfaEnrollmentRequired = context.User.FindFirst(AuthService.MfaEnrollmentRequiredClaim)?.Value;
            if (string.Equals(mfaEnrollmentRequired, "true", StringComparison.OrdinalIgnoreCase) &&
                !mfaPath && !passwordChangePath && !accountReadPath && !signOutPath)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "mfa_enrollment_required",
                    message = "Set up multi-factor authentication before continuing."
                }, context.RequestAborted);
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
