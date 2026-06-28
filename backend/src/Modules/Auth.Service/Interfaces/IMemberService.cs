using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IMemberService
{
    Task<int> CreateMemberAsync(CreateMemberRequest request, CancellationToken cancellationToken = default);
    Task<IEnumerable<MemberVM>> GetMembersAsync();
    Task<MemberVM> UpdateMemberRolesAsync(int id, UpdateMemberRolesRequest request);
    Task DeleteMemberAsync(int id);
}