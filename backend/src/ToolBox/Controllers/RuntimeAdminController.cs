using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using SqlAgent.Service.Models;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Enums;

namespace ToolBox.Controllers;

[ApiController]
[Authorize]
[Route("api/runtime")]
public class RuntimeAdminController(
    IMcpAccessKeyService keyService,
    IDbSetterService testDbConnection,
    IAuditService auditService) : ControllerBase
{
    private readonly IMcpAccessKeyService _keyService = keyService;
    private readonly IDbSetterService _testDbConnection = testDbConnection;
    private readonly IAuditService _auditService = auditService;

    [HttpGet("mcp-keys")]
    public async Task<IActionResult> ListKeys(CancellationToken cancellationToken)
    {
        var result = await _keyService.ListKeysAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("mcp-keys")]
    public async Task<IActionResult> IssueKey([FromBody] IssueMcpAccessKeyRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Key name is required.");
        }
        var issueMcpAccessKeyModel = new IssueMcpAccessKeyModel
        {
            Name = request.Name,
            ExpiresAt = request.ExpiresAt,
            AllowedTools = request.AllowedTools,
            CorsAllowedOrigins = request.CorsAllowedOrigins,
            SqlProvider = request.SqlProvider,
            SqlConnectionString = await _testDbConnection.BuildDbConnectionAsync(new BuildDbConnectionModel
            {
                Provider = request.SqlProvider ?? SqlAgentToolType.Global.ToString(),
                Host = request.Host,
                Port = request.Port,
                Database = request.Database,
                Username = request.Username,
                Password = request.Password
            }, cancellationToken)
        };
        var actorId = GetActorId();
        var result = await _keyService.IssueKeyAsync(
            issueMcpAccessKeyModel,
            actorId,
            cancellationToken);

        await _auditService.WriteAsync(
            action: "mcp.key.issued",
            target: result.Name,
            result: "success",
            detail: result.KeyPrefix,
            actorType: "admin",
            actorId: actorId,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);

        return Ok(result);
    }

    [HttpPost("mcp-keys/{id:int}/revoke")]
    public async Task<IActionResult> RevokeKey(int id, CancellationToken cancellationToken)
    {
        var actorId = GetActorId();
        var success = await _keyService.RevokeKeyAsync(id, actorId, cancellationToken);
        if (!success)
        {
            return NotFound("MCP key not found.");
        }

        await _auditService.WriteAsync(
            action: "mcp.key.revoked",
            target: id.ToString(),
            result: "success",
            actorType: "admin",
            actorId: actorId,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("mcp-keys/test-db-connection")]
    public async Task<IActionResult> TestDbConnection([FromBody] TestDbConnectionRequest request, CancellationToken cancellationToken)
    {
        var result = await _testDbConnection.TestDbConnectionAsync(request, cancellationToken);
        return Ok(new { success = result.IsSuccess, errorMessage = result.ErrorMessage });
    }

    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? action = null,
        [FromQuery] string? keyword = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _auditService.QueryAsync(page, pageSize, action, keyword, cancellationToken);
        return Ok(result);
    }

    [HttpGet("audit/daily-summary")]
    public async Task<IActionResult> GetAuditDailySummary(
        [FromQuery] int days = 7,
        CancellationToken cancellationToken = default)
    {
        var items = await _auditService.QueryDailySummaryAsync(days, cancellationToken: cancellationToken);
        return Ok(new
        {
            days,
            items
        });
    }

    private string? GetActorId()
    {
        return User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
