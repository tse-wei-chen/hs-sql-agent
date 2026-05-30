namespace Admin.Service.Data.Entites;

public class CustomSqlTool
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Description { get; set; } = null!;

    /// <summary>
    /// JSON representation of QueryDefinition or DmlDefinition.
    /// </summary>
    public string DefinitionJson { get; set; } = null!;

    /// <summary>
    /// "Query" or "DML"
    /// </summary>
    public string Type { get; set; } = "Query";

    /// <summary>
    /// JSON array of parameter definitions.
    /// Example: [{"Name": "userId", "Type": "int", "Description": "The user ID"}]
    /// </summary>
    public string? ParametersJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastModifiedAt { get; set; }
}
