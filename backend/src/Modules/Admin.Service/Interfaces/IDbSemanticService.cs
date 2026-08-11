using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IDbSemanticService
{
    Task<List<DbSemanticVM>> GetSemanticsByDbIdAsync(int dbManagementId, CancellationToken cancellationToken = default);
    Task<DbSemanticVM> UpsertSemanticAsync(DbSemanticRequest request, CancellationToken cancellationToken = default);
    Task DeleteSemanticAsync(int id, CancellationToken cancellationToken = default);
    Task<DbSemanticModel> GetSemanticModelAsync(int dbManagementId, CancellationToken cancellationToken = default);
    Task<DbSemanticRelationshipModel> UpsertRelationshipAsync(DbSemanticRelationshipModel model, CancellationToken cancellationToken = default);
    Task DeleteRelationshipAsync(int id, CancellationToken cancellationToken = default);
    Task<DbSemanticMetricModel> UpsertMetricAsync(DbSemanticMetricModel model, CancellationToken cancellationToken = default);
    Task DeleteMetricAsync(int id, CancellationToken cancellationToken = default);
}
