using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Common.Interfaces;
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

    [HttpGet("retirement/{purpose}/{keyId}")]
    [HasPermission("/runtime/security", "edit")]
    public async Task<IActionResult> ReviewRetirement(
        string purpose, string keyId, CancellationToken cancellationToken)
    {
        if (!IsHumanSecurityEditor()) return Forbid();
        if (!Enum.TryParse<SecretPurpose>(purpose, ignoreCase: true, out var parsed)
            || parsed is not (SecretPurpose.DatabasePassword or SecretPurpose.DurableDmlPayload))
            return BadRequest(new { error = "Unknown encrypted secret purpose." });
        try
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(await migration.ReviewKeyRetirementAsync(parsed, keyId, cancellationToken));
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "Invalid encryption key identifier." });
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            return Conflict(new { error = "The key cannot be inspected using the configured key ring." });
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

