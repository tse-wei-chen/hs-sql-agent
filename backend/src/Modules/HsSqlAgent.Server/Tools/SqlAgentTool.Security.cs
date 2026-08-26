using Admin.Service.Models;
using Common.Models;
using HsSqlAgent.Server.Models;
using HsSqlAgent.SqlCore.Enums;

namespace HsSqlAgent.Server.Tools;

public partial class SqlAgentTool
{
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
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return;
        var allowedTools = context.Items[McpContextItemKeys.AllowedTools] as string;
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
        var context = _httpContextAccessor.HttpContext;
        if (context == null) return null;
        var tableWhitelist = context.Items[McpContextItemKeys.TableWhitelist] as string;
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
