using System.Collections.Frozen;

namespace Common.Models;

public sealed record McpToolDescriptor(
    string Name,
    string Type,
    string DisplayName,
    string Risk,
    bool IsBuiltIn,
    bool DefaultSelected);

/// <summary>
/// Canonical public built-in MCP tool surface. Published Custom Tools are database-scoped
/// extensions and are intentionally not part of this catalog.
/// </summary>
public static class McpBuiltInTools
{
    public const string ExecuteQuerySql = "execute_query_sql";
    public const string GetColumns = "get_columns";
    public const string GetSchemas = "get_schemas";
    public const string GetTables = "get_tables";
    public const string ExecuteDmlSql = "execute_dml_sql";
    public const string ExecuteDdlSql = "execute_ddl_sql";

    public static IReadOnlyList<McpToolDescriptor> Catalog { get; } =
    [
        new(GetSchemas, "Query", "Get schemas", "low", true, true),
        new(GetTables, "Query", "Get tables", "low", true, true),
        new(GetColumns, "Query", "Get columns", "low", true, true),
        new(ExecuteQuerySql, "Query", "Execute query", "medium", true, true),
        new(ExecuteDmlSql, "DML", "Execute DML", "high", true, false),
        new(ExecuteDdlSql, "DDL", "Execute DDL", "critical", true, false)
    ];

    public static FrozenSet<string> Names { get; } = Catalog
        .Select(tool => tool.Name)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> DefaultNames { get; } = Catalog
        .Where(tool => tool.DefaultSelected)
        .Select(tool => tool.Name)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // A missing/blank tool selection must not become a wildcard grant when a key
    // is first issued or explicitly edited. Existing legacy null grants remain
    // backward-compatible until the administrator elects to change them.
    public static string DefaultAllowedToolsCsv { get; } =
        string.Join(',', Catalog.Where(tool => tool.DefaultSelected).Select(tool => tool.Name));

    public static string NormalizeExplicitAllowedTools(string? selectedTools)
    {
        var names = selectedTools?.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names is { Length: > 0 }
            ? string.Join(',', names)
            : DefaultAllowedToolsCsv;
    }
}
