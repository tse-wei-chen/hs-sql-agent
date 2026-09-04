using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Controllers;

[ApiController]
[Route("api/runtime/client-config")]
public sealed class McpClientConfigController(IOptions<McpOptions> options) : ControllerBase
{
    [HttpGet]
    [HasPermission("/runtime/mcp-keys", "view")]
    public ActionResult<McpClientConfigResponse> Get()
        => Ok(new McpClientConfigResponse(options.Value.PublicEndpoint));
}
