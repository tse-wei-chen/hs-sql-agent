using System.Text.Json;
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
    public List<string> Synonyms { get; set; } = [];
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    public static DbSemanticVM FromEntity(DbSemantic s) => new()
    {
        Id = s.Id,
        DbManagementId = s.DbManagementId,
        SchemaName = s.SchemaName,
        TableName = s.TableName,
        ColumnName = s.ColumnName,
        Description = s.Description,
        DisplayName = s.DisplayName,
        Synonyms = SemanticSynonymNormalizer.Deserialize(s.SynonymsJson),
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
    public List<string>? Synonyms { get; set; }
}

public class DbSemanticRelationshipModel
{
    public int Id { get; set; }
    public int DbManagementId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SourceSchema { get; set; }
    public string SourceTable { get; set; } = string.Empty;
    public string SourceColumn { get; set; } = string.Empty;
    public string? TargetSchema { get; set; }
    public string TargetTable { get; set; } = string.Empty;
    public string TargetColumn { get; set; } = string.Empty;
    public string Cardinality { get; set; } = "many-to-one";
    public string Direction { get; set; } = "source-to-target";
    public string? Description { get; set; }
}

public class DbSemanticMetricModel
{
    public int Id { get; set; }
    public int DbManagementId { get; set; }
    public string? SchemaName { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string Formula { get; set; } = string.Empty;
    public string Aggregation { get; set; } = "custom";
    public string? Grain { get; set; }
    public string? Filter { get; set; }
    public List<string>? Synonyms { get; set; }
    public bool Executable { get; init; } = false;
}

public record DbSemanticModel(
    int DbManagementId,
    IReadOnlyList<DbSemanticVM> Entities,
    IReadOnlyList<DbSemanticRelationshipModel> Relationships,
    IReadOnlyList<DbSemanticMetricModel> Metrics);

internal static class SemanticSynonymNormalizer
{
    public static string? Serialize(IEnumerable<string>? values)
    {
        var normalized = values?
            .Select(x => x?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        return normalized is { Length: > 0 } ? JsonSerializer.Serialize(normalized) : null;
    }

    public static List<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
