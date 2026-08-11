namespace Admin.Service.Data.Entites;

public class DbSemanticMetric
{
    public int Id { get; set; }
    public int DbManagementId { get; set; }
    public string? SchemaName { get; set; }
    public string TableName { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string Formula { get; set; } = null!;
    public string Aggregation { get; set; } = "custom";
    public string? Grain { get; set; }
    public string? Filter { get; set; }
    public string? SynonymsJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DbManagement DbManagement { get; set; } = null!;
}
