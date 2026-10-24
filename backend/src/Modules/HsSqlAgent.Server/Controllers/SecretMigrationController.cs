using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

/// <summary>
/// Human-security-editor-only, explicitly confirmed secret migration surface.
/// Never emits encrypted values, key bytes, connection strings or plaintext.
/// </summary>
[ApiController]
[Route("api/runtime/security/secret-migration")]
public sealed class SecretMigrationController(
    DatabaseSecretMigrationService migration) : ControllerBase
{
    [HttpGet("inventory")]
    [HasPermission("/runtime/security", "edit")]
    public async Task<IActionResult> Inventory(CancellationToken cancellationToken)
    {
        if (!IsHumanSecurityEditor()) return Forbid();
        Response.Headers.CacheControl = "no-store";
        return Ok(await migration.InspectAsync(cancellationToken));
    }

    [HttpPost("database-passwords/batch")]
    [HasPermission("/runtime/security", "edit")]
    public async Task<IActionResult> MigrateDatabasePasswords(
        [FromBody] DatabasePasswordMigrationRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsHumanSecurityEditor()) return Forbid();
        if (request is null ||
            request.Confirmation != "MIGRATE_DATABASE_SECRETS" ||
            request.AfterId < 0 || request.BatchSize is < 1 or > 100)
            return BadRequest(new { error =
                "A valid cursor, batch size (1-100), and explicit MIGRATE_DATABASE_SECRETS confirmation are required." });

        try
        {
            var actor = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(actor) || actor.Length > 64)
                return BadRequest(new { error = "Invalid security operator identity." });
            // Every row writes its audit event in the same DB transaction
            // as the secret re-encryption. Batch-level logging is not a
            // substitute for durable, transactionally coupled evidence.
            var result = await migration.MigrateBatchAsync(
                request.AfterId, request.BatchSize, cancellationToken, actor);
            Response.Headers.CacheControl = "no-store";
            return Ok(result);
        }
        catch (InvalidOperationException)
        {
            // Do not propagate lower-layer provider/crypto/configuration text
            // via an exception filter to the API response.
            return Conflict(new { error = "Migration is blocked. Verify key configuration, pending approvals, and worker quiescence." });
        }
    }

    private bool IsHumanSecurityEditor()
    {
        if (User.FindFirst("hsa_actor_type")?.Value == "management-api-key")
            return false;
        return !string.IsNullOrWhiteSpace(
            User.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
            User.FindFirstValue(ClaimTypes.NameIdentifier));
    }
}

public sealed record DatabasePasswordMigrationRequest(
    int AfterId, int BatchSize, string? Confirmation);
