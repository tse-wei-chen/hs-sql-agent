using Auth.Service.Data;
using Auth.Service.Data.Entites;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace Auth.Service.Services;

public class MemberService(IAuthContext context) : IMemberService
{
    private readonly IAuthContext _context = context;

    public async Task<IEnumerable<MemberVM>> GetMembersAsync()
    {
        return await _context.Members
            .AsNoTracking()
            .OrderBy(x => x.Mail)
            .Select(x => new MemberVM
            {
                Id = x.Id,
                Username = x.Username,
                Mail = x.Mail,
                RoleIds = x.MemberRoles.OrderBy(r => r.RoleId).Select(r => r.RoleId).ToArray(),
                Roles = x.MemberRoles.OrderBy(r => r.Role.Name).Select(r => r.Role.Name).ToArray()
            })
            .ToListAsync();
    }

    public async Task<MemberVM> UpdateMemberRolesAsync(int id, UpdateMemberRolesRequest request)
    {
        var member = await _context.Members
            .Include(x => x.MemberRoles)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (member is null) throw new InvalidOperationException("Member not found.");

        var roleIds = request.RoleIds.Distinct().ToArray();
        var validRoleIds = await _context.Roles
            .Where(x => roleIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync();

        member.MemberRoles.Clear();
        foreach (var roleId in validRoleIds)
        {
            member.MemberRoles.Add(new MemberRole
            {
                MemberId = member.Id,
                RoleId = roleId
            });
        }

        await _context.SaveChangesAsync();

        return new MemberVM
        {
            Id = member.Id,
            Username = member.Username,
            Mail = member.Mail,
            RoleIds = [.. validRoleIds],
            Roles = await _context.MemberRoles
                .AsNoTracking()
                .Where(x => x.MemberId == member.Id)
                .Select(x => x.Role.Name)
                .OrderBy(x => x)
                .ToArrayAsync()
        };
    }

    public async Task DeleteMemberAsync(int id)
    {
        var member = await _context.Members.FirstOrDefaultAsync(x => x.Id == id);
        if (member is null) throw new InvalidOperationException("Member not found.");

        _context.Members.Remove(member);
        await _context.SaveChangesAsync();
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
}
