using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Auth.Service.Data;
using Admin.Service.Interfaces;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Auth.Service.Services;
using HsSqlAgent.Server.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(
    ILogger<AuthController> logger,
    IAuthService authService,
    IMemberService memberService,
    IPasswordResetService passwordResetService,
    IEnterpriseIdentityService enterpriseIdentityService,
    IMfaService mfaService,
    IOptions<EnterpriseIdentitySettings> enterpriseIdentitySettings,
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

    [AllowAnonymous]
    [HttpGet("oidc/status")]
    public IActionResult GetOidcStatus()
        => Ok(new { enabled = enterpriseIdentitySettings.Value.OidcEnabled });

    [AllowAnonymous]
    [HttpGet("oidc/login")]
    public IActionResult OidcLogin()
    {
        if (!enterpriseIdentitySettings.Value.OidcEnabled) return NotFound("OIDC is not enabled.");
        return Challenge(new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(OidcCallbackAsync))
        }, "oidc");
    }

    [Authorize(Policy = "ExternalLoginPolicy")]
    [HttpGet("oidc/callback")]
    public async Task<IActionResult> OidcCallbackAsync(CancellationToken cancellationToken)
    {
        var settings = enterpriseIdentitySettings.Value;
        var subject = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = User.FindFirstValue(settings.EmailClaim) ?? User.FindFirstValue(ClaimTypes.Email);
        var name = User.FindFirstValue(settings.NameClaim) ?? User.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
            return Unauthorized("OIDC provider did not return required subject and email claims.");
        if (settings.RequireVerifiedEmail &&
            !string.Equals(User.FindFirstValue(settings.EmailVerifiedClaim), "true", StringComparison.OrdinalIgnoreCase))
            return Unauthorized("OIDC provider did not verify the email address.");
        var externalRoles = User.FindAll(settings.RoleClaim).Concat(User.FindAll(ClaimTypes.Role))
            .Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var code = await enterpriseIdentityService.CreateExternalLoginCodeAsync(
            "oidc", subject, email, name, externalRoles, cancellationToken);
        await HttpContext.SignOutAsync("ExternalCookie");
        var separator = settings.FrontendCallbackUrl.Contains('?') ? '&' : '?';
        return Redirect($"{settings.FrontendCallbackUrl}{separator}code={Uri.EscapeDataString(code)}");
    }

    [AllowAnonymous]
    [HttpPost("oidc/exchange")]
    public async Task<IActionResult> ExchangeOidcCodeAsync(ExternalCodeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await enterpriseIdentityService.ExchangeExternalLoginCodeAsync(
                request.Code,
                cancellationToken,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString());
            await auditService.WriteLogAsync("admin.signin.oidc", result.Email ?? "external", "success", cancellationToken: cancellationToken);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "OIDC exchange failed.");
            return Unauthorized(new { code = "external_login_invalid", message = ex.Message });
        }
    }

    [HttpGet("mfa/status")]
    public async Task<IActionResult> GetMfaStatusAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var memberId)) return Unauthorized();
        return Ok(await mfaService.GetStatusAsync(memberId, cancellationToken));
    }

    [HttpPost("mfa/setup")]
    public async Task<IActionResult> BeginMfaSetupAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var memberId)) return Unauthorized();
        try { return Ok(await mfaService.BeginSetupAsync(memberId, cancellationToken)); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("mfa/confirm")]
    public async Task<IActionResult> ConfirmMfaSetupAsync(MfaCodeRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var memberId)) return Unauthorized();
        try
        {
            var recoveryCodes = await mfaService.ConfirmSetupAsync(memberId, request.Code, cancellationToken);
            await auditService.WriteLogAsync("admin.account.mfa.enable", memberId.ToString(), "success", cancellationToken: cancellationToken);
            return Ok(new { recoveryCodes });
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("mfa/disable")]
    public async Task<IActionResult> DisableMfaAsync(MfaCodeRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var memberId)) return Unauthorized();
        try
        {
            await mfaService.DisableAsync(memberId, request.Code, cancellationToken);
            await auditService.WriteLogAsync("admin.account.mfa.disable", memberId.ToString(), "success", cancellationToken: cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
    }

    [Authorize(Policy = "MfaChallengePolicy")]
    [HttpPost("mfa/verify")]
    public async Task<IActionResult> VerifyMfaChallengeAsync(MfaCodeRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var memberId) ||
            !int.TryParse(User.FindFirstValue(AuthService.SecurityVersionClaim), out var securityVersion)) return Unauthorized();
        if (!await mfaService.VerifyAsync(memberId, request.Code, cancellationToken))
            return Unauthorized(new { code = "mfa_invalid", message = "Invalid authenticator or recovery code." });
        var challengeJti = User.FindFirstValue(JwtRegisteredClaimNames.Jti);
        var challengeExp = User.FindFirstValue(JwtRegisteredClaimNames.Exp);
        if (string.IsNullOrWhiteSpace(challengeJti) || !long.TryParse(challengeExp, out var challengeExpUnix)) return Unauthorized();
        await tokenRevocationService.RevokeAsync(
            challengeJti,
            DateTimeOffset.FromUnixTimeSeconds(challengeExpUnix).UtcDateTime,
            cancellationToken);
        var result = await authService.CompleteMfaSignInAsync(
            memberId,
            securityVersion,
            cancellationToken,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString());
        await auditService.WriteLogAsync("admin.signin.mfa", memberId.ToString(), "success", cancellationToken: cancellationToken);
        return Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await passwordResetService.RequestAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Password reset delivery failed.");
        }
        return Accepted(new { message = "If the account exists, password reset instructions will be sent." });
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await passwordResetService.ResetAsync(request, cancellationToken);
            await auditService.WriteLogAsync("admin.account.password.reset", "password-reset", "success", cancellationToken: cancellationToken);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("account")]
    public async Task<IActionResult> GetAccountAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var memberId)) return Unauthorized();
        return Ok(await memberService.GetAccountAsync(memberId, cancellationToken));
    }

    [HttpPut("account")]
    public async Task<IActionResult> UpdateAccountAsync(UpdateAccountRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var memberId)) return Unauthorized();
        try
        {
            var result = await memberService.UpdateAccountAsync(memberId, request, cancellationToken);
            await auditService.WriteLogAsync("admin.account.update", memberId.ToString(), "success", cancellationToken: cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("account/password")]
    public async Task<IActionResult> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var memberId)) return Unauthorized();
        try
        {
            await memberService.ChangePasswordAsync(memberId, request, cancellationToken);
            await auditService.WriteLogAsync("admin.account.password.change", memberId.ToString(), "success", cancellationToken: cancellationToken);
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }
}
