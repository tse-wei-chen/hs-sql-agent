using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ToolBox.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DbManagementController(
    IDbManagementService dbManagementService,
    IAuditService auditService) : ControllerBase
{
    private readonly IDbManagementService _dbManagementService = dbManagementService;
    private readonly IAuditService _auditService = auditService;

    [HttpPost]
    public async Task<IActionResult> CreateDb([FromBody] DbManagementRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        var result = await _dbManagementService.CreateDbAsync(request, cancellationToken);

        await _auditService.WriteAsync(
            action: "db.management.created",
            target: result.Id.ToString(),
            result: "success",
            detail: $"Created DB management entry with ID {result.Id}",
            actorType: "admin",
            actorId: GetActorId(),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetDbById(int id, CancellationToken cancellationToken)
    {
        var result = await _dbManagementService.GetDbByIdAsync(id, false, cancellationToken);
        if (result == null)
        {
            return NotFound();
        }
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetAllDbs(CancellationToken cancellationToken)
    {
        var result = await _dbManagementService.GetAllDbsAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateDb(int id, [FromBody] DbManagementRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required.");
        }

        await _dbManagementService.UpdateDbAsync(id, request, cancellationToken);

        await _auditService.WriteAsync(
            action: "db.management.updated",
            target: id.ToString(),
            result: "success",
            detail: $"Updated DB management entry with ID {id}",
            actorType: "admin",
            actorId: GetActorId(),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteDb(int id, CancellationToken cancellationToken)
    {
        await _dbManagementService.DeleteDbAsync(id, cancellationToken);

        await _auditService.WriteAsync(
            action: "db.management.deleted",
            target: id.ToString(),
            result: "success",
            detail: $"Deleted DB management entry with ID {id}",
            actorType: "admin",
            actorId: GetActorId(),
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: HttpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken: cancellationToken);

        return NoContent();
    }

    private string? GetActorId()
    {
        return User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}