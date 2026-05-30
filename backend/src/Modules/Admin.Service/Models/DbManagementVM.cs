using System.Linq.Expressions;
using Admin.Service.Data.Entites;

namespace Admin.Service.Models;

public class DbManagementBase
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SqlProvider { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public string Port { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Database { get; init; } = string.Empty;
    public string? ExtraSettings { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string CreatedBy { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public string UpdatedBy { get; init; } = string.Empty;
}

public class DbManagementVM : DbManagementBase
{
    public static Expression<Func<DbManagement, DbManagementVM>> Projection => static db => new DbManagementVM
    {
        Id = db.Id,
        Name = db.Name,
        SqlProvider = db.SqlProvider ?? string.Empty,
        Host = db.Host ?? string.Empty,
        Port = db.Port ?? string.Empty,
        Username = db.Username ?? string.Empty,
        Database = db.Database ?? string.Empty,
        ExtraSettings = db.ExtraSettings,
        CreatedAt = db.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        CreatedBy = db.CreatedBy ?? string.Empty,
        UpdatedAt = db.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        UpdatedBy = db.UpdatedBy ?? string.Empty
    };
}

public class DbManagementPwdVM : DbManagementBase
{
    public string PasswordHash { get; set; } = string.Empty;
    public static Expression<Func<DbManagement, DbManagementPwdVM>> Projection => static db => new DbManagementPwdVM
    {
        Id = db.Id,
        Name = db.Name,
        SqlProvider = db.SqlProvider ?? string.Empty,
        Host = db.Host ?? string.Empty,
        Port = db.Port ?? string.Empty,
        Username = db.Username ?? string.Empty,
        PasswordHash = db.PasswordHash ?? string.Empty,
        Database = db.Database ?? string.Empty,
        ExtraSettings = db.ExtraSettings,
        CreatedAt = db.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        CreatedBy = db.CreatedBy ?? string.Empty,
        UpdatedAt = db.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        UpdatedBy = db.UpdatedBy ?? string.Empty
    };
}
