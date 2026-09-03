using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Auth.Service.Services;

public class PasswordResetService(
    IAuthContext context,
    IOptions<PasswordResetSettings> settings,
    IAuthRuntimeStateCache? authRuntimeStateCache = null) : IPasswordResetService
{
    private readonly PasswordResetSettings _settings = settings.Value;
    private readonly IAuthRuntimeStateCache? _authRuntimeStateCache = authRuntimeStateCache;

    public async Task RequestAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var normalized = AuthService.NormalizeEmail(request.Email);
        var member = await context.Members.FirstOrDefaultAsync(x => x.NormalizedMail == normalized && x.IsActive, cancellationToken);
        if (member is null) return;

        var rawToken = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            MemberId = member.Id,
            TokenHash = Hash(rawToken),
            ExpiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.ExpirationMinutes))
        });
        await context.SaveChangesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(_settings.SmtpHost) || string.IsNullOrWhiteSpace(_settings.SmtpFrom)) return;
        var separator = _settings.BaseUrl.Contains('?') ? '&' : '?';
        var link = $"{_settings.BaseUrl}{separator}token={Uri.EscapeDataString(rawToken)}";
        using var message = new MailMessage(_settings.SmtpFrom, member.Mail, "Reset your HS SQL Agent password", $"Use this one-time link before it expires: {link}");
        using var smtp = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort) { EnableSsl = _settings.SmtpEnableSsl };
        if (!string.IsNullOrWhiteSpace(_settings.SmtpUsername))
            smtp.Credentials = new NetworkCredential(_settings.SmtpUsername, _settings.SmtpPassword);
        await smtp.SendMailAsync(message, cancellationToken);
    }

    public async Task ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var tokenHash = Hash(request.Token);
        var token = await context.PasswordResetTokens.Include(x => x.Member)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (token is null || token.UsedAt is not null || token.ExpiresAt <= now)
            throw new ArgumentException("Password reset token is invalid or expired.");

        token.UsedAt = now;
        token.Member.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        token.Member.RequirePasswordChangeAtNextSignIn = false;
        token.Member.SecurityVersion++;
        var sessions = await context.AuthSessions.Where(x => x.MemberId == token.MemberId && x.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var session in sessions) { session.RevokedAt = now; session.RevocationReason = "Password reset."; }

        if (_authRuntimeStateCache is null)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await _authRuntimeStateCache.RunWithBarrierAsync(
                token.MemberId,
                "Password reset revoked authentication state.",
                ct => context.SaveChangesAsync(ct),
                cancellationToken);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
