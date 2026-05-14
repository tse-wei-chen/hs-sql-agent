using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ToolBox.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DbSemanticController(IDbSemanticService semanticService) : ControllerBase
{
    private readonly IDbSemanticService _semanticService = semanticService;

    [HttpGet("{dbManagementId}")]
    public async Task<ActionResult<List<DbSemanticVM>>> GetByDbId(int dbManagementId)
    {
        var result = await _semanticService.GetSemanticsByDbIdAsync(dbManagementId);
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<DbSemanticVM>> Upsert(DbSemanticRequest request)
    {
        var result = await _semanticService.UpsertSemanticAsync(request);
        return Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        await _semanticService.DeleteSemanticAsync(id);
        return NoContent();
    }
}
