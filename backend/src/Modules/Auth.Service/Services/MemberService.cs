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
    {
        return await _context.Members
            .AsNoTracking()
            .OrderBy(x => x.Mail)
            .Select(x => new MemberVM
            {
                Id = x.Id,
                Username = x.Username,
                Mail = x.Mail,
                IsActive = x.IsActive,
                RoleIds = x.MemberRoles.OrderBy(r => r.RoleId).Select(r => r.RoleId).ToArray(),
                Roles = x.MemberRoles.OrderBy(r => r.Role.Name).Select(r => r.Role.Name).ToArray()
            })
            .ToListAsync(cancellationToken);
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
        if (await _context.Members.AnyAsync(x => x.Mail == email, cancellationToken))
        {
            throw new InvalidOperationException("Member email already exists.");
        }

        var member = new Member
        {
            Mail = email,
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
        RoleIds = [.. member.MemberRoles.OrderBy(x => x.RoleId).Select(x => x.RoleId)],
        Roles = [.. member.MemberRoles
            .Where(x => x.Role is not null)
            .OrderBy(x => x.Role.Name)
            .Select(x => x.Role.Name)]
    };
}
