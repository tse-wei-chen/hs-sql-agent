using System.Collections.Immutable;
using Common.Models;
using Microsoft.AspNetCore.Http;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Request-local authorization scope for all MCP SQL entry points. The middleware owns
/// HttpContext.Items; SQL tools capture and validate its identity and authorization
/// values before resolving a provider, compiling SQL, or starting an approval.
/// </summary>
internal sealed class McpSqlExecutionScope
{
    private McpSqlExecutionScope(
        string toolName,
        int accessKeyId,
        int dbManagementId,
        string databaseName,
        SqlAgentToolType provider,
        ImmutableHashSet<string>? allowedTables,
        ImmutableHashSet<string>? allowedQueryColumns)
    {
        ToolName = toolName;
        AccessKeyId = accessKeyId;
        DbManagementId = dbManagementId;
        DatabaseName = databaseName;
        Provider = provider;
        AllowedTables = allowedTables;
        AllowedQueryColumns = allowedQueryColumns;
    }

    internal string ToolName { get; }
    internal int AccessKeyId { get; }
    internal int DbManagementId { get; }
    internal string DatabaseName { get; }
    internal SqlAgentToolType Provider { get; }

    // Null is the existing explicit-unrestricted whitelist contract; a missing
    // context item is NEVER treated as unrestricted.
    internal IReadOnlySet<string>? AllowedTables { get; }
    // Null means unrestricted, but an empty set is explicitly deny-all.
    internal IReadOnlySet<string>? AllowedQueryColumns { get; }

    internal void EnsureDmlColumnPolicySupported()
    {
        if (AllowedQueryColumns is not null)
            throw new UnauthorizedAccessException(
                "This MCP key restricts query columns; DML is denied until mutation column proofs are available.");
    }

    internal static McpSqlExecutionScope Capture(
        HttpContext? context,
        string toolName,
        SqlAgentToolType provider)
    {
        EnsureToolAllowed(context, toolName);
        // EnsureToolAllowed checks null; repeat the guard for nullable-flow analysis.
        if (context is null)
            throw new UnauthorizedAccessException("MCP tool authorization context is missing.");

        if (!context.Items.TryGetValue(McpContextItemKeys.TableWhitelist, out var tableValue)
            || tableValue is not string tableWhitelist)
            throw new UnauthorizedAccessException("MCP table authorization context is missing.");

        if (!context.Items.TryGetValue(McpContextItemKeys.AccessKeyId, out var keyValue)
            || keyValue is not int accessKeyId || accessKeyId <= 0)
            throw new UnauthorizedAccessException("MCP SQL execution requires a stable access-key identity.");

        if (!context.Items.TryGetValue(McpContextItemKeys.DbManagementId, out var dbValue)
            || dbValue is not int dbManagementId || dbManagementId <= 0)
            throw new UnauthorizedAccessException("MCP SQL execution requires a stable target database identity.");

        if (!context.Items.TryGetValue(McpContextItemKeys.DatabaseName, out var nameValue)
            || nameValue is not string databaseName || string.IsNullOrWhiteSpace(databaseName))
            throw new UnauthorizedAccessException("MCP SQL execution requires a stable database name identity.");

        if (!context.Items.TryGetValue(McpContextItemKeys.SqlProvider, out var providerValue)
            || providerValue is not string providerName
            || !Enum.TryParse<SqlAgentToolType>(providerName, true, out var authenticatedProvider)
            || authenticatedProvider != provider)
            throw new UnauthorizedAccessException("MCP SQL provider does not match its authenticated execution context.");

        var tables = tableWhitelist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowSet = tables.Length == 0
            ? null
            : ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, tables);

        if (!context.Items.TryGetValue(McpContextItemKeys.AllowedQueryColumns, out var columnValue)
            || columnValue is not null and not string)
            throw new UnauthorizedAccessException("MCP query column authorization context is missing or invalid.");

        var allowedColumns = columnValue is string csv
            ? ImmutableHashSet.CreateRange(
                StringComparer.Ordinal,
                csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : null;
        return new McpSqlExecutionScope(
            toolName, accessKeyId, dbManagementId, databaseName.Trim(), provider, allowSet, allowedColumns);
    }

    /// <summary>
    /// Reuse the same tool gate before SQL configuration validation; in particular,
    /// a DML request must not get to validate payloads without tool authorization.
    /// </summary>
    internal static void EnsureToolAllowed(HttpContext? context, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        if (context is null
            || !context.Items.TryGetValue(McpContextItemKeys.AllowedTools, out var value)
            || value is not string allowedTools)
            throw new UnauthorizedAccessException("MCP tool authorization context is missing.");

        if (!string.IsNullOrWhiteSpace(allowedTools)
            && !allowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(toolName, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"API key does not have permission to use tool: {toolName}");
    }

    internal DmlApprovalExecutionContext ToApprovalContext() =>
        new(
            "mcp-key:" + AccessKeyId,
            "db-management:" + DbManagementId,
            Provider,
            DatabaseName);

    /// <summary>
    /// DML preview and commit must re-evaluate current table/tool permissions.
    /// Never allow an approval to switch principals, databases, or providers.
    /// The DML policy-version check separately rejects changed table policies.
    /// </summary>
    internal IReadOnlySet<string>? RefreshAllowedTables(HttpContext? context)
    {
        var latest = Capture(context, ToolName, Provider);
        // Do not allow a pending DML approval to bypass a newly restricted key.
        latest.EnsureDmlColumnPolicySupported();
        if (latest.AccessKeyId != AccessKeyId
            || latest.DbManagementId != DbManagementId
            || !string.Equals(latest.DatabaseName, DatabaseName, StringComparison.Ordinal)
            || latest.Provider != Provider)
            throw new UnauthorizedAccessException(
                "MCP execution identity changed during an authorized SQL operation.");
        return latest.AllowedTables;
    }
}
