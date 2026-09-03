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

public class EnterpriseIdentityService(
    IAuthContext context,
    IAuthService authService,
    IOptions<EnterpriseIdentitySettings> settings,
    IAuthRuntimeStateCache? authRuntimeStateCache = null) : IEnterpriseIdentityService
{
    private readonly EnterpriseIdentitySettings _settings = settings.Value;
    private readonly IAuthRuntimeStateCache? _authRuntimeStateCache = authRuntimeStateCache;

    public async Task<string> CreateExternalLoginCodeAsync(
        string provider,
        string subject,
        string email,
        string? name,
        IReadOnlyCollection<string> externalRoles,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
            throw new UnauthorizedAccessException("OIDC subject and email claims are required.");

        var identity = await context.ExternalIdentities.Include(x => x.Member)
            .FirstOrDefaultAsync(x => x.Provider == provider && x.Subject == subject, cancellationToken);
        Member member;
        if (identity is not null)
        {
            member = identity.Member;
        }
        else
        {
            var normalizedEmail = AuthService.NormalizeEmail(email);
            member = await context.Members.FirstOrDefaultAsync(x => x.NormalizedMail == normalizedEmail, cancellationToken)
                ?? await ProvisionMemberAsync(email, name, cancellationToken);
            context.ExternalIdentities.Add(new ExternalIdentity { Member = member, Provider = provider, Subject = subject });
        }

        if (!member.IsActive) throw new UnauthorizedAccessException("Account is disabled.");
        var rolesChanged = await ApplyMappedRolesAsync(member, externalRoles, cancellationToken);

        var rawCode = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        context.ExternalLoginCodes.Add(new ExternalLoginCode
        {
            Member = member,
            CodeHash = Hash(rawCode),
            ExpiresAt = DateTime.UtcNow.AddMinutes(Math.Max(1, _settings.LoginCodeExpirationMinutes))
        });
        if (rolesChanged && _authRuntimeStateCache is not null)
        {
            await _authRuntimeStateCache.RunWithBarrierAsync(
                member.Id,
                "Enterprise identity role mapping changed.",
                ct => context.SaveChangesAsync(ct),
                cancellationToken);
        }
        else
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        return rawCode;
    }

    public async Task<AuthResult> ExchangeExternalLoginCodeAsync(
        string code,
        CancellationToken cancellationToken = default,
        string? ipAddress = null,
        string? userAgent = null)
    {
        var now = DateTime.UtcNow;
        var hash = Hash(code);
        var loginCode = await context.ExternalLoginCodes.FirstOrDefaultAsync(x => x.CodeHash == hash, cancellationToken);
        if (loginCode is null || loginCode.UsedAt is not null || loginCode.ExpiresAt <= now)
            throw new UnauthorizedAccessException("External login code is invalid or expired.");
        loginCode.UsedAt = now;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new UnauthorizedAccessException("External login code has already been used."); }
        return await authService.BeginMemberSignInAsync(loginCode.MemberId, cancellationToken, ipAddress, userAgent);
    }

    private async Task<Member> ProvisionMemberAsync(string email, string? name, CancellationToken cancellationToken)
    {
        if (!_settings.AutoProvision) throw new UnauthorizedAccessException("Account is not provisioned.");
        var member = new Member
        {
            Mail = email.Trim(),
            NormalizedMail = AuthService.NormalizeEmail(email),
            Username = string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48)))
        };
        context.Members.Add(member);
        await context.SaveChangesAsync(cancellationToken);
        return member;
    }

    private async Task<bool> ApplyMappedRolesAsync(Member member, IReadOnlyCollection<string> externalRoles, CancellationToken cancellationToken)
    {
        var requestedNames = externalRoles
            .Select(role => _settings.RoleMappings.TryGetValue(role, out var mapped) ? mapped : null)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Concat(_settings.DefaultRoleNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requestedNames.Length == 0) return false;
        var roleIds = await context.Roles.Where(x => requestedNames.Contains(x.Name)).Select(x => x.Id).ToListAsync(cancellationToken);
        var existingRoleIds = await context.MemberRoles.Where(x => x.MemberId == member.Id).Select(x => x.RoleId).ToListAsync(cancellationToken);
        var addedRoleIds = roleIds.Except(existingRoleIds).ToArray();
        foreach (var roleId in addedRoleIds) context.MemberRoles.Add(new MemberRole { Member = member, RoleId = roleId });
        if (addedRoleIds.Length > 0) member.SecurityVersion++;
        return addedRoleIds.Length > 0;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
