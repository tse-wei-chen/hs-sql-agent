using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Admin.Service.Interfaces;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MemberController(
    ILogger<MemberController> logger,
    IMemberService memberService,
    IAuditService auditService) : ControllerBase
{
    [HttpPost]
    [HasPermission("/auth/user", "create")]
    public async Task<IActionResult> CreateMemberAsync([FromBody] CreateMemberRequest request)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        try
        {
            var memberId = await memberService.CreateMemberAsync(request);
            await auditService.WriteLogAsync("admin.users.create", request.Email, "success");
            return Ok(new { id = memberId });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Create user failed.");
            await auditService.WriteLogAsync("admin.users.create", request.Email, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid create user request.");
            await auditService.WriteLogAsync("admin.users.create", request.Email, "failed", ex.Message);
            return BadRequest(ex.Message);
        }
    }

    [HttpGet]
    [HasPermission("/auth/user", "view")]
    public async Task<IActionResult> GetUsersAsync()
    {
        var users = await memberService.GetMembersAsync();
        return Ok(users);
    }

    [HttpPut("{id:int}/roles")]
    [HasPermission("/auth/user", "edit")]
    public async Task<IActionResult> UpdateUserRolesAsync(int id, [FromBody] UpdateMemberRolesRequest request)
    {
        var currentUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (currentUserId == id.ToString())
        {
            await auditService.WriteLogAsync("admin.users.roles.update", id.ToString(), "failed", "Cannot change your own roles.");
            return BadRequest("Cannot change your own roles.");
        }

        try
        {
            var result = await memberService.UpdateMemberRolesAsync(id, request);
            await auditService.WriteLogAsync("admin.users.roles.update", id.ToString(), "success");
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Update user roles failed.");
            await auditService.WriteLogAsync("admin.users.roles.update", id.ToString(), "failed", ex.Message);
            return NotFound(ex.Message);
        }
    }

    [HttpDelete("{id:int}")]
    [HasPermission("/auth/user", "delete")]
    public async Task<IActionResult> DeleteUserAsync(int id)
    {
        var currentUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (currentUserId == id.ToString())
        {
            await auditService.WriteLogAsync("admin.users.delete", id.ToString(), "failed", "Cannot delete yourself.");
            return BadRequest("Cannot delete yourself.");
        }

        try
        {
            await memberService.DeleteMemberAsync(id);
            await auditService.WriteLogAsync("admin.users.delete", id.ToString(), "success");
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Delete user failed.");
            await auditService.WriteLogAsync("admin.users.delete", id.ToString(), "failed", ex.Message);
            return NotFound(ex.Message);
        }
    }
}
