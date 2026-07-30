using System.Security.Cryptography;
using System.Text;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
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
    private readonly Mock<ICacheService> _cacheMock;
    private readonly Mock<ISecurityPolicyRuntimeState> _securityPolicyRuntimeStateMock;
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
        _cacheMock = new Mock<ICacheService>();
        _securityPolicyRuntimeStateMock = new Mock<ISecurityPolicyRuntimeState>();
        _securityPolicyRuntimeStateMock.Setup(x => x.GetCurrent()).Returns(new SecurityPolicyModel
        {
            KeyPermitLimit = 120,
            KeyWindowSeconds = 60
        });

        // Default empty DbSet to prevent null reference errors
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>());
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement>());

        _service = new McpAccessKeyService(
            _contextMock.Object,
            _settingsMock.Object,
            _cryptoServiceMock.Object,
            _cacheMock.Object,
            _securityPolicyRuntimeStateMock.Object);
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
            DbManagementId = 1,
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
        Assert.NotNull(result.PlaintextKey);

        mockDbSet.Verify(m => m.Add(It.Is<McpAccessKey>(e =>
            e.Name == "Test Key" &&
            e.CorsAllowedOrigins == "http://localhost:3000,https://api.example.com" &&
            e.DbManagementId == 1 &&
            e.CreatedBy == "tester" &&
            e.IsActive == true
        )), Times.Once);

        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IssueKeyAsync_ShouldPersistExplicitCustomRateLimit()
    {
        var request = new IssueMcpAccessKeyModel
        {
            Name = "Custom quota",
            RateLimitMode = McpKeyRateLimitMode.Custom,
            PermitLimitOverride = 500,
            WindowSecondsOverride = 30
        };
        var mockDbSet = new Mock<Microsoft.EntityFrameworkCore.DbSet<McpAccessKey>>();
        _contextMock.Setup(c => c.McpAccessKeys).Returns(mockDbSet.Object);

        var result = await _service.IssueKeyAsync(request, "tester", TestContext.Current.CancellationToken);

        Assert.Equal(McpKeyRateLimitMode.Custom, result.RateLimitMode);
        Assert.Equal(500, result.PermitLimitOverride);
        Assert.Equal(30, result.WindowSecondsOverride);
        mockDbSet.Verify(x => x.Add(It.Is<McpAccessKey>(key =>
            key.RateLimitMode == McpKeyRateLimitMode.Custom &&
            key.PermitLimitOverride == 500 &&
            key.WindowSecondsOverride == 30)), Times.Once);
    }

    [Fact]
    public async Task IssueKeyAsync_ShouldRejectIncompleteCustomRateLimit()
    {
        var request = new IssueMcpAccessKeyModel
        {
            Name = "Broken quota",
            RateLimitMode = McpKeyRateLimitMode.Custom,
            PermitLimitOverride = 10
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.IssueKeyAsync(request, "tester", TestContext.Current.CancellationToken));

        Assert.Contains("WindowSecondsOverride", exception.Message);
    }

    #endregion

    #region ListKeysAsync Tests

    [Fact]
    public async Task ListKeysAsync_ShouldReportEffectiveStatusAndDatabase()
    {
        var keys = new List<McpAccessKey>
        {
            new()
            {
                Id = 1,
                Name = "usable",
                KeyPrefix = "usable01",
                IsActive = true,
                DbManagementId = 10,
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                Id = 2,
                Name = "expired",
                KeyPrefix = "expired1",
                IsActive = true,
                DbManagementId = 10,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            }
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(keys);
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement>
        {
            new() { Id = 10, Name = "Orders", SqlProvider = "PostgreSQL" }
        });

        var result = await _service.ListKeysAsync(TestContext.Current.CancellationToken);

        var usable = Assert.Single(result, x => x.Id == 1);
        Assert.True(usable.IsActive);
        Assert.False(usable.IsExpired);
        Assert.Equal("Orders", usable.DbManagementName);
        Assert.Equal("PostgreSQL", usable.SqlProvider);
        Assert.Equal(McpKeyRateLimitMode.Inherit, usable.RateLimitMode);
        Assert.Equal(120, usable.EffectivePermitLimit);
        Assert.Equal(60, usable.EffectiveWindowSeconds);

        var expired = Assert.Single(result, x => x.Id == 2);
        Assert.False(expired.IsActive);
        Assert.True(expired.IsExpired);
    }

    [Fact]
    public async Task ListKeysAsync_ShouldKeepMissingDatabaseIdVisible()
    {
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>
        {
            new()
            {
                Id = 1,
                Name = "orphan",
                KeyPrefix = "orphan01",
                IsActive = false,
                DbManagementId = 99,
                CreatedAt = DateTime.UtcNow
            }
        });

        var item = Assert.Single(await _service.ListKeysAsync(TestContext.Current.CancellationToken));

        Assert.Equal(99, item.DbManagementId);
        Assert.Null(item.DbManagementName);
    }

    [Fact]
    public async Task ListKeysAsync_ShouldFlagActiveKeysExpiringWithinSevenDays()
    {
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>
        {
            new()
            {
                Id = 1, Name = "soon", KeyPrefix = "soon-key", IsActive = true,
                ExpiresAt = DateTime.UtcNow.AddDays(3), CreatedAt = DateTime.UtcNow
            },
            new()
            {
                Id = 2, Name = "later", KeyPrefix = "laterkey", IsActive = true,
                ExpiresAt = DateTime.UtcNow.AddDays(10), CreatedAt = DateTime.UtcNow
            }
        });

        var result = await _service.ListKeysAsync(TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(result, x => x.Id == 1).IsExpiringSoon);
        Assert.False(Assert.Single(result, x => x.Id == 2).IsExpiringSoon);
    }

    #endregion

    #region Lifecycle Tests

    [Fact]
    public async Task UpdateKeyAsync_ShouldUpdateConfigurationAndInvalidateValidationCache()
    {
        var key = new McpAccessKey
        {
            Id = 1, Name = "old", KeyPrefix = "12345678",
            KeyHash = GenerateTestHash("12345678-secret"), IsActive = true
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey> { key });

        var result = await _service.UpdateKeyAsync(
            1,
            new UpdateMcpAccessKeyRequest
            {
                Name = "new",
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                AllowedTools = "get_tables,get_columns",
                CorsAllowedOrigins = "HTTPS://APP.EXAMPLE.COM/",
                DbManagementId = 5,
                TableWhitelist = "public.users"
            },
            "tester",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("new", key.Name);
        Assert.Equal("https://app.example.com", key.CorsAllowedOrigins);
        Assert.Equal(5, key.DbManagementId);
        _cacheMock.Verify(c => c.RemoveAsync(
            McpAccessKeyCacheKeys.ForStoredHash(key.KeyHash),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task RotateKeyAsync_ShouldIssueReplacementAndImmediatelyRevokeOldKey()
    {
        var key = new McpAccessKey
        {
            Id = 1, Name = "production", KeyPrefix = "12345678",
            KeyHash = GenerateTestHash("12345678-secret"), IsActive = true,
            AllowedTools = "get_tables", DbManagementId = 5
        };
        var keys = new List<McpAccessKey> { key };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(keys);

        var result = await _service.RotateKeyAsync(
            1,
            new RotateMcpAccessKeyRequest
            {
                GracePeriodMinutes = 0,
                ExpiresAt = DateTime.UtcNow.AddDays(30)
            },
            "tester",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotEmpty(result.PlaintextKey);
        Assert.Equal("get_tables", result.AllowedTools);
        Assert.False(key.IsActive);
        Assert.NotNull(key.RevokedAt);
        _cacheMock.Verify(c => c.SetAsync(
            McpAccessKeyCacheKeys.ForRevokedKeyId(1),
            true,
            It.IsAny<TimeSpan?>(),
            CancellationToken.None), Times.Once);
        _cacheMock.Verify(c => c.RemoveAsync(
            McpAccessKeyCacheKeys.ForStoredHash(key.KeyHash),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task RotateKeyAsync_ShouldKeepOldKeyActiveOnlyForGracePeriod()
    {
        var key = new McpAccessKey
        {
            Id = 1, Name = "production", KeyPrefix = "12345678",
            KeyHash = GenerateTestHash("12345678-secret"), IsActive = true
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey> { key });
        var before = DateTime.UtcNow.AddMinutes(14);

        await _service.RotateKeyAsync(
            1,
            new RotateMcpAccessKeyRequest { GracePeriodMinutes = 15 },
            "tester",
            TestContext.Current.CancellationToken);

        Assert.True(key.IsActive);
        Assert.Null(key.RevokedAt);
        Assert.True(key.ExpiresAt >= before && key.ExpiresAt <= DateTime.UtcNow.AddMinutes(16));
        _cacheMock.Verify(c => c.SetAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _cacheMock.Verify(c => c.RemoveAsync(
            McpAccessKeyCacheKeys.ForStoredHash(key.KeyHash),
            CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task CloneKeyAsync_ShouldCopyConfigurationAndReturnOneTimeSecret()
    {
        var source = new McpAccessKey
        {
            Id = 1, Name = "source", KeyPrefix = "12345678",
            KeyHash = GenerateTestHash("12345678-secret"), IsActive = false,
            AllowedTools = "get_tables", CorsAllowedOrigins = "https://app.example.com",
            DbManagementId = 5, TableWhitelist = "public.users",
            RateLimitMode = McpKeyRateLimitMode.Unlimited
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey> { source });

        var result = await _service.CloneKeyAsync(
            1,
            new CloneMcpAccessKeyRequest
            {
                Name = "copy",
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            },
            "tester",
            TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("copy", result.Name);
        Assert.Equal("get_tables", result.AllowedTools);
        Assert.Equal("https://app.example.com", result.CorsAllowedOrigins);
        Assert.Equal(5, result.DbManagementId);
        Assert.Equal("public.users", result.TableWhitelist);
        Assert.Equal(McpKeyRateLimitMode.Unlimited, result.RateLimitMode);
        Assert.NotEmpty(result.PlaintextKey);
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
        var expiresAt = DateTime.UtcNow.AddHours(1);

        var keys = new List<McpAccessKey>
        {
            new()
            {
                Id = 1,
                Name = "Valid Key",
                KeyPrefix = prefix,
                KeyHash = GenerateTestHash(rawKey),
                IsActive = true,
                ExpiresAt = expiresAt,
                DbManagementId = 10,
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
        Assert.Equal(10, result.DbManagementId);
        Assert.Equal(expiresAt, result.ExpiresAt);

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
        var key = new McpAccessKey
        {
            Id = 1,
            IsActive = true,
            KeyHash = GenerateTestHash("12345678-secret")
        };
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey> { key });

        // Act
        var result = await _service.RevokeKeyAsync(1, "tester", TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result);
        Assert.False(key.IsActive);
        Assert.NotNull(key.RevokedAt);
        Assert.Equal("tester", key.RevokedBy);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _cacheMock.Verify(c => c.SetAsync(
            McpAccessKeyCacheKeys.ForRevokedKeyId(key.Id),
            true,
            It.Is<TimeSpan?>(expiry =>
                expiry.HasValue &&
                expiry.Value > TimeSpan.FromMinutes(5)),
            CancellationToken.None), Times.Once);
        _cacheMock.Verify(c => c.RemoveAsync(
            McpAccessKeyCacheKeys.ForStoredHash(key.KeyHash),
            CancellationToken.None), Times.Once);
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
