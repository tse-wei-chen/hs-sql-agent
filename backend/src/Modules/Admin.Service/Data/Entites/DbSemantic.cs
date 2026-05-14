namespace Admin.Service.Data.Entites;

public class DbSemantic
{
    public int Id { get; set; }
    public int DbManagementId { get; set; }
    
    public string? SchemaName { get; set; }
    public string TableName { get; set; } = null!;
    public string? ColumnName { get; set; }
    
    public string? Description { get; set; }
    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation property
    public DbManagement DbManagement { get; set; } = null!;
}
