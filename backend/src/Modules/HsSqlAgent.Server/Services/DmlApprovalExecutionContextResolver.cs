using System.Security.Claims;

namespace HsSqlAgent.Server.Services;

internal static class DmlApprovalExecutionContextResolver
{
    internal static DmlApprovalExecutionContext FromMcp(
        HttpContext? context,
        SqlAgentToolType provider)
    {
        return McpSqlExecutionScope.Capture(context, "execute_dml_sql", provider).ToApprovalContext();
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

        var normalizedDatabaseName = databaseName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDatabaseName))
        {
            throw new UnauthorizedAccessException(
                "DML approval requires a stable database name identity.");
        }

        return new DmlApprovalExecutionContext(
            "admin:" + actor.Trim(),
            "db-management:" + dbManagementId,
            provider,
            normalizedDatabaseName);
    }
}
