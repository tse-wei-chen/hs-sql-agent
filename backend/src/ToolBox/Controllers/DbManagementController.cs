using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Models;

namespace ToolBox.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DbManagementController(
    IDbManagementService dbManagementService,
    IAuditService auditService) : ControllerBase
{
    private readonly IDbManagementService _dbManagementService = dbManagementService;
    private readonly IAuditService _auditService = auditService;

    [HttpPost]
    public async Task<IActionResult> CreateDb([FromBody] DbManagementRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        var result = await _dbManagementService.CreateDbAsync(request, cancellationToken);

        await _auditService.WriteAsync(
            action: "db.management.created",
            target: result.Id.ToString(),
            result: "success",
            detail: $"Created DB management entry with ID {result.Id}",
            actorType: "admin",
            actorId: GetActorId(),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetDbById(int id, CancellationToken cancellationToken)
    {
        var result = await _dbManagementService.GetDbByIdAsync(id, false, cancellationToken);
        if (result == null)
        {
            return NotFound();
        }
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAllDbs(CancellationToken cancellationToken)
    {
        var result = await _dbManagementService.GetAllDbsAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateDb(int id, [FromBody] DbManagementRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        await _dbManagementService.UpdateDbAsync(id, request, cancellationToken);

        await _auditService.WriteAsync(
            action: "db.management.updated",
            target: id.ToString(),
            result: "success",
            detail: $"Updated DB management entry with ID {id}",
            actorType: "admin",
            actorId: GetActorId(),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteDb(int id, CancellationToken cancellationToken)
    {
        await _dbManagementService.DeleteDbAsync(id, cancellationToken);

        await _auditService.WriteAsync(
            action: "db.management.deleted",
            target: id.ToString(),
            result: "success",
            detail: $"Deleted DB management entry with ID {id}",
            actorType: "admin",
            actorId: GetActorId(),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);

        return NoContent();
    }

    [HttpGet("{id}/schemas")]
    public async Task<IActionResult> GetSchemas(
        int id,
        [FromServices] ISqlStrategyFactory sqlStrategyFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<Admin.Service.Models.McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (await _dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();

        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType))
            return BadRequest("Invalid SqlProvider");

        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(db.PasswordHash, hmacSecret);

        var strategy = sqlStrategyFactory.GetStrategy(dbType);
        var connectionString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
        {
            Host = db.Host,
            Port = db.Port,
            Username = db.Username,
            Password = password,
            Database = db.Database
        });

        var schemas = await strategy.GetSchemasAsync(connectionString, cancellationToken);
        return Ok(schemas);
    }

    [HttpGet("{id}/tables")]
    public async Task<IActionResult> GetTables(
        int id,
        [FromQuery] string? schema,
        [FromServices] ISqlStrategyFactory sqlStrategyFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<Admin.Service.Models.McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (await _dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();

        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType))
            return BadRequest("Invalid SqlProvider");

        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(db.PasswordHash, hmacSecret);

        var strategy = sqlStrategyFactory.GetStrategy(dbType);
        var connectionString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
        {
            Host = db.Host,
            Port = db.Port,
            Username = db.Username,
            Password = password,
            Database = db.Database
        });

        var tables = await strategy.GetTablesAsync(connectionString, schema ?? string.Empty, cancellationToken);
        return Ok(tables);
    }

    [HttpGet("{id}/columns")]
    public async Task<IActionResult> GetColumns(
        int id,
        [FromQuery] string? schema,
        [FromQuery] string table,
        [FromServices] ISqlStrategyFactory sqlStrategyFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<Admin.Service.Models.McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table))
            return BadRequest("Table name is required.");

        if (await _dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();

        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType))
            return BadRequest("Invalid SqlProvider");

        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(db.PasswordHash, hmacSecret);

        var strategy = sqlStrategyFactory.GetStrategy(dbType);
        var connectionString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
        {
            Host = db.Host,
            Port = db.Port,
            Username = db.Username,
            Password = password,
            Database = db.Database
        });

        var columns = await strategy.GetColumnsAsync(connectionString, schema ?? string.Empty, table, cancellationToken);
        return Ok(columns);
    }

    private string? GetActorId()
    {
        return User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}