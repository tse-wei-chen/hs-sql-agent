using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToolBox.Attributes;

namespace ToolBox.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController(ILogger<AdminController> logger, IAdminService adminService) : ControllerBase
{
    private readonly ILogger<AdminController> _logger = logger;
    private readonly IAdminService _adminService = adminService;

    [HttpGet("first-run")]
    [AllowAnonymous]
    public async Task<IActionResult> CheckFirstRunAsync()
        => Ok(await _adminService.IsFirstRunAsync());

    [HttpPost("sign-in")]
    [AllowAnonymous]
    public async Task<IActionResult> SignIn([FromBody] SignInRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }
        try
        {
            var result = await _adminService.SignInAsync(request);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Sign-in failed for user");
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid sign-in request.");
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("sign-up")]
    [AllowAnonymous]
    public async Task<IActionResult> SignUp([FromBody] SignUpRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }
        try
        {
            var result = await _adminService.SignUpAsync(request);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid sign-up request.");
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Sign-up attempt when admin user already exists.");
            return BadRequest(ex.Message);
        }
    }

    [RefreshAuthorize]
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshTokenAsync()
    {
        if (User.FindFirstValue(JwtRegisteredClaimNames.Sub) is var id && string.IsNullOrWhiteSpace(id))
        {
            return Unauthorized("User ID is required.");
        }
        try
        {
            var result = await _adminService.RefreshTokenAsync(id);
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Token refresh failed");
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid token refresh request.");
            return BadRequest(ex.Message);
        }
    }
}
