namespace Admin.Service.Data.Entites;

public class DbSemanticRelationship
{
    public int Id { get; set; }
    public int DbManagementId { get; set; }
    public string Name { get; set; } = null!;
    public string? SourceSchema { get; set; }
    public string SourceTable { get; set; } = null!;
    public string SourceColumn { get; set; } = null!;
    public string? TargetSchema { get; set; }
    public string TargetTable { get; set; } = null!;
    public string TargetColumn { get; set; } = null!;
    public string Cardinality { get; set; } = "many-to-one";
    public string Direction { get; set; } = "source-to-target";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DbManagement DbManagement { get; set; } = null!;
}
