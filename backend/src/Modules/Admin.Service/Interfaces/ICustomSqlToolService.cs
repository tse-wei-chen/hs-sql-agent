using Admin.Service.Data.Entites;
using Admin.Service.Models;

namespace Admin.Service.Interfaces;

public interface ICustomSqlToolService
{
    Task<List<CustomSqlTool>> GetAllToolsAsync();
    Task<CustomSqlTool?> GetToolByIdAsync(int id);
    Task<CustomSqlTool?> GetToolByNameAsync(string name);
    Task<CustomSqlTool> CreateToolAsync(CustomSqlTool tool);
    Task<CustomSqlTool> UpdateToolAsync(CustomSqlTool tool);
    Task<bool> DeleteToolAsync(int id);
}
