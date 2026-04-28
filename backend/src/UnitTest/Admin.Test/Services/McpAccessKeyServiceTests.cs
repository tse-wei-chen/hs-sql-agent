using System.Security.Cryptography;
using System.Text;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Admin.Service.Services;
using Common.Interfaces;
using Microsoft.Extensions.Options;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Admin.Test.Services;

public class McpAccessKeyServiceTests
{
    private readonly Mock<IAdminContext> _contextMock;
    private readonly Mock<IOptions<McpKeySettings>> _settingsMock;
    private readonly Mock<ICryptoService> _cryptoServiceMock;
    private readonly McpAccessKeyService _service;
    private readonly string _testHmacSecret = "12345678901234567890123456789012";

    public McpAccessKeyServiceTests()
    {
        _contextMock = new Mock<IAdminContext>();

        _settingsMock = new Mock<IOptions<McpKeySettings>>();
        _settingsMock.Setup(s => s.Value).Returns(new McpKeySettings { HmacSecretKey = _testHmacSecret });

        _cryptoServiceMock = new Mock<ICryptoService>();
        _cryptoServiceMock.Setup(c => c.EncryptText(It.IsAny<string>(), It.IsAny<byte[]>()))
            .Returns((string? plain, byte[] key) => plain != null ? $"ENCRYPTED_{plain}" : null);
        _cryptoServiceMock.Setup(c => c.DecryptText(It.IsAny<string>(), It.IsAny<byte[]>()))
            .Returns((string? cipher, byte[] key) => cipher?.Replace("ENCRYPTED_", ""));

        // Default empty DbSet to prevent null reference errors
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>());

        _service = new McpAccessKeyService(_contextMock.Object, _settingsMock.Object, _cryptoServiceMock.Object);
    }

    #region IssueKeyAsync Tests

    [Fact]
    public async Task IssueKeyAsync_ShouldThrowArgumentException_WhenNameIsMissing()
    {
        // Arrange
        var request = new IssueMcpAccessKeyModel { Name = "" };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueKeyAsync(request, "tester", TestContext.Current.CancellationToken));
        Assert.Contains("Key name is required", ex.Message);
    }

    [Fact]
    public async Task IssueKeyAsync_ShouldThrowArgumentException_WhenSqlProviderOrConnectionStringAreMismatched()
    {
        // Arrange
        var requestWithProviderOnly = new IssueMcpAccessKeyModel { Name = "Test", SqlProvider = "PostgreSQL" };
        var requestWithConnOnly = new IssueMcpAccessKeyModel { Name = "Test", SqlConnectionString = "Host=localhost;" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueKeyAsync(requestWithProviderOnly, "tester", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueKeyAsync(requestWithConnOnly, "tester", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IssueKeyAsync_ShouldThrowArgumentException_WhenCorsOriginIsInvalid()
    {
        // Arrange
        var request = new IssueMcpAccessKeyModel { Name = "Test", CorsAllowedOrigins = "not-a-valid-url" };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.IssueKeyAsync(request, "tester", TestContext.Current.CancellationToken));
        Assert.Contains("Invalid CORS origin", ex.Message);
    }

    [Fact]
    public async Task IssueKeyAsync_ShouldNormalizeCorsOriginsAndIssueKey_WhenRequestIsValid()
    {
        // Arrange
        var request = new IssueMcpAccessKeyModel
        {
            Name = "   Test Key   ",
            SqlProvider = "  PostgreSQL  ",
            SqlConnectionString = "  Host=localhost;  ",
            CorsAllowedOrigins = "http://localhost:3000, HTTPS://API.EXAMPLE.COM/; http://localhost:3000  "
        };

        var mockDbSet = new Mock<Microsoft.EntityFrameworkCore.DbSet<McpAccessKey>>();
        _contextMock.Setup(c => c.McpAccessKeys).Returns(mockDbSet.Object);

        // Act
        var result = await _service.IssueKeyAsync(request, "tester", TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Key", result.Name);
        Assert.Equal("http://localhost:3000,https://api.example.com", result.CorsAllowedOrigins);
        Assert.Equal("PostgreSQL", result.SqlProvider);
        Assert.True(result.HasSqlConnectionStringOverride);
        Assert.NotNull(result.PlaintextKey);

        mockDbSet.Verify(m => m.Add(It.Is<McpAccessKey>(e =>
            e.Name == "Test Key" &&
            e.CorsAllowedOrigins == "http://localhost:3000,https://api.example.com" &&
            e.SqlProvider == "PostgreSQL" &&
            e.SqlConnectionString == "ENCRYPTED_Host=localhost;" &&
            e.CreatedBy == "tester" &&
            e.IsActive == true
        )), Times.Once);

        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ValidateAsync Tests

    private string GenerateTestHash(string plaintext)
    {
        var hmacSecretBytes = Encoding.UTF8.GetBytes(_testHmacSecret);
        var hashBytes = HMACSHA256.HashData(hmacSecretBytes, Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToBase64String(hashBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateAsync_ShouldReturnFalse_WhenRawKeyIsMissingOrWhitespace(string? rawKey)
    {
        // Act
        var result = await _service.ValidateAsync(rawKey!, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Missing key.", result.Reason);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnFalse_WhenKeyIsNotFoundByPrefix()
    {
        // Arrange
        var rawKey = "12345678-abcd";
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>());

        // Act
        var result = await _service.ValidateAsync(rawKey, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Key not found.", result.Reason);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnFalse_WhenPrefixMatchesButHashDoesNot()
    {
        // Arrange
        var rawKey = "12345678-invalid-secret";
        var prefix = "12345678";

        var keys = new List<McpAccessKey>
        {
            new() { KeyPrefix = prefix, KeyHash = "ZGlmZmVyZW50X2hhc2g=", IsActive = true }
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(keys);

        // Act
        var result = await _service.ValidateAsync(rawKey, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Key not found.", result.Reason);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnFalse_WhenKeyIsFoundButNotActive()
    {
        // Arrange
        var rawKey = "12345678-secret";
        var prefix = "12345678";

        var keys = new List<McpAccessKey>
        {
            new() { KeyPrefix = prefix, KeyHash = GenerateTestHash(rawKey), IsActive = false }
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(keys);

        // Act
        var result = await _service.ValidateAsync(rawKey, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Key not found.", result.Reason); // The query filters by IsActive initially, so it won't be found
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnFalse_WhenKeyIsFoundButExpired()
    {
        // Arrange
        var rawKey = "12345678-secret";
        var prefix = "12345678";

        var keys = new List<McpAccessKey>
        {
            new() { KeyPrefix = prefix, KeyHash = GenerateTestHash(rawKey), IsActive = true, ExpiresAt = DateTime.UtcNow.AddMinutes(-5) }
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(keys);

        // Act
        var result = await _service.ValidateAsync(rawKey, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal("Key expired.", result.Reason);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnTrueAndDecryptedDetails_WhenKeyIsValid()
    {
        // Arrange
        var rawKey = "12345678-secret";
        var prefix = "12345678";

        var keys = new List<McpAccessKey>
        {
            new()
            {
                Id = 1,
                Name = "Valid Key",
                KeyPrefix = prefix,
                KeyHash = GenerateTestHash(rawKey),
                IsActive = true,
                SqlProvider = "PostgreSQL",
                SqlConnectionString = "ENCRYPTED_Server=localhost;",
                CorsAllowedOrigins = "http://localhost:3000,http://app.com"
            }
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(keys);

        // Act
        var result = await _service.ValidateAsync(rawKey, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsValid);
        Assert.Null(result.Reason);
        Assert.Equal(1, result.KeyId);
        Assert.Equal("Valid Key", result.Name);
        Assert.Equal("PostgreSQL", result.SqlProvider);
        Assert.Equal("Server=localhost;", result.SqlConnectionString);

        Assert.NotNull(result.CorsAllowedOriginsSet);
        Assert.Contains("http://localhost:3000", result.CorsAllowedOriginsSet);
        Assert.Contains("http://app.com", result.CorsAllowedOriginsSet);
    }

    #endregion

    #region RevokeKeyAsync Tests

    [Fact]
    public async Task RevokeKeyAsync_ShouldReturnFalse_WhenKeyNotFound()
    {
        // Arrange
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>());

        // Act
        var result = await _service.RevokeKeyAsync(999, "tester", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RevokeKeyAsync_ShouldDeactivateKeyAndReturnTrue_WhenKeyExists()
    {
        // Arrange
        var key = new McpAccessKey { Id = 1, IsActive = true };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey> { key });

        // Act
        var result = await _service.RevokeKeyAsync(1, "tester", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result);
        Assert.False(key.IsActive);
        Assert.NotNull(key.RevokedAt);
        Assert.Equal("tester", key.RevokedBy);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region TouchLastUsedAsync Tests

    [Fact]
    public async Task TouchLastUsedAsync_ShouldUpdateLastUsedAt_WhenKeyExists()
    {
        // Arrange
        var key = new McpAccessKey { Id = 1 };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey> { key });

        var beforeTouch = DateTime.UtcNow;

        // Act
        await _service.TouchLastUsedAsync(1, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(key.LastUsedAt);
        Assert.True(key.LastUsedAt >= beforeTouch);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TouchLastUsedAsync_ShouldNotThrowOrSave_WhenKeyDoesNotExist()
    {
        // Arrange
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>());

        // Act
        await _service.TouchLastUsedAsync(999, TestContext.Current.CancellationToken);

        // Assert
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion
}
