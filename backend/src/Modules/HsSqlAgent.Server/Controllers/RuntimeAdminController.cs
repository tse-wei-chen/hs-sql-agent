using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/runtime")]
public class RuntimeAdminController(
    IMcpAccessKeyService keyService,
    IDbSetterService testDbConnection,
    IAuditService auditService,
    IDbManagementService dbManagementService,
    ICryptoService cryptoService,
    IOptions<McpKeySettings> mcpKeySettings) : ControllerBase
{
    private readonly byte[] _hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);

    [HttpGet("mcp-keys")]
    [HasPermission("/runtime/mcp-keys", "view")]
    public async Task<IActionResult> ListKeys(CancellationToken cancellationToken)
    {
        return Ok(await keyService.ListKeysAsync(cancellationToken));
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
                TableWhitelist = request.TableWhitelist
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
        CancellationToken cancellationToken = default)
    {
        return Ok(await auditService.QueryAsync(page, pageSize, action, keyword, cancellationToken));
    }

    [HasPermission("/runtime/audit", "view")]
    [HttpGet("audit/daily-summary")]
    public async Task<IActionResult> GetAuditDailySummary([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        var items = await auditService.QueryDailySummaryAsync(days, cancellationToken: cancellationToken);
        return Ok(new { days, items });
    }
}
