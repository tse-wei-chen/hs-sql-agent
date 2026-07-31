using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface ISecurityPolicyChangePublisher
{
    Task PublishAsync(
        SecurityPolicyModel policy,
        CancellationToken cancellationToken = default);
}
