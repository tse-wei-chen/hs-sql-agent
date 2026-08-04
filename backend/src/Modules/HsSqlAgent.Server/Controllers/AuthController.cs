using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Auth.Service.Data;
using Admin.Service.Interfaces;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Auth.Service.Services;
using HsSqlAgent.Server.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    ILogger<AuthController> logger,
    IAuthService authService,
    IAuditService auditService,
    ITokenRevocationService tokenRevocationService) : ControllerBase
{
    [HttpGet("first-run")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckFirstRunAsync()
        => Ok(await authService.IsFirstRunAsync());

    [HttpPost("sign-in")]
    [AllowAnonymous]
    public async Task<IActionResult> SignIn([FromBody] SignInRequest request)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var result = await authService.SignInAsync(
                request,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString());
            await auditService.WriteLogAsync("admin.signin", request.Email, "success");
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Sign-in failed for user");
            await auditService.WriteLogAsync("admin.signin", request.Email, "failed", "Invalid credentials");
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid sign-in request.");
            await auditService.WriteLogAsync("admin.signin", request.Email, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("sign-up")]
    [AllowAnonymous]
    public async Task<IActionResult> SignUp([FromBody] SignUpRequest request)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var result = await authService.SignUpFirstAdminAsync(
                request,
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString());
            await auditService.WriteLogAsync("admin.signup", request.Email, "success");
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid sign-up request.");
            await auditService.WriteLogAsync("admin.signup", request.Email, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Sign-up attempt when admin user already exists.");
            await auditService.WriteLogAsync("admin.signup", request.Email, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
    }

    [RefreshAuthorize]
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshTokenAsync(CancellationToken cancellationToken)
    {
        if (User.FindFirstValue(JwtRegisteredClaimNames.Sub) is var id && string.IsNullOrWhiteSpace(id))
            return Unauthorized("User ID is required.");
        if (!int.TryParse(User.FindFirstValue(AuthService.SecurityVersionClaim), out var securityVersion))
            return Unauthorized("Security version is required.");
        if (!Guid.TryParse(User.FindFirstValue(AuthService.SessionIdClaim), out var sessionId))
            return Unauthorized("Session ID is required.");
        var refreshTokenId = User.FindFirstValue(JwtRegisteredClaimNames.Jti);
        if (string.IsNullOrWhiteSpace(refreshTokenId))
            return Unauthorized("Refresh token ID is required.");
        try
        {
            var result = await authService.RefreshTokenAsync(id, securityVersion, sessionId, refreshTokenId, cancellationToken);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Token refresh failed");
            await auditService.WriteLogAsync(
                "admin.sessions.refresh-rejected",
                id,
                "failed",
                ex.Message,
                cancellationToken);
            return Unauthorized(new { code = "session_invalid", message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid token refresh request.");
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("sign-out")]
    public async Task<IActionResult> SignOutAsync([FromBody] SignOutRequest? request)
    {
        var memberIdClaim = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var sessionIdClaim = User.FindFirstValue(AuthService.SessionIdClaim);
        if (int.TryParse(memberIdClaim, out var memberId) && Guid.TryParse(sessionIdClaim, out var sessionId))
            await authService.RevokeSessionAsync(memberId, sessionId, "User signed out.");

        var accessJti = User.FindFirstValue(JwtRegisteredClaimNames.Jti);

        if (!string.IsNullOrWhiteSpace(accessJti))
        {
            var expClaim = User.FindFirstValue(JwtRegisteredClaimNames.Exp);
            if (long.TryParse(expClaim, out var expUnix))
            {
                var expDate = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
                await tokenRevocationService.RevokeAsync(accessJti, expDate);
            }
        }

        if (!string.IsNullOrWhiteSpace(request?.RefreshToken))
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var refreshToken = handler.ReadJwtToken(request.RefreshToken);
                var refreshJti = refreshToken.Claims
                    .FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;

                if (!string.IsNullOrWhiteSpace(refreshJti))
                {
                    await tokenRevocationService.RevokeAsync(refreshJti, refreshToken.ValidTo);
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Invalid refresh token provided during sign-out.");
            }
        }

        await auditService.WriteLogAsync("admin.signout", User.FindFirstValue(JwtRegisteredClaimNames.Email) ?? "unknown", "success");
        return Ok();
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessionsAsync(CancellationToken cancellationToken)
    {
        if (!TryGetSessionIdentity(out var memberId, out var sessionId)) return Unauthorized();
        return Ok(await authService.GetSessionsAsync(memberId, sessionId, cancellationToken));
    }

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!TryGetSessionIdentity(out var memberId, out _)) return Unauthorized();
        try
        {
            await authService.RevokeSessionAsync(memberId, sessionId, "Revoked by user.", cancellationToken);
            await auditService.WriteLogAsync("admin.sessions.revoke", sessionId.ToString(), "success", cancellationToken: cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    [HttpDelete("sessions")]
    public async Task<IActionResult> RevokeOtherSessionsAsync(CancellationToken cancellationToken)
    {
        if (!TryGetSessionIdentity(out var memberId, out var sessionId)) return Unauthorized();
        await authService.RevokeAllSessionsAsync(memberId, sessionId, "Other sessions revoked by user.", cancellationToken);
        await auditService.WriteLogAsync("admin.sessions.revoke-others", memberId.ToString(), "success", cancellationToken: cancellationToken);
        return NoContent();
    }

    private bool TryGetSessionIdentity(out int memberId, out Guid sessionId)
    {
        var hasMember = int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out memberId);
        var hasSession = Guid.TryParse(User.FindFirstValue(AuthService.SessionIdClaim), out sessionId);
        return hasMember && hasSession;
    }
}
