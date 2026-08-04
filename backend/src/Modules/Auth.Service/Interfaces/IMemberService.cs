using Auth.Service.Models;

namespace Auth.Service.Interfaces;

public interface IMemberService
{
    Task<int> CreateMemberAsync(CreateMemberRequest request, CancellationToken cancellationToken = default);
    Task<MemberPage> GetMembersAsync(MemberQuery query, CancellationToken cancellationToken = default);
    Task<IEnumerable<MemberVM>> GetMembersAsync(CancellationToken cancellationToken = default);
    Task<MemberVM> GetAccountAsync(int id, CancellationToken cancellationToken = default);
    Task<MemberVM> UpdateAccountAsync(int id, UpdateAccountRequest request, CancellationToken cancellationToken = default);
    Task ChangePasswordAsync(int id, ChangePasswordRequest request, CancellationToken cancellationToken = default);
    Task SetPasswordChangeRequiredAsync(int id, bool required, CancellationToken cancellationToken = default);
    Task<MemberVM> UpdateMemberRolesAsync(int id, UpdateMemberRolesRequest request, CancellationToken cancellationToken = default);
    Task<MemberVM> UpdateMemberStatusAsync(int id, UpdateMemberStatusRequest request, CancellationToken cancellationToken = default);
    Task DeleteMemberAsync(int id, CancellationToken cancellationToken = default);
}
