using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface ISecurityPolicyService
{
    Task<SecurityPolicyModel> GetAsync(CancellationToken cancellationToken = default);
    Task<SecurityPolicyModel> UpdateAsync(
        SecurityPolicyModel request,
        string? actorId,
        CancellationToken cancellationToken = default);
}
