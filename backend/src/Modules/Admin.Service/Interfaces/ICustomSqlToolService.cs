using Admin.Service.Data.Entites;
using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface ICustomSqlToolService
{
    Task<List<CustomSqlTool>> GetAllToolsAsync();
    Task<CustomSqlTool?> GetToolByIdAsync(int id);
    Task<CustomSqlTool?> GetToolByNameAsync(string name);
    Task<List<CustomSqlTool>> GetPublishedToolsForDbAsync(int dbManagementId, CancellationToken cancellationToken = default);
    Task<CustomSqlTool?> GetPublishedToolByNameAsync(string name, int dbManagementId, CancellationToken cancellationToken = default);
    Task<List<CustomSqlToolRevision>> GetRevisionsAsync(int toolId, CancellationToken cancellationToken = default);
    Task<CustomSqlToolImpact?> GetImpactAsync(int toolId, CancellationToken cancellationToken = default);
    Task<CustomSqlTool> CreateToolAsync(CustomSqlTool tool);
    Task<CustomSqlTool> UpdateToolAsync(CustomSqlTool tool);
    Task<CustomSqlTool?> PublishAsync(int id, string? actor, CancellationToken cancellationToken = default);
    Task<CustomSqlTool?> DisableAsync(int id, CancellationToken cancellationToken = default);
    Task<CustomSqlTool?> RollbackAsync(int id, int revisionId, string? actor, CancellationToken cancellationToken = default);
    Task<bool> DeleteToolAsync(int id);
}
