using Admin.Service.Interfaces;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RoleController(ILogger<RoleController> logger, IRoleService roleService, IAuditService auditService) : ControllerBase
{
    [HttpGet]
    [HasPermission("/auth/role", "view")]
    public async Task<IActionResult> GetRolesAsync()
        => Ok(await roleService.GetRolesAsync());

    [HttpPost]
    [HasPermission("/auth/role", "create")]
    public async Task<IActionResult> CreateRoleAsync([FromBody] RolePayload request)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var result = await roleService.UpsertRoleAsync(null, request);
            await auditService.WriteLogAsync("role.create", request.Name, "success");
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Create role failed.");
            await auditService.WriteLogAsync("role.create", request.Name, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error creating role.");
            await auditService.WriteLogAsync("role.create", request.Name, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id:int}")]
    [HasPermission("/auth/role", "edit")]
    public async Task<IActionResult> UpdateRoleAsync(int id, [FromBody] RolePayload request)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var result = await roleService.UpsertRoleAsync(id, request);
            await auditService.WriteLogAsync("role.update", request.Name, "success");
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Update role failed.");
            await auditService.WriteLogAsync("role.update", request.Name, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error updating role.");
            await auditService.WriteLogAsync("role.update", request.Name, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:int}")]
    [HasPermission("/auth/role", "delete")]
    public async Task<IActionResult> RemoveRoleAsync(int id)
    {
        try
        {
            await roleService.RemoveRoleAsync(id);
            await auditService.WriteLogAsync("role.remove", id.ToString(), "success");
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Remove role failed.");
            await auditService.WriteLogAsync("role.remove", id.ToString(), "failed", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error removing role.");
            await auditService.WriteLogAsync("role.remove", id.ToString(), "failed", ex.Message);
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("permission-action-templates")]
    [HasPermission("/auth/role", "view")]
    public async Task<IActionResult> GetPermissionActionTemplatesAsync()
        => Ok(await roleService.GetPermissionActionTemplatesAsync());
}
