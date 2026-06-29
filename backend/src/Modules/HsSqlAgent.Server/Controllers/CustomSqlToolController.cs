using System.Text.Json;
using System.Text.Json.Serialization;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlAgent.Service.Models;
using SqlAgent.Service.Validation;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CustomSqlToolController(ICustomSqlToolService toolService, IAuditService auditService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [HttpGet]
    [HasPermission("/runtime/custom-tools", "view")]
    public async Task<IActionResult> GetAllTools()
        => Ok(await toolService.GetAllToolsAsync());

    [HttpGet("{id}")]
    [HasPermission("/runtime/custom-tools", "view")]
    public async Task<IActionResult> GetTool(int id)
    {
        var tool = await toolService.GetToolByIdAsync(id);
        return tool == null ? NotFound() : Ok(tool);
    }

    [HttpPost]
    [HasPermission("/runtime/custom-tools", "create")]
    public async Task<IActionResult> CreateTool([FromBody] CustomSqlTool tool)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var validationError = ValidateDefinition(tool);
        if (validationError != null) return BadRequest(validationError);

        var created = await toolService.CreateToolAsync(tool);
        await auditService.WriteLogAsync("tool.custom.created", created.Id.ToString(), "success", $"Name: {created.Name}");
        return CreatedAtAction(nameof(GetTool), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    [HasPermission("/runtime/custom-tools", "edit")]
    public async Task<IActionResult> UpdateTool(int id, [FromBody] CustomSqlTool tool)
    {
        if (id != tool.Id) return BadRequest("ID mismatch");
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var validationError = ValidateDefinition(tool);
        if (validationError != null) return BadRequest(validationError);

        var updated = await toolService.UpdateToolAsync(tool);
        await auditService.WriteLogAsync("tool.custom.updated", id.ToString(), "success", $"Name: {updated.Name}");
        return Ok(updated);
    }

    [HttpDelete("{id}")]
    [HasPermission("/runtime/custom-tools", "delete")]
    public async Task<IActionResult> DeleteTool(int id)
    {
        var deleted = await toolService.DeleteToolAsync(id);
        if (!deleted) return NotFound();
        await auditService.WriteLogAsync("tool.custom.deleted", id.ToString(), "success");
        return NoContent();
    }

    private static object? ValidateDefinition(CustomSqlTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.DefinitionJson))
            return new { error = "DefinitionJson must not be empty." };

        try
        {
            var errors = IsDml(tool)
                ? DefinitionValidator.Validate(JsonSerializer.Deserialize<DmlDefinition>(tool.DefinitionJson, JsonOptions))
                : DefinitionValidator.Validate(JsonSerializer.Deserialize<QueryDefinition>(tool.DefinitionJson, JsonOptions));

            return errors.Count == 0
                ? null
                : new { error = "Validation failed.", errors };
        }
        catch (JsonException ex)
        {
            return new { error = "DefinitionJson is invalid JSON.", detail = ex.Message };
        }
    }

    private static bool IsDml(CustomSqlTool tool)
        => string.Equals(tool.Type, "DML", StringComparison.OrdinalIgnoreCase);
}
