using Admin.Service.Interfaces;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/runtime/operability/doctor")]
public sealed class RuntimeDoctorController(
    IConfiguration configuration,
    IHostEnvironment environment,
    IDbManagementService dbManagementService,
    IMcpAccessKeyService keyService) : ControllerBase
{
    [HttpGet]
    [HasPermission("/runtime/operability", "view")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var databases = await dbManagementService.GetAllDbsAsync(cancellationToken);
        var keys = await keyService.ListKeysAsync(cancellationToken);
        var activeKeys = keys.Where(key => key.IsActive && !key.IsExpired).ToArray();

        return Ok(RuntimeDoctorAnalyzer.Analyze(
            configuration,
            environment.EnvironmentName,
            databases.Count,
            activeKeys.Length,
            activeKeys.Count(key => key.LastUsedAt.HasValue)));
    }
}
