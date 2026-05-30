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

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DbManagementController(
    IDbManagementService dbManagementService,
    IAuditService auditService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateDb([FromBody] DbManagementRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        var result = await dbManagementService.CreateDbAsync(request, cancellationToken);
        await auditService.WriteLogAsync("db.management.created", result.Id.ToString(), "success", $"Name: {result.Name}", cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetDbById(int id, CancellationToken cancellationToken)
    {
        var result = await dbManagementService.GetDbByIdAsync(id, false, cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAllDbs(CancellationToken cancellationToken)
        => Ok(await dbManagementService.GetAllDbsAsync(cancellationToken));

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateDb(int id, [FromBody] DbManagementRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        await dbManagementService.UpdateDbAsync(id, request, cancellationToken);
        await auditService.WriteLogAsync("db.management.updated", id.ToString(), "success", $"Name: {request.Name}", cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteDb(int id, CancellationToken cancellationToken)
    {
        await dbManagementService.DeleteDbAsync(id, cancellationToken);
        await auditService.WriteLogAsync("db.management.deleted", id.ToString(), "success", null, cancellationToken);
        return NoContent();
    }

    [HttpGet("{id}/schemas")]
    public async Task<IActionResult> GetSchemas(
        int id,
        [FromServices] ISqlStrategyFactory sqlStrategyFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (await dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType)) return BadRequest("Invalid SqlProvider");

        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(db.PasswordHash, hmacSecret);
        var strategy = sqlStrategyFactory.GetStrategy(dbType);
        var connectionString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
        {
            Host = db.Host,
            Port = db.Port,
            Username = db.Username,
            Password = password,
            Database = db.Database,
            ExtraSettings = db.ExtraSettings
        });

        return Ok(await strategy.GetSchemasAsync(connectionString, cancellationToken));
    }

    [HttpGet("{id}/tables")]
    public async Task<IActionResult> GetTables(
        int id, [FromQuery] string? schema,
        [FromServices] ISqlStrategyFactory sqlStrategyFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (await dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType)) return BadRequest("Invalid SqlProvider");

        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(db.PasswordHash, hmacSecret);
        var strategy = sqlStrategyFactory.GetStrategy(dbType);
        var connectionString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
        {
            Host = db.Host,
            Port = db.Port,
            Username = db.Username,
            Password = password,
            Database = db.Database,
            ExtraSettings = db.ExtraSettings
        });

        return Ok(await strategy.GetTablesAsync(connectionString, schema ?? string.Empty, cancellationToken));
    }

    [HttpGet("{id}/columns")]
    public async Task<IActionResult> GetColumns(
        int id, [FromQuery] string? schema, [FromQuery] string table,
        [FromServices] ISqlStrategyFactory sqlStrategyFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table)) return BadRequest("Table name is required.");
        if (await dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType)) return BadRequest("Invalid SqlProvider");

        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(db.PasswordHash, hmacSecret);
        var strategy = sqlStrategyFactory.GetStrategy(dbType);
        var connectionString = strategy.BuildConnectionString(new BuildDbConnectionModelBase
        {
            Host = db.Host,
            Port = db.Port,
            Username = db.Username,
            Password = password,
            Database = db.Database,
            ExtraSettings = db.ExtraSettings
        });

        return Ok(await strategy.GetColumnsAsync(connectionString, schema ?? string.Empty, table, cancellationToken));
    }
}
