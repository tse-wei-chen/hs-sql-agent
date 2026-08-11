namespace HsSqlAgent.Server.Models;

public class SemanticLayerEntry
{
    public string? SchemaName { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string? ColumnName { get; set; }
    public string? Description { get; set; }
    public string? DisplayName { get; set; }
    public List<string>? Synonyms { get; set; }
}
