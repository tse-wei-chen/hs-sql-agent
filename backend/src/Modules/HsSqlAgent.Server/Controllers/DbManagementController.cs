using System.Text;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Core.Providers;
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
    [HasPermission("/runtime/db-management", "create")]
    public async Task<IActionResult> CreateDb([FromBody] DbManagementRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        if (DbManagementPasswordPolicy.RequiresPassword(request.SqlProvider)
            && string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Password is required for this SQL provider.");
        }
        var result = await dbManagementService.CreateDbAsync(request, cancellationToken);
        await auditService.WriteLogAsync("db.management.created", result.Id.ToString(), "success", $"Name: {result.Name}", cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}")]
    [HasPermission("/runtime/db-management", "view")]
    public async Task<IActionResult> GetDbById(int id, CancellationToken cancellationToken)
    {
        var result = await dbManagementService.GetDbByIdAsync(id, false, cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet]
    [HasPermission("/runtime/db-management", "view")]
    public async Task<IActionResult> GetAllDbs(CancellationToken cancellationToken)
        => Ok(await dbManagementService.GetAllDbsAsync(cancellationToken));

    [HttpPut("{id}")]
    [HasPermission("/runtime/db-management", "edit")]
    public async Task<IActionResult> UpdateDb(int id, [FromBody] DbManagementRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        await dbManagementService.UpdateDbAsync(id, request, cancellationToken);
        await auditService.WriteLogAsync("db.management.updated", id.ToString(), "success", $"Name: {request.Name}", cancellationToken);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [HasPermission("/runtime/db-management", "delete")]
    public async Task<IActionResult> DeleteDb(int id, CancellationToken cancellationToken)
    {
        await dbManagementService.DeleteDbAsync(id, cancellationToken);
        await auditService.WriteLogAsync("db.management.deleted", id.ToString(), "success", null, cancellationToken);
        return NoContent();
    }

    [HttpGet("{id}/schemas")]
    [HasPermission("/runtime/db-management", "view")]
    public async Task<IActionResult> GetSchemas(
        int id,
        [FromServices] ISqlProviderFactory providerFactory,
        [FromServices] ISqlConnectionStringFactory connectionStringFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (await dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType)) return BadRequest("Invalid SqlProvider");

        var connectionString = BuildConnectionString(db, dbType, connectionStringFactory, cryptoService, mcpKeySettings);
        var provider = providerFactory.GetProvider(dbType);
        return Ok(await provider.Metadata.GetSchemasAsync(connectionString, cancellationToken));
    }

    [HttpGet("{id}/tables")]
    [HasPermission("/runtime/db-management", "view")]
    public async Task<IActionResult> GetTables(
        int id, [FromQuery] string? schema,
        [FromServices] ISqlProviderFactory providerFactory,
        [FromServices] ISqlConnectionStringFactory connectionStringFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (await dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType)) return BadRequest("Invalid SqlProvider");

        var connectionString = BuildConnectionString(db, dbType, connectionStringFactory, cryptoService, mcpKeySettings);
        var provider = providerFactory.GetProvider(dbType);
        return Ok(await provider.Metadata.GetTablesAsync(
            connectionString,
            schema ?? string.Empty,
            cancellationToken));
    }

    [HttpGet("{id}/columns")]
    [HasPermission("/runtime/db-management", "view")]
    public async Task<IActionResult> GetColumns(
        int id, [FromQuery] string? schema, [FromQuery] string table,
        [FromServices] ISqlProviderFactory providerFactory,
        [FromServices] ISqlConnectionStringFactory connectionStringFactory,
        [FromServices] ICryptoService cryptoService,
        [FromServices] IOptions<McpKeySettings> mcpKeySettings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(table)) return BadRequest("Table name is required.");
        if (await dbManagementService.GetDbByIdAsync(id, true, cancellationToken) is not DbManagementPwdVM db) return NotFound();
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var dbType)) return BadRequest("Invalid SqlProvider");

        var schemaName = schema ?? string.Empty;
        var connectionString = BuildConnectionString(db, dbType, connectionStringFactory, cryptoService, mcpKeySettings);
        var provider = providerFactory.GetProvider(dbType);
        var metadata = await provider.Metadata.GetColumnsAsync(
            connectionString,
            schemaName,
            table,
            cancellationToken);
        var columns = metadata
            .Select(column => new ColumnInfo(
                column.Name,
                column.Type,
                column.IsPrimaryKey,
                column.PrimaryKeyOrdinal))
            .ToArray();
        return Ok(columns);
    }

    private static string BuildConnectionString(
        DbManagementPwdVM db,
        SqlAgentToolType dbType,
        ISqlConnectionStringFactory connectionStringFactory,
        ICryptoService cryptoService,
        IOptions<McpKeySettings> mcpKeySettings)
    {
        var hmacSecret = Encoding.UTF8.GetBytes(mcpKeySettings.Value.HmacSecretKey);
        var password = cryptoService.DecryptText(db.PasswordHash, hmacSecret);
        return connectionStringFactory.BuildConnectionString(
            dbType,
            new BuildDbConnectionModelBase
            {
                Host = db.Host,
                Port = db.Port,
                Username = db.Username,
                Password = password,
                Database = db.Database,
                ExtraSettings = db.ExtraSettings
            });
    }
}
