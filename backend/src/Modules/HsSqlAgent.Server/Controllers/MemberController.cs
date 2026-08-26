using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Admin.Service.Interfaces;
using Auth.Service.Interfaces;
using Auth.Service.Models;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MemberController(
    ILogger<MemberController> logger,
    IMemberService memberService,
    IAuthService authService,
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
    public async Task<IActionResult> GetUsersAsync([FromQuery] MemberQuery query, CancellationToken cancellationToken)
    {
        var users = await memberService.GetMembersAsync(query, cancellationToken);
        return Ok(users);
    }

    [HttpPut("{id:int}/roles")]
    [HasPermission("/auth/user", "edit")]
    public async Task<IActionResult> UpdateUserRolesAsync(
        int id,
        [FromBody] UpdateMemberRolesRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (currentUserId == id.ToString())
        {
            await auditService.WriteLogAsync("admin.users.roles.update", id.ToString(), "failed", "Cannot change your own roles.");
            return BadRequest("Cannot change your own roles.");
        }

        try
        {
            var result = await memberService.UpdateMemberRolesAsync(id, request, cancellationToken);
            await auditService.WriteLogAsync("admin.users.roles.update", id.ToString(), "success");
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Update user roles failed.");
            await auditService.WriteLogAsync("admin.users.roles.update", id.ToString(), "failed", ex.Message);
            return ex.Message == "Member not found."
                ? NotFound(ex.Message)
                : BadRequest(ex.Message);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid user roles request.");
            await auditService.WriteLogAsync("admin.users.roles.update", id.ToString(), "failed", ex.Message, cancellationToken);
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id:int}/status")]
    [HasPermission("/auth/user", "edit")]
    public async Task<IActionResult> UpdateUserStatusAsync(
        int id,
        [FromBody] UpdateMemberStatusRequest request,
        CancellationToken cancellationToken)
    {
        var currentUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (currentUserId == id.ToString() && !request.IsActive)
            return BadRequest("Cannot disable yourself.");

        try
        {
            var result = await memberService.UpdateMemberStatusAsync(id, request, cancellationToken);
            await auditService.WriteLogAsync(
                "admin.users.status.update",
                id.ToString(),
                "success",
                request.IsActive ? "Account enabled" : "Account disabled",
                cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Update user status failed.");
            await auditService.WriteLogAsync("admin.users.status.update", id.ToString(), "failed", ex.Message, cancellationToken);
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:int}")]
    [HasPermission("/auth/user", "delete")]
    public async Task<IActionResult> DeleteUserAsync(int id, CancellationToken cancellationToken)
    {
        var currentUserId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (currentUserId == id.ToString())
        {
            await auditService.WriteLogAsync("admin.users.delete", id.ToString(), "failed", "Cannot delete yourself.");
            return BadRequest("Cannot delete yourself.");
        }

        try
        {
            await memberService.DeleteMemberAsync(id, cancellationToken);
            await auditService.WriteLogAsync("admin.users.delete", id.ToString(), "success");
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Delete user failed.");
            await auditService.WriteLogAsync("admin.users.delete", id.ToString(), "failed", ex.Message);
            return ex.Message == "Member not found."
                ? NotFound(ex.Message)
                : BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:int}/sessions")]
    [HasPermission("/auth/user", "edit")]
    public async Task<IActionResult> RevokeUserSessionsAsync(int id, CancellationToken cancellationToken)
    {
        await authService.RevokeAllSessionsAsync(id, null, "Revoked by administrator.", cancellationToken);
        await auditService.WriteLogAsync("admin.users.sessions.revoke", id.ToString(), "success", cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpPut("{id:int}/password-change-required")]
    [HasPermission("/auth/user", "edit")]
    public async Task<IActionResult> SetPasswordChangeRequiredAsync(
        int id,
        RequirePasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await memberService.SetPasswordChangeRequiredAsync(id, request.Required, cancellationToken);
            await auditService.WriteLogAsync("admin.users.password-reset.require", id.ToString(), "success", cancellationToken: cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
