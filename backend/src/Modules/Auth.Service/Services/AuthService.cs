using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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

public class AuthService(
    IAuthContext context,
    IOptions<JwtSettings> jwtSettings,
    IOptions<EnterpriseIdentitySettings>? enterpriseIdentitySettings = null,
    IAuthRuntimeStateCache? authRuntimeStateCache = null) : IAuthService
{
    public const string SuperUserRoleName = "SuperUser";
    public const string SecurityVersionClaim = "security_version";
    public const string SessionIdClaim = "session_id";
    public const string PasswordChangeRequiredClaim = "password_change_required";
    public const string MfaEnrollmentRequiredClaim = "mfa_enrollment_required";

    private readonly IAuthContext _context = context;
    private readonly JwtSettings _jwtSettings = jwtSettings.Value;
    private readonly EnterpriseIdentitySettings _enterpriseIdentitySettings = enterpriseIdentitySettings?.Value ?? new();
    private readonly IAuthRuntimeStateCache? _authRuntimeStateCache = authRuntimeStateCache;

    public async Task<bool> IsFirstRunAsync(CancellationToken cancellationToken = default)
        => !await _context.Members.AnyAsync(cancellationToken);

    public async Task<AuthResult> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default, string? ipAddress = null, string? userAgent = null)
    {
        var email = request.Email.Trim();
        var normalizedEmail = NormalizeEmail(email);
        var password = request.Password.Trim();

        var member = await _context.Members
            .FirstOrDefaultAsync(x => x.NormalizedMail == normalizedEmail ||
                                      (x.NormalizedMail == null && x.Mail.ToUpper() == normalizedEmail), cancellationToken);

        var now = DateTime.UtcNow;
        if (member is null || !member.IsActive || member.LockoutEnd > now)
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        if (!BCrypt.Net.BCrypt.Verify(password, member.PasswordHash))
        {
            member.FailedSignInCount++;
            if (member.FailedSignInCount >= Math.Max(1, _jwtSettings.SignInLockoutThreshold))
            {
                member.LockoutEnd = now.AddMinutes(Math.Max(1, _jwtSettings.SignInLockoutMinutes));
                member.FailedSignInCount = 0;
            }
            await _context.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        member.FailedSignInCount = 0;
        member.LockoutEnd = null;
        member.LastLoginAt = now;
        await _context.SaveChangesAsync(cancellationToken);

        return await BeginMemberSignInAsync(member.Id, cancellationToken, ipAddress, userAgent);
    }

    public async Task<AuthResult> SignUpFirstAdminAsync(SignUpRequest request, CancellationToken cancellationToken = default, string? ipAddress = null, string? userAgent = null)
    {
        if (await _context.Members.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("Admin user already exists. Sign-up is only allowed on first run.");
        }

        var email = request.Email.Trim();
        var member = new Member
        {
            Mail = email,
            NormalizedMail = NormalizeEmail(email),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password.Trim()),
            Username = email.Split('@')[0]
        };

        var superUserRole = await EnsureSuperUserRoleAsync(cancellationToken);

        _context.Members.Add(member);
        _context.MemberRoles.Add(new MemberRole
        {
            Member = member,
            Role = superUserRole
        });

        await _context.SaveChangesAsync(cancellationToken);
        return await BeginMemberSignInAsync(member.Id, cancellationToken, ipAddress, userAgent);
    }

    public async Task<AuthResult> BeginMemberSignInAsync(
        int memberId,
        CancellationToken cancellationToken = default,
        string? ipAddress = null,
        string? userAgent = null)
    {
        var member = await _context.Members.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == memberId, cancellationToken)
            ?? throw new UnauthorizedAccessException("User not found.");
        if (!member.IsActive) throw new UnauthorizedAccessException("Account is disabled.");
        if (member.MfaEnabled)
        {
            return new AuthResult
            {
                UserName = member.Username,
                Email = member.Mail,
                RequiresMfa = true,
                MfaToken = GenerateMfaChallengeToken(member)
            };
        }
        return await CreateSessionAndBuildAuthResultAsync(memberId, ipAddress, userAgent, cancellationToken);
    }

    public async Task<AuthResult> CompleteMfaSignInAsync(
        int memberId,
        int securityVersion,
        CancellationToken cancellationToken = default,
        string? ipAddress = null,
        string? userAgent = null)
    {
        var valid = await _context.Members.AsNoTracking()
            .AnyAsync(x => x.Id == memberId && x.IsActive && x.MfaEnabled && x.SecurityVersion == securityVersion, cancellationToken);
        if (!valid) throw new UnauthorizedAccessException("MFA challenge is no longer valid.");
        return await CreateSessionAndBuildAuthResultAsync(memberId, ipAddress, userAgent, cancellationToken);
    }

    public async Task<AuthResult> RefreshTokenAsync(
        string id,
        int securityVersion,
        Guid sessionId,
        string refreshTokenId,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(id?.Trim(), out var memberId))
        {
            throw new ArgumentException("User ID is required.");
        }

        var member = await _context.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == memberId, cancellationToken);

        if (member is null || !member.IsActive || member.SecurityVersion != securityVersion)
        {
            throw new UnauthorizedAccessException("Session is no longer valid.");
        }

        var session = await _context.AuthSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId && x.MemberId == memberId, cancellationToken);
        var now = DateTime.UtcNow;
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
            throw new UnauthorizedAccessException("Session is no longer valid.");

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(session.CurrentRefreshTokenHash),
                Convert.FromHexString(HashTokenId(refreshTokenId))))
        {
            session.RevokedAt = now;
            session.RevocationReason = "Refresh token reuse detected.";
            await RunSecurityMutationAsync(
                memberId,
                "Refresh token reuse revoked the session.",
                ct => _context.SaveChangesAsync(ct),
                cancellationToken);
            throw new UnauthorizedAccessException("Refresh token has already been used.");
        }

        var nextRefreshTokenId = Guid.NewGuid().ToString();
        session.CurrentRefreshTokenHash = HashTokenId(nextRefreshTokenId);
        session.LastUsedAt = now;
        session.ExpiresAt = now.AddDays(_jwtSettings.RefreshTokenExpirationDays);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new UnauthorizedAccessException("Refresh token has already been used.");
        }
        await InvalidateRuntimeStateAsync(memberId);

        return await BuildAuthResultAsync(memberId, session.Id, nextRefreshTokenId, cancellationToken);
    }

    public async Task<IReadOnlyCollection<AuthSessionVM>> GetSessionsAsync(
        int memberId,
        Guid currentSessionId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        return await _context.AuthSessions
            .AsNoTracking()
            .Where(x => x.MemberId == memberId && x.RevokedAt == null && x.ExpiresAt > now)
            .OrderByDescending(x => x.LastUsedAt)
            .Select(x => new AuthSessionVM
            {
                Id = x.Id,
                IsCurrent = x.Id == currentSessionId,
                CreatedAt = x.CreatedAt,
                LastUsedAt = x.LastUsedAt,
                ExpiresAt = x.ExpiresAt,
                IpAddress = x.IpAddress,
                UserAgent = x.UserAgent
            })
            .ToListAsync(cancellationToken);
    }

    public async Task RevokeSessionAsync(
        int memberId,
        Guid sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var session = await _context.AuthSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId && x.MemberId == memberId, cancellationToken)
            ?? throw new InvalidOperationException("Session not found.");

        if (session.RevokedAt is null)
        {
            session.RevokedAt = DateTime.UtcNow;
            session.RevocationReason = reason;
            await RunSecurityMutationAsync(
                memberId,
                reason,
                ct => _context.SaveChangesAsync(ct),
                cancellationToken);
        }
    }

    public async Task RevokeAllSessionsAsync(
        int memberId,
        Guid? exceptSessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _context.AuthSessions
            .Where(x => x.MemberId == memberId && x.RevokedAt == null &&
                        (!exceptSessionId.HasValue || x.Id != exceptSessionId.Value))
            .ToListAsync(cancellationToken);
        if (sessions.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = reason;
        }
        await RunSecurityMutationAsync(
            memberId,
            reason,
            ct => _context.SaveChangesAsync(ct),
            cancellationToken);
    }

    private async Task<Role> EnsureSuperUserRoleAsync(CancellationToken cancellationToken)
    {
        var role = await _context.Roles.FirstOrDefaultAsync(x => x.Name == SuperUserRoleName, cancellationToken);
        if (role is not null) return role;

        role = new Role
        {
            Name = SuperUserRoleName,
            Description = "Built-in role with unrestricted administrative access."
        };
        _context.Roles.Add(role);
        await _context.SaveChangesAsync(cancellationToken);

        var templates = await _context.PermissionActionTemplates
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var t in templates)
        {
            _context.PermissionActions.Add(new PermissionAction
            {
                RoleId = role.Id,
                PermissionId = t.PermissionId,
                ActionId = t.ActionId
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        return role;
    }

    private async Task<AuthResult> CreateSessionAndBuildAuthResultAsync(
        int memberId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var refreshTokenId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var session = new AuthSession
        {
            CurrentRefreshTokenHash = HashTokenId(refreshTokenId),
            MemberId = memberId,
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = now.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            IpAddress = ipAddress?[..Math.Min(ipAddress.Length, 64)],
            UserAgent = userAgent?[..Math.Min(userAgent.Length, 512)]
        };
        _context.AuthSessions.Add(session);
        await _context.SaveChangesAsync(cancellationToken);
        await InvalidateRuntimeStateAsync(memberId);
        return await BuildAuthResultAsync(memberId, session.Id, refreshTokenId, cancellationToken);
    }

    private async Task<AuthResult> BuildAuthResultAsync(int memberId, Guid sessionId, string refreshTokenId, CancellationToken cancellationToken)
    {
        var member = await _context.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == memberId, cancellationToken)
            ?? throw new UnauthorizedAccessException("User not found.");

        if (!member.IsActive)
            throw new UnauthorizedAccessException("Account is disabled.");

        var roleInfos = await _context.MemberRoles
            .AsNoTracking()
            .Where(x => x.MemberId == memberId)
            .Select(x => new { x.Role.Id, x.Role.Name })
            .Distinct()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var roleIds = roleInfos.Select(r => r.Id).ToList();
        var roleNames = roleInfos.Select(r => r.Name).ToList();

        var permissions = await GetPermissionGrantsAsync(roleIds, cancellationToken);
        var requiresMfaEnrollment = !member.MfaEnabled && roleNames.Any(role =>
            _enterpriseIdentitySettings.RequireMfaForRoles.Contains(role, StringComparer.OrdinalIgnoreCase));

        return new AuthResult
        {
            UserName = member.Username,
            Email = member.Mail,
            Roles = roleNames,
            Permissions = permissions,
            RequiresMfaEnrollment = requiresMfaEnrollment,
            AccessToken = GenerateAccessToken(member.Id, member.Username, member.Mail, member.SecurityVersion, member.RequirePasswordChangeAtNextSignIn, requiresMfaEnrollment, sessionId, roleIds, roleNames),
            RefreshToken = GenerateRefreshToken(member.Id, member.Username, member.Mail, member.SecurityVersion, member.RequirePasswordChangeAtNextSignIn, requiresMfaEnrollment, sessionId, refreshTokenId, roleIds, roleNames)
        };
    }

    private async Task<IReadOnlyCollection<PermissionGrant>> GetPermissionGrantsAsync(
        IReadOnlyCollection<int> roleIds,
        CancellationToken cancellationToken)
    {
        var rows = await _context.PermissionActions
            .AsNoTracking()
            .Where(x => roleIds.Contains(x.RoleId))
            .Select(x => new PermissionActionGrantRow(
                x.Permission.Id,
                x.Permission.Name,
                x.Permission.Path,
                x.Action.Id,
                x.Action.Code,
                x.Action.Name))
            .Distinct()
            .ToListAsync(cancellationToken);

        return [.. rows.GroupBy(x => new { x.Id, x.Name, x.Path })
            .Select(x => new PermissionGrant
            {
                PermissionId = x.Key.Id,
                Name = x.Key.Name,
                Path = x.Key.Path,
                Actions = [.. x
                    .OrderBy(a => a.ActionCode)
                    .Select(a => new ActionGrant
                    {
                        ActionId = a.ActionId,
                        Code = a.ActionCode,
                        Name = a.ActionName
                    })]
            })
            .OrderBy(x => x.Path)];
    }

    private sealed record PermissionActionGrantRow(
        int Id,
        string Name,
        string Path,
        int ActionId,
        string ActionCode,
        string ActionName);

    private string GenerateAccessToken(int memberId, string userName, string email, int securityVersion, bool passwordChangeRequired, bool mfaEnrollmentRequired, Guid sessionId, IReadOnlyCollection<int> roleIds, IReadOnlyCollection<string> roleNames)
        => GenerateToken(memberId, userName, email, securityVersion, passwordChangeRequired, mfaEnrollmentRequired, sessionId, Guid.NewGuid().ToString(), roleIds, roleNames, "access", DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes));

    private string GenerateRefreshToken(int memberId, string userName, string email, int securityVersion, bool passwordChangeRequired, bool mfaEnrollmentRequired, Guid sessionId, string refreshTokenId, IReadOnlyCollection<int> roleIds, IReadOnlyCollection<string> roleNames)
        => GenerateToken(memberId, userName, email, securityVersion, passwordChangeRequired, mfaEnrollmentRequired, sessionId, refreshTokenId, roleIds, roleNames, "refresh", DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays));

    private string GenerateToken(
        int memberId,
        string userName,
        string email,
        int securityVersion,
        bool passwordChangeRequired,
        bool mfaEnrollmentRequired,
        Guid sessionId,
        string tokenId,
        IReadOnlyCollection<int> roleIds,
        IReadOnlyCollection<string> roleNames,
        string tokenType,
        DateTime expires)
    {
        if (string.IsNullOrWhiteSpace(_jwtSettings.SecretKey))
        {
            throw new InvalidOperationException("JwtSettings:SecretKey is required.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Typ, tokenType),
            new(JwtRegisteredClaimNames.Sub, memberId.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, userName),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, tokenId),
            new(SecurityVersionClaim, securityVersion.ToString()),
            new(SessionIdClaim, sessionId.ToString()),
            new(PasswordChangeRequiredClaim, passwordChangeRequired.ToString().ToLowerInvariant()),
            new(MfaEnrollmentRequiredClaim, mfaEnrollmentRequired.ToString().ToLowerInvariant())
        };

        claims.AddRange(roleNames.Select(n => new Claim(ClaimTypes.Role, n)));
        claims.AddRange(roleIds.Select(id => new Claim("role_id", id.ToString())));

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private Task RunSecurityMutationAsync(
        int memberId,
        string reason,
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken) =>
        _authRuntimeStateCache is null
            ? mutation(cancellationToken)
            : _authRuntimeStateCache.RunWithBarrierAsync(
                memberId,
                reason,
                mutation,
                cancellationToken);

    private Task InvalidateRuntimeStateAsync(int memberId) =>
        _authRuntimeStateCache?.InvalidateAsync(memberId, CancellationToken.None)
        ?? Task.CompletedTask;

    private static string HashTokenId(string tokenId)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenId)));

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    private string GenerateMfaChallengeToken(Member member)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Typ, "mfa"),
                new Claim(JwtRegisteredClaimNames.Sub, member.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(SecurityVersionClaim, member.SecurityVersion.ToString())
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
