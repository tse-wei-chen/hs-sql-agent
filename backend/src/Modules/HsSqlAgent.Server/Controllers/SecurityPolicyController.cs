using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/runtime/security")]
public class SecurityPolicyController(
    ISecurityPolicyService securityPolicyService,
    IAuditService auditService) : ControllerBase
{
    [HttpGet]
    [HasPermission("/runtime/security", "view")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Ok(await securityPolicyService.GetAsync(cancellationToken));

    [HttpPut]
    [HasPermission("/runtime/security", "edit")]
    public async Task<IActionResult> Update(
        [FromBody] SecurityPolicyModel request,
        CancellationToken cancellationToken)
    {
        var actorId = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        var result = await securityPolicyService.UpdateAsync(request, actorId, cancellationToken);
        await auditService.WriteLogAsync(
            "security.policy.updated",
            "runtime-security",
            "success",
            $"MaxRows: {result.QueryMaxRows}; TimeoutSeconds: {result.QueryTimeoutSeconds}; DmlMaxAffectedRows: {result.DmlMaxAffectedRows}",
            cancellationToken);
        return Ok(result);
    }
}
