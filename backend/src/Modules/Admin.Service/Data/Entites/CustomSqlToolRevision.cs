namespace Admin.Service.Data.Entites;

using System.Text.Json.Serialization;

public class CustomSqlToolRevision
{
    public int Id { get; set; }
    public int CustomSqlToolId { get; set; }
    public int RevisionNumber { get; set; }
    public int DbManagementId { get; set; }
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string SqlTemplate { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string? ParametersJson { get; set; }
    public string DiffJson { get; set; } = "{}";
    public string? PublishedBy { get; set; }
    public DateTime PublishedAt { get; set; }

    [JsonIgnore] public CustomSqlTool CustomSqlTool { get; set; } = null!;
    [JsonIgnore] public DbManagement DbManagement { get; set; } = null!;
}
