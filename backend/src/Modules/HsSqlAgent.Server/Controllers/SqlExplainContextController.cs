using Admin.Service.Interfaces;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/runtime/security/sql-explain")]
public sealed class SqlExplainContextController(
    IDbManagementService dbManagementService,
    IMcpAccessKeyService keyService) : ControllerBase
{
    [HttpGet("context")]
    [HasPermission("/runtime/security", "view")]
    public async Task<IActionResult> GetContext(CancellationToken cancellationToken)
    {
        var databases = await dbManagementService.GetAllDbsAsync(cancellationToken);
        var keys = await keyService.ListKeysAsync(cancellationToken);

        return Ok(new
        {
            databases = databases
                .OrderBy(database => database.Name, StringComparer.OrdinalIgnoreCase)
                .Select(database => new
                {
                    database.Id,
                    database.Name,
                    database.SqlProvider
                })
                .ToArray(),
            accessKeys = keys
                .Where(key => key.DbManagementId.HasValue)
                .OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase)
                .Select(key => new
                {
                    key.Id,
                    key.Name,
                    key.DbManagementId,
                    key.IsActive,
                    key.IsExpired,
                    key.AllowedTools,
                    key.TableWhitelist
                })
                .ToArray()
        });
    }
}
