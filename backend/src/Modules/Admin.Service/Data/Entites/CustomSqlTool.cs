namespace Admin.Service.Data.Entites;

using System.Text.Json.Serialization;

public class CustomSqlTool
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;

    /// <summary>
    /// SQL template parsed into an AST at execution time. Dynamic values use
    /// unquoted {{parameterName}} placeholders declared in ParametersJson.
    /// </summary>
    public string SqlTemplate { get; set; } = null!;

    /// <summary>
    /// "Query" or "DML"
    /// </summary>
    public string Type { get; set; } = "Query";

    /// <summary>
    /// JSON array of parameter definitions.
    /// Example: [{"Name": "userId", "Type": "int", "Description": "The user ID"}]
    /// </summary>
    public string? ParametersJson { get; set; }

    public int? DbManagementId { get; set; }
    public string Status { get; set; } = CustomSqlToolStatuses.Draft;
    public int? PublishedRevisionId { get; set; }
    public string? PublishedIdentity { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastModifiedAt { get; set; }

    [JsonIgnore] public DbManagement? DbManagement { get; set; }
    [JsonIgnore] public CustomSqlToolRevision? PublishedRevision { get; set; }
    [JsonIgnore] public ICollection<CustomSqlToolRevision> Revisions { get; set; } = [];
}

public static class CustomSqlToolStatuses
{
    public const string Draft = "Draft";
    public const string Published = "Published";
    public const string Disabled = "Disabled";
}
