using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Admin.Service.Interfaces;
using Admin.Service.Data.Entites;

namespace ToolBox.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CustomSqlToolController(ICustomSqlToolService toolService, IAuditService auditService) : ControllerBase
{
    private readonly ICustomSqlToolService _toolService = toolService;
    private readonly IAuditService _auditService = auditService;

    [HttpGet]
    public async Task<IActionResult> GetAllTools()
    {
        return Ok(await _toolService.GetAllToolsAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTool(int id)
    {
        var tool = await _toolService.GetToolByIdAsync(id);
        if (tool == null) return NotFound();
        return Ok(tool);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTool([FromBody] CustomSqlTool tool)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        
        var created = await _toolService.CreateToolAsync(tool);

        await _auditService.WriteLogAsync(
            action: "tool.custom.created",
            target: created.Id.ToString(),
            result: "success",
            detail: $"Name: {created.Name}");

        return CreatedAtAction(nameof(GetTool), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateTool(int id, [FromBody] CustomSqlTool tool)
    {
        if (id != tool.Id) return BadRequest("ID mismatch");
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var updated = await _toolService.UpdateToolAsync(tool);

        await _auditService.WriteLogAsync(
            action: "tool.custom.updated",
            target: id.ToString(),
            result: "success",
            detail: $"Name: {updated.Name}");

        return Ok(updated);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTool(int id)
    {
        var deleted = await _toolService.DeleteToolAsync(id);
        if (!deleted) return NotFound();

        await _auditService.WriteLogAsync(
            action: "tool.custom.deleted",
            target: id.ToString(),
            result: "success");

        return NoContent();
    }
}
