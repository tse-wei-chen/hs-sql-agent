using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Auth.Service.Services;

public class AuthService(IAuthContext context, IOptions<JwtSettings> jwtSettings) : IAuthService
{
    public const string SuperUserRoleName = "SuperUser";

    private readonly IAuthContext _context = context;
    private readonly JwtSettings _jwtSettings = jwtSettings.Value;

    public async Task<bool> IsFirstRunAsync(CancellationToken cancellationToken = default)
        => !await _context.Members.AnyAsync(cancellationToken);

    public async Task<AuthResult> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim();
        var password = request.Password.Trim();

        var member = await _context.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Mail == email, cancellationToken);

        if (member is null || !BCrypt.Net.BCrypt.Verify(password, member.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        return await BuildAuthResultAsync(member.Id, cancellationToken);
    }

    public async Task<AuthResult> SignUpFirstAdminAsync(SignUpRequest request, CancellationToken cancellationToken = default)
    {
        if (await _context.Members.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("Admin user already exists. Sign-up is only allowed on first run.");
        }

        var email = request.Email.Trim();
        var member = new Member
        {
            Mail = email,
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
        return await BuildAuthResultAsync(member.Id, cancellationToken);
    }

    public async Task<AuthResult> RefreshTokenAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(id?.Trim(), out var memberId))
        {
            throw new ArgumentException("User ID is required.");
        }

        if (!await _context.Members.AnyAsync(x => x.Id == memberId, cancellationToken))
        {
            throw new UnauthorizedAccessException("User not found.");
        }

        return await BuildAuthResultAsync(memberId, cancellationToken);
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

    private async Task<AuthResult> BuildAuthResultAsync(int memberId, CancellationToken cancellationToken)
    {
        var member = await _context.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == memberId, cancellationToken)
            ?? throw new UnauthorizedAccessException("User not found.");

        var roles = await _context.MemberRoles
            .AsNoTracking()
            .Where(x => x.MemberId == memberId)
            .Select(x => x.Role.Name)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var permissions = await GetPermissionGrantsAsync(roles, cancellationToken);

        return new AuthResult
        {
            UserName = member.Username,
            Email = member.Mail,
            Roles = roles,
            Permissions = permissions,
            AccessToken = GenerateAccessToken(member.Id, member.Username, member.Mail, roles),
            RefreshToken = GenerateRefreshToken(member.Id, member.Username, member.Mail, roles)
        };
    }

    private async Task<IReadOnlyCollection<PermissionGrant>> GetPermissionGrantsAsync(
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        var rows = await _context.PermissionActions
            .AsNoTracking()
            .Where(x => roles.Contains(x.Role.Name))
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

    private string GenerateAccessToken(int memberId, string userName, string email, IReadOnlyCollection<string> roles)
        => GenerateToken(memberId, userName, email, roles, "access", DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes));

    private string GenerateRefreshToken(int memberId, string userName, string email, IReadOnlyCollection<string> roles)
        => GenerateToken(memberId, userName, email, roles, "refresh", DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays));

    private string GenerateToken(
        int memberId,
        string userName,
        string email,
        IReadOnlyCollection<string> roles,
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
            new(JwtRegisteredClaimNames.Email, email)
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
