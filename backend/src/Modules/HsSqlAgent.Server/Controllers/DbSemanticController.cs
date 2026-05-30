using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DbSemanticController(IDbSemanticService semanticService) : ControllerBase
{
    [HttpGet("{dbManagementId}")]
    public async Task<ActionResult<List<DbSemanticVM>>> GetByDbId(int dbManagementId)
        => Ok(await semanticService.GetSemanticsByDbIdAsync(dbManagementId));

    [HttpPost]
    public async Task<ActionResult<DbSemanticVM>> Upsert(DbSemanticRequest request)
        => Ok(await semanticService.UpsertSemanticAsync(request));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        await semanticService.DeleteSemanticAsync(id);
        return NoContent();
    }
}
