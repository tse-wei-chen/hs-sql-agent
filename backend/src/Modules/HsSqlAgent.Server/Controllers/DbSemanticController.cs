using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DbSemanticController(IDbSemanticService semanticService) : ControllerBase
{
    [HttpGet("{dbManagementId}")]
    [HasPermission("/runtime/db-management/semantic", "view")]
    public async Task<ActionResult<List<DbSemanticVM>>> GetByDbId(int dbManagementId)
        => Ok(await semanticService.GetSemanticsByDbIdAsync(dbManagementId));

    [HttpGet("{dbManagementId}/model")]
    [HasPermission("/runtime/db-management/semantic", "view")]
    public async Task<ActionResult<DbSemanticModel>> GetModel(int dbManagementId)
        => Ok(await semanticService.GetSemanticModelAsync(dbManagementId));

    [HttpPost]
    [HasPermission("/runtime/db-management/semantic", "edit")]
    public async Task<ActionResult<DbSemanticVM>> Upsert(DbSemanticRequest request)
        => Ok(await semanticService.UpsertSemanticAsync(request));

    [HttpDelete("{id}")]
    [HasPermission("/runtime/db-management/semantic", "edit")]
    public async Task<IActionResult> Delete(int id)
    {
        await semanticService.DeleteSemanticAsync(id);
        return NoContent();
    }

    [HttpPost("relationship")]
    [HasPermission("/runtime/db-management/semantic", "edit")]
    public async Task<ActionResult<DbSemanticRelationshipModel>> UpsertRelationship(DbSemanticRelationshipModel model)
        => Ok(await semanticService.UpsertRelationshipAsync(model));

    [HttpDelete("relationship/{id}")]
    [HasPermission("/runtime/db-management/semantic", "edit")]
    public async Task<IActionResult> DeleteRelationship(int id)
    {
        await semanticService.DeleteRelationshipAsync(id);
        return NoContent();
    }

    [HttpPost("metric")]
    [HasPermission("/runtime/db-management/semantic", "edit")]
    public async Task<ActionResult<DbSemanticMetricModel>> UpsertMetric(DbSemanticMetricModel model)
        => Ok(await semanticService.UpsertMetricAsync(model));

    [HttpDelete("metric/{id}")]
    [HasPermission("/runtime/db-management/semantic", "edit")]
    public async Task<IActionResult> DeleteMetric(int id)
    {
        await semanticService.DeleteMetricAsync(id);
        return NoContent();
    }
}
