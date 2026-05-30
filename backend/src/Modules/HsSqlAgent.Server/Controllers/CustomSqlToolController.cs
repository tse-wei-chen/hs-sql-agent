using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CustomSqlToolController(ICustomSqlToolService toolService, IAuditService auditService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAllTools()
        => Ok(await toolService.GetAllToolsAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTool(int id)
    {
        var tool = await toolService.GetToolByIdAsync(id);
        return tool == null ? NotFound() : Ok(tool);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTool([FromBody] CustomSqlTool tool)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var created = await toolService.CreateToolAsync(tool);
        await auditService.WriteLogAsync("tool.custom.created", created.Id.ToString(), "success", $"Name: {created.Name}");
        return CreatedAtAction(nameof(GetTool), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateTool(int id, [FromBody] CustomSqlTool tool)
    {
        if (id != tool.Id) return BadRequest("ID mismatch");
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var updated = await toolService.UpdateToolAsync(tool);
        await auditService.WriteLogAsync("tool.custom.updated", id.ToString(), "success", $"Name: {updated.Name}");
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTool(int id)
    {
        var deleted = await toolService.DeleteToolAsync(id);
        if (!deleted) return NotFound();
        await auditService.WriteLogAsync("tool.custom.deleted", id.ToString(), "success");
        return NoContent();
    }
}
