using Modules.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Modules.Models;
using Modules.Interfaces;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Modules.Services;

public class AdminService : IAdminService
{
    private readonly IAdminContext _context;
    private readonly JwtSettings _jwtSettings;
    private readonly IConfiguration _configuration;

    public AdminService(IAdminContext context, IOptions<JwtSettings> jwtSettings, IConfiguration configuration)
    {
        _context = context;
        _jwtSettings = jwtSettings.Value;
        _configuration = configuration;
    }

    public async Task<bool> IsFirstRunAsync()
    {
        return !await _context.SuperUsers.AnyAsync();
    }

    public async Task<SignInVM> SignInAsync(SignInRequest request)
    {
        if (request is null)
        {
            throw new ArgumentException("Request body is required.");
        }

        var email = request.Email?.Trim();
        var password = request.Password?.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Email and password are required.");
        }

        var user = await _context.SuperUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Mail == email);

        if (user is null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        return new SignInVM
        {
            UserName = user.Username,
            Email = user.Mail,
            AccessToken = GenerateAccessToken(user.Id, user.Username, user.Mail),
            RefreshToken = GenerateRefreshToken(user.Id, user.Username, user.Mail)
        };
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, string userEmail)
    {
        if (request is null)
        {
            throw new ArgumentException("Request body is required.");
        }

        var email = userEmail?.Trim();
        var currentPassword = request.CurrentPassword?.Trim();
        var newPassword = request.NewPassword?.Trim();

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
        {
            throw new ArgumentException("Email, current password, and new password are required.");
        }

        var user = await _context.SuperUsers
            .FirstOrDefaultAsync(x => x.Mail == email);

        if (user is null || !BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid email or current password.");
        }

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _context.SaveChangesAsync();
    }

    public async Task<SignInVM> RefreshTokenAsync(string id)
    {
        var userId = id?.Trim();
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User ID is required.");
        }

        var user = await _context.SuperUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id.ToString() == userId);

        if (user is null)
        {
            throw new UnauthorizedAccessException("User not found.");
        }

        return new SignInVM
        {
            UserName = user.Username,
            Email = user.Mail,
            AccessToken = GenerateAccessToken(user.Id, user.Username, user.Mail),
            RefreshToken = GenerateRefreshToken(user.Id, user.Username, user.Mail)
        };
    }

    private string GenerateAccessToken(int userId, string userName, string email)
    {
        if (string.IsNullOrWhiteSpace(_jwtSettings.SecretKey))
        {
            throw new InvalidOperationException("JwtSettings:SecretKey is required.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Typ, "access"),
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, userName),
            new Claim(JwtRegisteredClaimNames.Email, email)
        };

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GenerateRefreshToken(int userId, string userName, string email)
    {
        if (string.IsNullOrWhiteSpace(_jwtSettings.SecretKey))
        {
            throw new InvalidOperationException("JwtSettings:SecretKey is required.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Typ, "refresh"),
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, userName),
            new Claim(JwtRegisteredClaimNames.Email, email)
        };

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}