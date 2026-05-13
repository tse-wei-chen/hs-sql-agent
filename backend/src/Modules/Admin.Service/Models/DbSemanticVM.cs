using System.Linq.Expressions;
using Admin.Service.Data.Entites;

namespace Admin.Service.Models;

public class DbSemanticVM
{
    public int Id { get; set; }
    public int DbManagementId { get; set; }
    public string? SchemaName { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string? ColumnName { get; set; }
    public string? Description { get; set; }
    public string? DisplayName { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    public static Expression<Func<DbSemantic, DbSemanticVM>> Projection => static s => new DbSemanticVM
    {
        Id = s.Id,
        DbManagementId = s.DbManagementId,
        SchemaName = s.SchemaName,
        TableName = s.TableName,
        ColumnName = s.ColumnName,
        Description = s.Description,
        DisplayName = s.DisplayName,
        CreatedAt = s.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        UpdatedAt = s.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss")
    };
}

public class DbSemanticRequest
{
    public int DbManagementId { get; set; }
    public string? SchemaName { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string? ColumnName { get; set; }
    public string? Description { get; set; }
    public string? DisplayName { get; set; }
}
