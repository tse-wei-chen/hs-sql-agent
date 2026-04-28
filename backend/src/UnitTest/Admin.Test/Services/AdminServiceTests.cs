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
    private readonly JwtSettings _validJwtSettings;
    private AdminService _service;

    public AdminServiceTests()
    {
        _contextMock = new Mock<IAdminContext>();
        _jwtSettingsMock = new Mock<IOptions<JwtSettings>>();
        _configMock = new Mock<IConfiguration>();

        _validJwtSettings = new JwtSettings
        {
            SecretKey = "super-secret-key-that-is-long-enough-for-hmac-sha256",
            Issuer = "test-issuer",
            Audience = "test-audience",
            AccessTokenExpirationMinutes = 60,
            RefreshTokenExpirationDays = 7
        };

        _jwtSettingsMock.Setup(s => s.Value).Returns(_validJwtSettings);

        // Setup default empty DbSet
        var users = new List<SuperUser>();
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        _service = new AdminService(_contextMock.Object, _jwtSettingsMock.Object, _configMock.Object);
    }

    #region IsFirstRunAsync Tests

    [Fact]
    public async Task IsFirstRunAsync_ShouldReturnTrue_WhenNoUsersExist()
    {
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(new List<SuperUser>());

        var result = await _service.IsFirstRunAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task IsFirstRunAsync_ShouldReturnFalse_WhenUsersExist()
    {
        var users = new List<SuperUser> { new() { Id = 1, Mail = "admin@example.com" } };
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        var result = await _service.IsFirstRunAsync();

        Assert.False(result);
    }

    #endregion

    #region SignUpAsync Tests

    [Fact]
    public async Task SignUpAsync_ShouldThrowInvalidOperationException_WhenNotFirstRun()
    {
        // Arrange
        var users = new List<SuperUser> { new() { Id = 1, Mail = "admin@example.com" } };
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        var request = new SignUpRequest { Email = "new@example.com", Password = "password123" };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SignUpAsync(request));
        Assert.Contains("Admin user already exists", ex.Message);
        _contextMock.Verify(c => c.SuperUsers.Add(It.IsAny<SuperUser>()), Times.Never);
    }

    [Fact]
    public async Task SignUpAsync_ShouldCreateUserAndReturnTokens_WhenFirstRun()
    {
        // Arrange
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(new List<SuperUser>());

        var request = new SignUpRequest { Email = "  test@example.com  ", Password = "  password123  " };
        var usersList = new List<SuperUser>();
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(usersList);

        // Act
        var result = await _service.SignUpAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test", result.UserName); // Splitted from email
        Assert.Equal("test@example.com", result.Email);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);

        var dbSetMock = Mock.Get(_contextMock.Object.SuperUsers);
        dbSetMock.Verify(m => m.Add(It.Is<SuperUser>(u => 
            u.Mail == "test@example.com" && 
            u.Username == "test" &&
            BCrypt.Net.BCrypt.Verify("password123", u.PasswordHash)
        )), Times.Once);
        
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region SignInAsync Tests

    [Fact]
    public async Task SignInAsync_ShouldThrowUnauthorizedAccessException_WhenEmailNotFound()
    {
        // Arrange
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(new List<SuperUser>());
        var request = new SignInRequest { Email = "unknown@example.com", Password = "password" };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SignInAsync(request));
        Assert.Equal("Invalid email or password.", ex.Message);
    }

    [Fact]
    public async Task SignInAsync_ShouldThrowUnauthorizedAccessException_WhenPasswordIsIncorrect()
    {
        // Arrange
        var passHash = BCrypt.Net.BCrypt.HashPassword("correct-password");
        var users = new List<SuperUser>
        {
            new() { Id = 1, Mail = "admin@example.com", PasswordHash = passHash, Username = "admin" }
        };
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        var request = new SignInRequest { Email = "admin@example.com", Password = "wrong-password" };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SignInAsync(request));
        Assert.Equal("Invalid email or password.", ex.Message);
    }

    [Fact]
    public async Task SignInAsync_ShouldReturnTokens_WhenCredentialsAreCorrect()
    {
        // Arrange
        var passHash = BCrypt.Net.BCrypt.HashPassword("password123");
        var users = new List<SuperUser>
        {
            new() { Id = 1, Mail = "test@example.com", PasswordHash = passHash, Username = "test" }
        };
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        var request = new SignInRequest { Email = "  test@example.com  ", Password = "  password123  " };

        // Act
        var result = await _service.SignInAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test", result.UserName);
        Assert.Equal("test@example.com", result.Email);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
    }

    #endregion

    #region RefreshTokenAsync Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RefreshTokenAsync_ShouldThrowArgumentException_WhenIdIsMissingOrWhitespace(string? id)
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.RefreshTokenAsync(id!));
        Assert.Equal("User ID is required.", ex.Message);
    }

    [Fact]
    public async Task RefreshTokenAsync_ShouldThrowUnauthorizedAccessException_WhenUserNotFound()
    {
        // Arrange
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(new List<SuperUser>());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RefreshTokenAsync("999"));
        Assert.Equal("User not found.", ex.Message);
    }

    [Fact]
    public async Task RefreshTokenAsync_ShouldReturnNewTokens_WhenUserExists()
    {
        // Arrange
        var users = new List<SuperUser>
        {
            new() { Id = 1, Mail = "test@example.com", Username = "test" }
        };
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        // Act
        var result = await _service.RefreshTokenAsync("1");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test", result.UserName);
        Assert.Equal("test@example.com", result.Email);
        Assert.NotNull(result.AccessToken);
        Assert.NotNull(result.RefreshToken);
    }

    #endregion

    #region Token Generation Settings Validations

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SignInAsync_ShouldThrowInvalidOperationException_WhenJwtSecretKeyIsMissing(string? emptySecret)
    {
        // Arrange
        var passHash = BCrypt.Net.BCrypt.HashPassword("password123");
        var users = new List<SuperUser>
        {
            new() { Id = 1, Mail = "test@example.com", PasswordHash = passHash, Username = "test" }
        };
        _contextMock.Setup(c => c.SuperUsers).ReturnsDbSet(users);

        var invalidJwtSettings = new JwtSettings { SecretKey = emptySecret! };
        _jwtSettingsMock.Setup(s => s.Value).Returns(invalidJwtSettings);
        
        _service = new AdminService(_contextMock.Object, _jwtSettingsMock.Object, _configMock.Object);

        var request = new SignInRequest { Email = "test@example.com", Password = "password123" };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SignInAsync(request));
        Assert.Equal("JwtSettings:SecretKey is required.", ex.Message);
    }

    #endregion
}
