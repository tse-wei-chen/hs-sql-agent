using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Controllers;

/// <summary>
/// Operator-only confirmation of already-consumed, uncertain approvals.
/// No endpoint here can execute SQL or turn an approval back into Pending.
/// Management API keys cannot acquire /runtime/security.edit.
/// </summary>
[ApiController]
[Route("api/runtime/dml-approvals/reconciliation")]
public sealed class DurableDmlReconciliationController(
    DurableDmlReconciliationService reconciliation) : ControllerBase
{
    [HttpGet]
    [HasPermission("/runtime/security", "edit")]
    [ProducesResponseType(typeof(IReadOnlyList<DurableDmlReconciliationItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
            return BadRequest(new { error = "Limit must be between 1 and 100." });
        Response.Headers.CacheControl = "no-store";
        return Ok(await reconciliation.ListAsync(limit, cancellationToken));
    }

    [HttpPost("{requestId}/resolve")]
    [HasPermission("/runtime/security", "edit")]
    [ProducesResponseType(typeof(DurableDmlReconciliationItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resolve(
        string requestId,
        [FromBody] DurableDmlReconciliationRequest request,
        CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actor)
            || User.FindFirst("hsa_actor_type")?.Value == "management-api-key")
            return Forbid();

        try
        {
            var result = await reconciliation.ReconcileAsync(
                requestId, request, actor, cancellationToken);
            if (result is null) return NotFound();
            Response.Headers.CacheControl = "no-store";
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (DbUpdateConcurrencyException) { return Conflict(new { error = "Approval was changed by another operation. Reload and review before retrying reconciliation." }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
