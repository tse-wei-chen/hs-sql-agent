using Admin.Service.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Admin.Service.Models;
using Admin.Service.Interfaces;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Admin.Service.Data.Entites;

namespace Admin.Service.Services;

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

    public async Task<PermissionVM> SignInAsync(SignInRequest request)
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

        return new PermissionVM
        {
            UserName = user.Username,
            Email = user.Mail,
            AccessToken = GenerateAccessToken(user.Id, user.Username, user.Mail),
            RefreshToken = GenerateRefreshToken(user.Id, user.Username, user.Mail)
        };
    }

    public async Task<PermissionVM> SignUpAsync(SignUpRequest request)
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

        if (await _context.SuperUsers.AnyAsync(x => x.Mail == email))
        {
            throw new ArgumentException("A user with the same email already exists.");
        }

        var user = new SuperUser
        {
            Mail = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Username = email.Split('@')[0]
        };

        _context.SuperUsers.Add(user);
        await _context.SaveChangesAsync();

        return new PermissionVM
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

    public async Task<PermissionVM> RefreshTokenAsync(string id)
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

        return new PermissionVM
        {
            UserName = user.Username,
            Email = user.Mail,
            AccessToken = GenerateAccessToken(user.Id, user.Username, user.Mail),
            RefreshToken = GenerateRefreshToken(user.Id, user.Username, user.Mail)
        };
    }

    private string GenerateAccessToken(int userId, string userName, string email)
        => GenerateToken(userId, userName, email, "access", DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes));

    private string GenerateRefreshToken(int userId, string userName, string email)
        => GenerateToken(userId, userName, email, "refresh", DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpirationDays));

    private string GenerateToken(int userId, string userName, string email, string tokenType, DateTime expires)
    {
        if (string.IsNullOrWhiteSpace(_jwtSettings.SecretKey))
        {
            throw new InvalidOperationException("JwtSettings:SecretKey is required.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Typ, tokenType),
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, userName),
            new Claim(JwtRegisteredClaimNames.Email, email)
        };

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
