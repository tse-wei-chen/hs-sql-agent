using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Admin.Service.Interfaces;

namespace HsSqlAgent.Server.Controllers;

/// <summary>Human-admin only. Management keys cannot issue, rotate or revoke other management keys.</summary>
[ApiController]
[Route("api/runtime/management-api-keys")]
public sealed class ManagementApiKeysController(
    IManagementApiKeyService keys, IAuditService audit) : ControllerBase
{
    // Reuse existing security.edit permission; do not introduce separate authentication/role seed requirements.
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ManagementApiKeyView>), StatusCodes.Status200OK)]
    [HasPermission("/runtime/security", "edit")]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok(await keys.ListAsync(cancellationToken));

    [HttpGet("permissions")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [HasPermission("/runtime/security", "edit")]
    public IActionResult Permissions() => Ok(ManagementApiKeyPermissions.Grantable);

    [HttpPost]
    [ProducesResponseType(typeof(IssuedManagementApiKey), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [HasPermission("/runtime/security", "edit")]
    public async Task<IActionResult> Issue([FromBody] ManagementApiKeyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await keys.IssueAsync(request, Actor(), cancellationToken);
            await audit.WriteLogAsync("management-api-key.issued", result.Key.Id.ToString(),
                "success", $"Prefix: {result.Key.KeyPrefix}", cancellationToken);
            Response.Headers.CacheControl = "no-store";
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{id:int}/rotate")]
    [ProducesResponseType(typeof(IssuedManagementApiKey), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [HasPermission("/runtime/security", "edit")]
    public async Task<IActionResult> Rotate(int id, CancellationToken cancellationToken)
    {
        try
        {
            var result = await keys.RotateAsync(id, Actor(), cancellationToken);
            if (result is null) return NotFound();
            await audit.WriteLogAsync("management-api-key.rotated", id.ToString(), "success",
                $"Replacement key: {result.Key.Id}", cancellationToken);
            Response.Headers.CacheControl = "no-store";
            return Ok(result);
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("{id:int}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [HasPermission("/runtime/security", "edit")]
    public async Task<IActionResult> Revoke(int id, CancellationToken cancellationToken)
    {
        if (!await keys.RevokeAsync(id, Actor(), cancellationToken)) return NotFound();
        await audit.WriteLogAsync("management-api-key.revoked", id.ToString(), "success",
            cancellationToken: cancellationToken);
        return NoContent();
    }

    private string? Actor() => User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
