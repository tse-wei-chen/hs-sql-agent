using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Modules.Interfaces;
using Modules.Models;
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
            _logger.LogWarning(ex, "Sign-in failed for user: {Email}", request.Email);
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid sign-in request.");
            return BadRequest(ex.Message);
        }
    }

    [HttpPatch("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }
        if (User.FindFirstValue(JwtRegisteredClaimNames.Email) is var email && string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized("User email is required.");
        }
        try
        {
            await _adminService.ChangePasswordAsync(request, email);
            return Ok("Password changed successfully.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Change password failed for user: {Email}", email);
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid change password request.");
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
            _logger.LogWarning(ex, "Token refresh failed for user ID: {UserId}", id);
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid token refresh request.");
            return BadRequest(ex.Message);
        }
    }
}