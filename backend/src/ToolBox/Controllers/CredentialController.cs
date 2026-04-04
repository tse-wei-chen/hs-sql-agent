using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ToolBox.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CredentialController(ILogger<CredentialController> logger) : ControllerBase
{
    private readonly ILogger<CredentialController> _logger = logger;

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new { status = "Credential API is running." });
    }
}