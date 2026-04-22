using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface IDbManagementService
{
    Task<DbManagementVM> CreateDbAsync(DbManagementRequest dbManagement, CancellationToken cancellationToken);
    Task<DbManagementBase?> GetDbByIdAsync(int id, bool isPwd, CancellationToken cancellationToken);
    Task<List<DbManagementVM>> GetAllDbsAsync(CancellationToken cancellationToken);
    Task UpdateDbAsync(int id, DbManagementRequest dbManagement, CancellationToken cancellationToken);
    Task DeleteDbAsync(int id, CancellationToken cancellationToken);
}
