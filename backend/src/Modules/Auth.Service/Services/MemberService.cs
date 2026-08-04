using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace Auth.Service.Services;

public class MemberService(IAuthContext context) : IMemberService
{
    private readonly IAuthContext _context = context;

    public async Task<IEnumerable<MemberVM>> GetMembersAsync(CancellationToken cancellationToken = default)
        => (await GetMembersAsync(new MemberQuery { PageSize = 100 }, cancellationToken)).Items;

    public async Task<MemberPage> GetMembersAsync(MemberQuery request, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var query = _context.Members.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x => x.Mail.Contains(search) || x.Username.Contains(search));
        }
        if (request.IsActive.HasValue)
            query = query.Where(x => x.IsActive == request.IsActive.Value);
        if (request.RoleId.HasValue)
            query = query.Where(x => x.MemberRoles.Any(r => r.RoleId == request.RoleId.Value));

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .AsNoTracking()
            .OrderBy(x => x.Mail)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new MemberVM
            {
                Id = x.Id,
                Username = x.Username,
                Mail = x.Mail,
                IsActive = x.IsActive,
                RequirePasswordChangeAtNextSignIn = x.RequirePasswordChangeAtNextSignIn,
                CreatedAt = x.CreatedAt,
                LastLoginAt = x.LastLoginAt,
                ActiveSessionCount = x.AuthSessions.Count(s => s.RevokedAt == null && s.ExpiresAt > DateTime.UtcNow),
                RoleIds = x.MemberRoles.OrderBy(r => r.RoleId).Select(r => r.RoleId).ToArray(),
                Roles = x.MemberRoles.OrderBy(r => r.Role.Name).Select(r => r.Role.Name).ToArray()
            })
            .ToListAsync(cancellationToken);
        return new MemberPage { Items = items, TotalCount = totalCount, Page = page, PageSize = pageSize };
    }

    public async Task<MemberVM> GetAccountAsync(int id, CancellationToken cancellationToken = default)
    {
        var member = await _context.Members.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Member not found.");
        return ToViewModel(member);
    }

    public async Task<MemberVM> UpdateAccountAsync(
        int id,
        UpdateAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await _context.Members.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Member not found.");
        var email = request.Email.Trim();
        var normalizedEmail = AuthService.NormalizeEmail(email);
        if (await _context.Members.AnyAsync(
                x => x.Id != id && x.NormalizedMail == normalizedEmail, cancellationToken))
            throw new InvalidOperationException("Member email already exists.");

        member.Username = request.Username.Trim();
        member.Mail = email;
        member.NormalizedMail = normalizedEmail;
        member.SecurityVersion++;
        await RevokeSessionsInternalAsync(id, "Account identity changed.", cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return ToViewModel(member);
    }

    public async Task ChangePasswordAsync(
        int id,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await _context.Members.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Member not found.");
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, member.PasswordHash))
            throw new UnauthorizedAccessException("Current password is incorrect.");
        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, member.PasswordHash))
            throw new ArgumentException("New password must be different from the current password.");

        member.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        member.RequirePasswordChangeAtNextSignIn = false;
        member.SecurityVersion++;
        await RevokeSessionsInternalAsync(id, "Password changed.", cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetPasswordChangeRequiredAsync(
        int id,
        bool required,
        CancellationToken cancellationToken = default)
    {
        var member = await _context.Members.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Member not found.");
        member.RequirePasswordChangeAtNextSignIn = required;
        member.SecurityVersion++;
        await RevokeSessionsInternalAsync(id, "Password change required by administrator.", cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<MemberVM> UpdateMemberRolesAsync(
        int id,
        UpdateMemberRolesRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await _context.Members
            .Include(x => x.MemberRoles)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (member is null) throw new InvalidOperationException("Member not found.");

        var roleIds = request.RoleIds.Distinct().ToArray();
        var validRoleIds = await _context.Roles
            .Where(x => roleIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (validRoleIds.Count != roleIds.Length)
            throw new ArgumentException("One or more roles do not exist.");

        await EnsureSuperUserRemainsAsync(member, validRoleIds, cancellationToken);

        member.MemberRoles.Clear();
        foreach (var roleId in validRoleIds)
        {
            member.MemberRoles.Add(new MemberRole
            {
                MemberId = member.Id,
                RoleId = roleId
            });
        }

        member.SecurityVersion++;
        await _context.SaveChangesAsync(cancellationToken);

        return new MemberVM
        {
            Id = member.Id,
            Username = member.Username,
            Mail = member.Mail,
            IsActive = member.IsActive,
            RoleIds = [.. validRoleIds],
            Roles = await _context.MemberRoles
                .AsNoTracking()
                .Where(x => x.MemberId == member.Id)
                .Select(x => x.Role.Name)
                .OrderBy(x => x)
                .ToArrayAsync(cancellationToken)
        };
    }

    public async Task<MemberVM> UpdateMemberStatusAsync(
        int id,
        UpdateMemberStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var member = await _context.Members
            .Include(x => x.MemberRoles)
            .ThenInclude(x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Member not found.");

        if (member.IsActive == request.IsActive)
            return ToViewModel(member);

        if (!request.IsActive)
            await EnsureAnotherActiveSuperUserAsync(member, cancellationToken);

        member.IsActive = request.IsActive;
        member.SecurityVersion++;
        await _context.SaveChangesAsync(cancellationToken);
        return ToViewModel(member);
    }

    public async Task DeleteMemberAsync(int id, CancellationToken cancellationToken = default)
    {
        var member = await _context.Members
            .Include(x => x.MemberRoles)
            .ThenInclude(x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (member is null) throw new InvalidOperationException("Member not found.");

        await EnsureAnotherActiveSuperUserAsync(member, cancellationToken);

        _context.Members.Remove(member);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CreateMemberAsync(CreateMemberRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim();
        var normalizedEmail = AuthService.NormalizeEmail(email);
        if (await _context.Members.AnyAsync(x => x.NormalizedMail == normalizedEmail ||
                (x.NormalizedMail == null && x.Mail.ToUpper() == normalizedEmail), cancellationToken))
        {
            throw new InvalidOperationException("Member email already exists.");
        }

        var member = new Member
        {
            Mail = email,
            NormalizedMail = normalizedEmail,
            Username = string.IsNullOrWhiteSpace(request.Username) ? email.Split('@')[0] : request.Username.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password.Trim())
        };

        _context.Members.Add(member);

        var roleIds = request.AssignAllRoles
            ? await _context.Roles.Select(x => x.Id).ToListAsync(cancellationToken)
            : request.RoleIds.Distinct().ToList();

        if (roleIds.Count > 0)
        {
            var existingRoleIds = await _context.Roles
                .Where(x => roleIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);

            if (existingRoleIds.Count != roleIds.Count)
                throw new ArgumentException("One or more roles do not exist.");

            foreach (var roleId in existingRoleIds)
            {
                _context.MemberRoles.Add(new MemberRole
                {
                    Member = member,
                    RoleId = roleId
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return member.Id;
    }

    private async Task EnsureSuperUserRemainsAsync(
        Member member,
        IReadOnlyCollection<int> newRoleIds,
        CancellationToken cancellationToken)
    {
        if (!member.IsActive)
            return;

        var superUserRoleId = await _context.Roles
            .Where(x => x.Name == AuthService.SuperUserRoleName)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (!superUserRoleId.HasValue || newRoleIds.Contains(superUserRoleId.Value))
            return;

        var currentlySuperUser = member.MemberRoles.Any(x => x.RoleId == superUserRoleId.Value);
        if (!currentlySuperUser)
            return;

        var anotherExists = await _context.Members
            .AsNoTracking()
            .AnyAsync(x => x.Id != member.Id && x.IsActive &&
                x.MemberRoles.Any(mr => mr.RoleId == superUserRoleId.Value), cancellationToken);

        if (!anotherExists)
            throw new InvalidOperationException("At least one active SuperUser must remain.");
    }

    private async Task EnsureAnotherActiveSuperUserAsync(Member member, CancellationToken cancellationToken)
    {
        if (!member.IsActive || !member.MemberRoles.Any(x =>
                string.Equals(x.Role?.Name, AuthService.SuperUserRoleName, StringComparison.OrdinalIgnoreCase)))
            return;

        var anotherExists = await _context.Members
            .AsNoTracking()
            .AnyAsync(x => x.Id != member.Id && x.IsActive &&
                x.MemberRoles.Any(mr => mr.Role.Name == AuthService.SuperUserRoleName), cancellationToken);

        if (!anotherExists)
            throw new InvalidOperationException("At least one active SuperUser must remain.");
    }

    private static MemberVM ToViewModel(Member member) => new()
    {
        Id = member.Id,
        Username = member.Username,
        Mail = member.Mail,
        IsActive = member.IsActive,
        RequirePasswordChangeAtNextSignIn = member.RequirePasswordChangeAtNextSignIn,
        CreatedAt = member.CreatedAt,
        LastLoginAt = member.LastLoginAt,
        RoleIds = [.. member.MemberRoles.OrderBy(x => x.RoleId).Select(x => x.RoleId)],
        Roles = [.. member.MemberRoles
            .Where(x => x.Role is not null)
            .OrderBy(x => x.Role.Name)
            .Select(x => x.Role.Name)]
    };

    private async Task RevokeSessionsInternalAsync(int memberId, string reason, CancellationToken cancellationToken)
    {
        var sessions = await _context.AuthSessions
            .Where(x => x.MemberId == memberId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
            session.RevocationReason = reason;
        }
    }
}
