using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IDbSemanticService
{
    Task<List<DbSemanticVM>> GetSemanticsByDbIdAsync(int dbManagementId, CancellationToken cancellationToken = default);
    Task<DbSemanticVM> UpsertSemanticAsync(DbSemanticRequest request, CancellationToken cancellationToken = default);
    Task DeleteSemanticAsync(int id, CancellationToken cancellationToken = default);
}
