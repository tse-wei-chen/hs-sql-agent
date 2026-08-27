using System.Security.Claims;
using Common.Models;

namespace HsSqlAgent.Server.Services;

internal static class DmlApprovalExecutionContextResolver
{
    internal static DmlApprovalExecutionContext FromMcp(
        HttpContext? context,
        SqlAgentToolType provider)
    {
        if (context is null)
        {
            throw new UnauthorizedAccessException(
                "DML approval requires an authenticated MCP execution context.");
        }

        if (!context.Items.TryGetValue(McpContextItemKeys.AccessKeyId, out var keyValue)
            || keyValue is not int accessKeyId)
        {
            throw new UnauthorizedAccessException(
                "DML approval requires a stable MCP access-key identity.");
        }

        if (!context.Items.TryGetValue(McpContextItemKeys.DbManagementId, out var dbValue)
            || dbValue is not int dbManagementId)
        {
            throw new UnauthorizedAccessException(
                "DML approval requires a stable target database identity.");
        }

        var databaseName =
            context.Items[McpContextItemKeys.DatabaseName]?.ToString()?.Trim()
            ?? string.Empty;

        return new DmlApprovalExecutionContext(
            "mcp-key:" + accessKeyId,
            "db-management:" + dbManagementId,
            provider,
            databaseName);
    }

    internal static DmlApprovalExecutionContext FromAdmin(
        ClaimsPrincipal principal,
        int dbManagementId,
        SqlAgentToolType provider,
        string? databaseName)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var actor =
            principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new UnauthorizedAccessException(
                "DML approval preview requires an authenticated admin principal.");
        }

        return new DmlApprovalExecutionContext(
            "admin:" + actor.Trim(),
            "db-management:" + dbManagementId,
            provider,
            databaseName?.Trim() ?? string.Empty);
    }
}
