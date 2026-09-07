using System.Collections.Frozen;

namespace Common.Models;

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

    public static FrozenSet<string> Names { get; } = new[]
    {
        ExecuteQuerySql,
        GetColumns,
        GetSchemas,
        GetTables,
        ExecuteDmlSql
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
