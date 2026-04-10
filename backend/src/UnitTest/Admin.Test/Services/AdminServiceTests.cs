using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Admin.Service.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Admin.Test.Services;

public class AdminServiceTests
{
    private readonly Mock<IAdminContext> _contextMock;
    private readonly Mock<IOptions<JwtSettings>> _jwtSettingsMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly AdminService _service;

    public AdminServiceTests()
    {
        _contextMock = new Mock<IAdminContext>();
        _jwtSettingsMock = new Mock<IOptions<JwtSettings>>();
        _configMock = new Mock<IConfiguration>();

        _jwtSettingsMock.Setup(s => s.Value).Returns(new JwtSettings
        {
            SecretKey = "super-secret-key-that-is-long-enough",
            Issuer = "test",
            Audience = "test",
            AccessTokenExpirationMinutes = 60,
            RefreshTokenExpirationDays = 7
        });

        var users = new List<SuperUser>();
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        _service = new AdminService(_contextMock.Object, _jwtSettingsMock.Object, _configMock.Object);
    }

    [Fact]
    public async Task IsFirstRunAsync_WhenNoUsers_ReturnsTrue()
    {
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(new List<SuperUser>());
        
        var result = await _service.IsFirstRunAsync();
        
        Assert.True(result);
    }

    [Fact]
    public async Task SignUpAsync_WithValidRequest_ReturnsToken()
    {
        var request = new SignUpRequest { Email = "test@example.com", Password = "password123" };
        
        var result = await _service.SignUpAsync(request);
        
        Assert.NotNull(result);
        Assert.Equal("test", result.UserName);
        Assert.Equal("test@example.com", result.Email);
        Assert.NotNull(result.AccessToken);
        _contextMock.Verify(c => c.SuperUsers.Add(It.IsAny<SuperUser>()), Times.Once);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SignInAsync_WithValidCredentials_ReturnsToken()
    {
        var passHash = BCrypt.Net.BCrypt.HashPassword("password123");
        var users = new List<SuperUser>
        {
            new() { Id = 1, Mail = "test@example.com", PasswordHash = passHash, Username = "test" }
        };
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        var request = new SignInRequest { Email = "test@example.com", Password = "password123" };
        
        var result = await _service.SignInAsync(request);
        
        Assert.NotNull(result);
        Assert.Equal("test", result.UserName);
    }

    [Fact]
    public async Task ChangePasswordAsync_WithValidRequest_ChangesPassword()
    {
        var passHash = BCrypt.Net.BCrypt.HashPassword("password123");
        var users = new List<SuperUser>
        {
            new() { Id = 1, Mail = "test@example.com", PasswordHash = passHash, Username = "test" }
        };
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        var request = new ChangePasswordRequest { CurrentPassword = "password123", NewPassword = "newpassword123" };
        
        await _service.ChangePasswordAsync(request, "test@example.com");
        
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(BCrypt.Net.BCrypt.Verify("newpassword123", users[0].PasswordHash));
    }
}
