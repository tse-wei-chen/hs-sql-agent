using Admin.Service.Models;
using Common.Models;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
    private const string InvalidSqlConfigurationMessage =
        "Invalid database provider or connection configuration.";

    private static bool CheckProviderAndConnectionString(
        SqlRuntimeConfig sqlConfig,
        out SqlAgentToolType dbType)
    {
        dbType = default;
        if (string.IsNullOrEmpty(sqlConfig.Provider) || string.IsNullOrEmpty(sqlConfig.ConnectionString))
            return false;
        return Enum.TryParse(sqlConfig.Provider, true, out dbType);
    }

    private Task<SqlRuntimeConfig> ResolveSqlConfigAsync()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is not null)
        {
            var provider = context.Items[McpContextItemKeys.SqlProvider]?.ToString();
            var connectionString = context.Items[McpContextItemKeys.SqlConnectionString]?.ToString();
            if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(connectionString))
            {
                return Task.FromResult(new SqlRuntimeConfig
                {
                    Provider = provider,
                    ConnectionString = connectionString
                });
            }
        }
        return Task.FromResult(new SqlRuntimeConfig());
    }

    private void ValidateToolAccess(string? toolName = null)
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new UnauthorizedAccessException("MCP tool authorization context is missing.");
        if (!context.Items.TryGetValue(McpContextItemKeys.AllowedTools, out var allowedToolsValue))
        {
            throw new UnauthorizedAccessException("MCP tool authorization context is missing.");
        }

        var allowedTools = allowedToolsValue?.ToString();
        if (string.IsNullOrWhiteSpace(allowedTools)) return;
        var isAllowed = allowedTools
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (!isAllowed)
            throw new UnauthorizedAccessException($"API key does not have permission to use tool: {toolName}");
    }

    private int? ResolveDbManagementId()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context != null
            && context.Items.TryGetValue(McpContextItemKeys.DbManagementId, out var idObj)
            && idObj is int id)
            return id;
        return null;
    }

    private HashSet<string>? ResolveTableWhitelist()
    {
        var context = _httpContextAccessor.HttpContext
            ?? throw new UnauthorizedAccessException("MCP table authorization context is missing.");
        if (!context.Items.TryGetValue(McpContextItemKeys.TableWhitelist, out var whitelistValue))
        {
            throw new UnauthorizedAccessException("MCP table authorization context is missing.");
        }

        var tableWhitelist = whitelistValue?.ToString();
        if (string.IsNullOrWhiteSpace(tableWhitelist)) return null;
        return tableWhitelist
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureTableAllowed(string tableName)
    {
        var whitelist = ResolveTableWhitelist();
        if (whitelist is null or { Count: 0 }) return;
        if (!whitelist.Contains(tableName))
            throw new UnauthorizedAccessException(
                $"API key does not have permission to access table: {tableName}");
    }
}
