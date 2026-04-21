using System.Linq.Expressions;
using Admin.Service.Data.Entites;

namespace Admin.Service.Models;

public class DbManagementVM
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SqlProvider { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string UpdatedBy { get; set; } = string.Empty;

    public static Expression<Func<DbManagement, DbManagementVM>> Projection => static db => new DbManagementVM
    {
        Id = db.Id,
        Name = db.Name,
        SqlProvider = db.SqlProvider ?? string.Empty,
        Host = db.Host ?? string.Empty,
        Port = db.Port ?? string.Empty,
        Username = db.Username ?? string.Empty,
        Database = db.Database ?? string.Empty,
        CreatedAt = db.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        CreatedBy = db.CreatedBy ?? string.Empty,
        UpdatedAt = db.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        UpdatedBy = db.UpdatedBy ?? string.Empty
    };
}