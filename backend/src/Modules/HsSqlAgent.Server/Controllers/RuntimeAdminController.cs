using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Models;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Interfaces;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/runtime")]
public class RuntimeAdminController(
    IMcpAccessKeyService keyService,
    IDbSetterService testDbConnection,
    IAuditService auditService,
    IDbManagementService dbManagementService,
    ICryptoService cryptoService,
    IOptions<McpKeySettings> mcpKeySettings,
    IOperabilityService operabilityService,
    IAuditRetentionService auditRetentionService,
    ICustomSqlToolService customSqlToolService) : ControllerBase
{
    private readonly byte[] _hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);

    [HttpGet("mcp-keys")]
    [HasPermission("/runtime/mcp-keys", "view")]
    public async Task<IActionResult> ListKeys(CancellationToken cancellationToken)
    {
        return Ok(await keyService.ListKeysAsync(cancellationToken));
    }

    [HttpGet("mcp-keys/available-tools")]
    [HasPermission("/runtime/mcp-keys", "view")]
    public async Task<IActionResult> ListAvailableTools(
        [FromQuery] int? dbManagementId,
        CancellationToken cancellationToken)
    {
        if (dbManagementId is <= 0)
            return BadRequest("DbManagementId must be a positive value when provided.");

        if (!dbManagementId.HasValue)
            return Ok(McpBuiltInTools.Catalog);

        var customTools = (await customSqlToolService.GetPublishedToolsForDbAsync(dbManagementId.Value, cancellationToken))
            .Select(tool => new McpToolDescriptor(
                tool.Name,
                tool.Type,
                $"Custom: {tool.Name}",
                string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase) ? "high" : "medium",
                false,
                false));

        return Ok(McpBuiltInTools.Catalog.Concat(customTools));
    }

    [HttpPost("mcp-keys")]
    [HasPermission("/runtime/mcp-keys", "create")]
    public async Task<IActionResult> IssueKey([FromBody] IssueMcpAccessKeyRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Key name is required.");

        var result = await keyService.IssueKeyAsync(
            new IssueMcpAccessKeyModel
            {
                Name = request.Name,
                ExpiresAt = request.ExpiresAt,
                AllowedTools = request.AllowedTools,
                CorsAllowedOrigins = request.CorsAllowedOrigins,
                DbManagementId = request.DbManagementId,
                TableWhitelist = request.TableWhitelist,
                RateLimitMode = request.RateLimitMode,
                PermitLimitOverride = request.PermitLimitOverride,
                WindowSecondsOverride = request.WindowSecondsOverride
            },
            User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
            cancellationToken);

        await auditService.WriteLogAsync("mcp.key.issued", result.Name, "success", result.KeyPrefix, cancellationToken: cancellationToken);
        return Ok(result);
    }

    [HttpPost("mcp-keys/{id:int}/revoke")]
    [HasPermission("/runtime/mcp-keys", "revoke")]
    public async Task<IActionResult> RevokeKey(int id, CancellationToken cancellationToken)
    {
        var success = await keyService.RevokeKeyAsync(id,
            User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier),
            cancellationToken);
        if (!success) return NotFound("MCP key not found.");
        await auditService.WriteLogAsync("mcp.key.revoked", id.ToString(), "success", cancellationToken: cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPut("mcp-keys/{id:int}")]
    [HasPermission("/runtime/mcp-keys", "edit")]
    public async Task<IActionResult> UpdateKey(
        int id,
        [FromBody] UpdateMcpAccessKeyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await keyService.UpdateKeyAsync(id, request, ResolveActorId(), cancellationToken);
        if (result is null) return NotFound("MCP key not found.");
        await auditService.WriteLogAsync(
            "mcp.key.updated",
            id.ToString(),
            "success",
            $"Name: {result.Name}",
            cancellationToken);
        return Ok(result);
    }

    [HttpPost("mcp-keys/{id:int}/rotate")]
    [HasPermission("/runtime/mcp-keys", "edit")]
    public async Task<IActionResult> RotateKey(
        int id,
        [FromBody] RotateMcpAccessKeyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await keyService.RotateKeyAsync(id, request, ResolveActorId(), cancellationToken);
        if (result is null) return NotFound("MCP key not found.");
        await auditService.WriteLogAsync(
            "mcp.key.rotated",
            id.ToString(),
            "success",
            $"ReplacementKeyId: {result.Id}; GracePeriodMinutes: {request.GracePeriodMinutes}",
            cancellationToken);
        return Ok(result);
    }

    [HttpPost("mcp-keys/{id:int}/clone")]
    [HasPermission("/runtime/mcp-keys", "create")]
    public async Task<IActionResult> CloneKey(
        int id,
        [FromBody] CloneMcpAccessKeyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await keyService.CloneKeyAsync(id, request, ResolveActorId(), cancellationToken);
        if (result is null) return NotFound("MCP key not found.");
        await auditService.WriteLogAsync(
            "mcp.key.cloned",
            id.ToString(),
            "success",
            $"NewKeyId: {result.Id}; Name: {result.Name}",
            cancellationToken);
        return Ok(result);
    }

    [HttpPost("mcp-keys/test-db-connection")]
    [HasAnyPermission(
        "/runtime/mcp-keys.create",
        "/runtime/db-management.create",
        "/runtime/db-management.edit")]
    public async Task<IActionResult> TestDbConnection([FromBody] TestDbConnectionRequest request, CancellationToken cancellationToken)
    {
        if (request.DbSettingMode == 0)
        {
            if (request.DbManagementId == null)
                return BadRequest("DbManagementId is required when DbSettingMode is Use Existing Connection.");

            var dbc = await dbManagementService.GetDbByIdAsync(request.DbManagementId.Value, true, cancellationToken);
            if (dbc == null)
                return BadRequest($"No DB management entry found for ID {request.DbManagementId.Value}.");

            request.SqlProvider = Enum.TryParse<SqlAgentToolType>(dbc.SqlProvider, out var providerEnum) ? providerEnum : null;
            request.Host = dbc.Host;
            request.Port = dbc.Port;
            request.Username = dbc.Username;
            request.Password = cryptoService.DecryptText(((DbManagementPwdVM)dbc).PasswordHash, _hmacSecret);
            request.Database = dbc.Database;
            request.ExtraSettings = dbc.ExtraSettings;
        }
        var result = await testDbConnection.TestDbConnectionAsync(request, cancellationToken);
        return Ok(new { success = result.IsSuccess, errorMessage = result.ErrorMessage });
    }

    [HasPermission("/runtime/audit", "view")]
    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? action = null, [FromQuery] string? keyword = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null,
        [FromQuery] string? result = null, [FromQuery] string? actor = null,
        [FromQuery] int? dbManagementId = null, [FromQuery] int? accessKeyId = null,
        [FromQuery] string? toolName = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await auditService.QueryAsync(new AuditLogFilter
        {
            Page = page,
            PageSize = pageSize,
            Action = action,
            Keyword = keyword,
            From = from,
            To = to,
            Result = result,
            Actor = actor,
            DbManagementId = dbManagementId,
            AccessKeyId = accessKeyId,
            ToolName = toolName
        }, cancellationToken));
    }

    [HasPermission("/runtime/audit", "view")]
    [HttpGet("audit/daily-summary")]
    public async Task<IActionResult> GetAuditDailySummary([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        var items = await auditService.QueryDailySummaryAsync(days, cancellationToken: cancellationToken);
        return Ok(new { days, items });
    }

    [HasPermission("/runtime/audit", "export")]
    [HttpGet("audit/export")]
    public async Task<IActionResult> ExportAudit(
        [FromQuery] AuditLogFilter filter,
        [FromQuery] string format = "csv",
        CancellationToken cancellationToken = default)
    {
        var items = await auditService.ExportAsync(filter, 100_001, cancellationToken);
        if (items.Count > 100_000)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "Export exceeds 100000 rows. Narrow the current filters and retry." });
        await auditService.WriteLogAsync("audit.exported", format, "success", $"Rows: {items.Count}", cancellationToken);
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return File(JsonSerializer.SerializeToUtf8Bytes(items), "application/json", $"audit-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
        if (!string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)) return BadRequest("Format must be csv or json.");

        var csv = new StringBuilder();
        csv.AppendLine("eventId,createdAt,actorType,actorId,action,target,result,dbManagementId,accessKeyId,toolName,operation,durationMs,returnedRows,affectedRows,approvalStatus,errorCategory,detail,definition");
        foreach (var x in items)
            csv.AppendLine(string.Join(',', new object?[]
            {
                x.EventId, x.CreatedAt.ToString("O"), x.ActorType, x.ActorId, x.Action, x.Target, x.Result,
                x.DbManagementId, x.AccessKeyId, x.ToolName, x.Operation, x.DurationMs, x.ReturnedRows,
                x.AffectedRows, x.ApprovalStatus, x.ErrorCategory, x.Detail, x.Definition
            }.Select(Csv)));
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"audit-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    [HasPermission("/runtime/operability", "view")]
    [HttpGet("operability/metrics")]
    public async Task<IActionResult> GetMetrics([FromQuery] OperabilityFilter filter, CancellationToken cancellationToken)
        => Ok(await operabilityService.GetMetricsAsync(filter, cancellationToken));

    [HasPermission("/runtime/operability", "view")]
    [HttpGet("operability/db-health")]
    public async Task<IActionResult> GetDbHealth(CancellationToken cancellationToken)
        => Ok(await operabilityService.GetDbHealthAsync(cancellationToken));

    [HasPermission("/runtime/operability", "view")]
    [HttpGet("operability/key-usage")]
    public async Task<IActionResult> GetKeyUsage([FromQuery] OperabilityFilter filter, CancellationToken cancellationToken)
        => Ok(await operabilityService.GetKeyUsageAsync(filter, cancellationToken));

    [HasPermission("/runtime/operability", "view")]
    [HttpGet("operability/deliveries")]
    public async Task<IActionResult> GetDeliveries([FromQuery] int limit = 100, CancellationToken cancellationToken = default)
        => Ok(await operabilityService.GetDeliveriesAsync(limit, cancellationToken));

    [HasPermission("/runtime/operability", "edit")]
    [HttpPost("operability/deliveries/{id:long}/retry")]
    public async Task<IActionResult> RetryDelivery(long id, CancellationToken cancellationToken)
        => await operabilityService.RetryDeliveryAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HasPermission("/runtime/audit", "view")]
    [HttpGet("audit/retention")]
    public IActionResult GetRetentionPolicy()
        => Ok(auditRetentionService.GetPolicy());

    [HasPermission("/runtime/audit", "edit")]
    [HttpPost("audit/retention/dry-run")]
    public async Task<IActionResult> DryRunRetention(CancellationToken cancellationToken)
    {
        if (!auditRetentionService.GetPolicy().Enabled)
            return BadRequest("Audit retention is disabled. Set Operability:AuditRetentionDays to a positive value and restart the service.");

        return Ok(await auditRetentionService.ExecuteAsync(true, cancellationToken));
    }

    [HasPermission("/runtime/audit", "edit")]
    [HttpPost("audit/retention/execute")]
    public async Task<IActionResult> ExecuteRetention(CancellationToken cancellationToken)
    {
        if (!auditRetentionService.GetPolicy().Enabled)
            return BadRequest("Audit retention is disabled. Set Operability:AuditRetentionDays to a positive value and restart the service.");

        return Ok(await auditRetentionService.ExecuteAsync(false, cancellationToken));
    }

    private string? ResolveActorId()
        => User.FindFirstValue(JwtRegisteredClaimNames.Sub)
           ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
