
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Interfaces;

public interface IDbSetterService
{
    Task<TestDbConnectionVM> TestDbConnectionAsync(TestDbConnectionRequest request, CancellationToken cancellationToken = default);
    Task<string?> BuildDbConnectionAsync(BuildDbConnectionModel model, CancellationToken cancellationToken = default);
}