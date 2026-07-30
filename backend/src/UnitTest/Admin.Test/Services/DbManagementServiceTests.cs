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

public class DbManagementServiceTests
{
    private readonly Mock<IAdminContext> _contextMock;
    private readonly Mock<ICryptoService> _cryptoServiceMock;
    private readonly Mock<IOptions<McpKeySettings>> _mcpKeySettingsMock;
    private readonly DbManagementService _service;
    private readonly string _testHmacSecret = "test-secret-key-12345";
    private readonly byte[] _hmacSecretBytes;

    public DbManagementServiceTests()
    {
        _contextMock = new Mock<IAdminContext>();
        _cryptoServiceMock = new Mock<ICryptoService>();
        _mcpKeySettingsMock = new Mock<IOptions<McpKeySettings>>();

        _mcpKeySettingsMock.Setup(s => s.Value).Returns(new McpKeySettings
        {
            HmacSecretKey = _testHmacSecret
        });

        _hmacSecretBytes = Encoding.UTF8.GetBytes(_testHmacSecret);

        // Setup default DbManagement DbSet to avoid null reference exceptions in async queries
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement>());

        _service = new DbManagementService(_contextMock.Object, _cryptoServiceMock.Object, _mcpKeySettingsMock.Object);
    }

    [Fact]
    public async Task CreateDbAsync_ShouldReturnMappedViewModel_WhenInputIsValid()
    {
        // Arrange
        var request = new DbManagementRequest
        {
            Name = "TestDB",
            SqlProvider = "PostgreSQL",
            Host = "localhost",
            Port = "5432",
            Username = "admin",
            Password = "raw-password",
            Database = "test_db",
            CreatedBy = "user1",
            UpdatedBy = "user1"
        };

        var expectedEncryptedPassword = "encrypted-password";
        _cryptoServiceMock.Setup(c => c.EncryptText(request.Password, _hmacSecretBytes))
            .Returns(expectedEncryptedPassword);

        // Act
        var result = await _service.CreateDbAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(request.Name, result.Name);
        Assert.Equal(request.SqlProvider, result.SqlProvider);
        Assert.Equal(request.Host, result.Host);
        Assert.Equal(request.Port, result.Port);
        Assert.Equal(request.Username, result.Username);
        Assert.Equal(request.Database, result.Database);

        _contextMock.Verify(c => c.DbManagement.Add(It.Is<DbManagement>(db =>
            db.Name == request.Name &&
            db.SqlProvider == request.SqlProvider &&
            db.PasswordHash == expectedEncryptedPassword &&
            db.CreatedBy == request.CreatedBy
        )), Times.Once, "The entity was not added to the context with correct parameters.");

        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once, "SaveChanges was not called.");
    }

    [Fact]
    public async Task CreateDbAsync_ShouldRejectBlankPassword_ForCredentialBasedProvider()
    {
        var request = new DbManagementRequest
        {
            Name = "Postgres",
            SqlProvider = "Postgres",
            Password = " "
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateDbAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("Password is required", exception.Message);
        _cryptoServiceMock.Verify(
            c => c.EncryptText(It.IsAny<string>(), It.IsAny<byte[]>()),
            Times.Never);
        _contextMock.Verify(
            c => c.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateDbAsync_ShouldAllowBlankPassword_ForSqlite()
    {
        var request = new DbManagementRequest
        {
            Name = "Local",
            SqlProvider = "Sqlite",
            Database = "local.db"
        };
        _cryptoServiceMock
            .Setup(c => c.EncryptText(string.Empty, _hmacSecretBytes))
            .Returns("encrypted-empty-password");

        await _service.CreateDbAsync(request, TestContext.Current.CancellationToken);

        _contextMock.Verify(c => c.DbManagement.Add(It.Is<DbManagement>(
            db => db.PasswordHash == "encrypted-empty-password")), Times.Once);
        _contextMock.Verify(
            c => c.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetDbByIdAsync_ShouldReturnNull_WhenDbDoesNotExist()
    {
        // Arrange
        int nonExistentId = 999;
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement>());

        // Act
        var result = await _service.GetDbByIdAsync(nonExistentId, isPwd: true, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetDbByIdAsync_ShouldReturnPwdViewModel_WhenIsPwdIsTrueAndDbExists()
    {
        // Arrange
        int existingId = 1;
        var existingDb = new DbManagement
        {
            Id = existingId,
            Name = "SecretDB",
            PasswordHash = "hashed-secret"
        };
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement> { existingDb });

        // Act
        var result = await _service.GetDbByIdAsync(existingId, isPwd: true, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        var pwdResult = Assert.IsType<DbManagementPwdVM>(result);
        Assert.Equal(existingDb.Id, pwdResult.Id);
        Assert.Equal(existingDb.Name, pwdResult.Name);
        Assert.Equal(existingDb.PasswordHash, pwdResult.PasswordHash);
    }

    [Fact]
    public async Task GetDbByIdAsync_ShouldReturnStandardViewModel_WhenIsPwdIsFalseAndDbExists()
    {
        // Arrange
        int existingId = 2;
        var existingDb = new DbManagement
        {
            Id = existingId,
            Name = "PublicDB",
            PasswordHash = "hashed-secret"
        };
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement> { existingDb });

        // Act
        var result = await _service.GetDbByIdAsync(existingId, isPwd: false, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        var stdResult = Assert.IsType<DbManagementVM>(result);
        Assert.Equal(existingDb.Id, stdResult.Id);
        Assert.Equal(existingDb.Name, stdResult.Name);

        // Assert that PasswordHash is not present in standard VM
        var propertyInfo = typeof(DbManagementVM).GetProperty("PasswordHash");
        Assert.Null(propertyInfo);
    }

    [Fact]
    public async Task GetAllDbsAsync_ShouldReturnEmptyList_WhenNoDbsExist()
    {
        // Arrange
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement>());

        // Act
        var result = await _service.GetAllDbsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllDbsAsync_ShouldReturnAllItems_WhenDbsExist()
    {
        // Arrange
        var dbs = new List<DbManagement>
        {
            new() { Id = 1, Name = "DB1" },
            new() { Id = 2, Name = "DB2" }
        };
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(dbs);

        // Act
        var result = await _service.GetAllDbsAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == 1 && r.Name == "DB1");
        Assert.Contains(result, r => r.Id == 2 && r.Name == "DB2");
    }

    [Fact]
    public async Task UpdateDbAsync_ShouldUpdateExistingEntityAndSaveChanges_WhenDbExists()
    {
        // Arrange
        int existingId = 1;
        var existingDb = new DbManagement
        {
            Id = existingId,
            Name = "OldName",
            PasswordHash = "old-hash"
        };
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement> { existingDb });

        var request = new DbManagementRequest
        {
            Name = "NewName",
            Password = "new-password"
        };

        var expectedEncryptedPassword = "new-hash";
        _cryptoServiceMock.Setup(c => c.EncryptText(request.Password, _hmacSecretBytes))
            .Returns(expectedEncryptedPassword);

        // Act
        await _service.UpdateDbAsync(existingId, request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("NewName", existingDb.Name);
        Assert.Equal(expectedEncryptedPassword, existingDb.PasswordHash);

        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once, "SaveChanges was not called after updating entity.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateDbAsync_ShouldPreserveExistingPassword_WhenPasswordIsBlank(string password)
    {
        var existingDb = new DbManagement
        {
            Id = 1,
            Name = "OldName",
            PasswordHash = "existing-encrypted-password"
        };
        _contextMock.Setup(c => c.DbManagement)
            .ReturnsDbSet(new List<DbManagement> { existingDb });
        var request = new DbManagementRequest
        {
            Name = "NewName",
            Password = password
        };

        await _service.UpdateDbAsync(
            existingDb.Id,
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal("NewName", existingDb.Name);
        Assert.Equal("existing-encrypted-password", existingDb.PasswordHash);
        _cryptoServiceMock.Verify(
            c => c.EncryptText(It.IsAny<string>(), It.IsAny<byte[]>()),
            Times.Never);
        _contextMock.Verify(
            c => c.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateDbAsync_ShouldSilentlyFailAndNotSaveChanges_WhenDbDoesNotExist()
    {
        // Arrange
        int nonExistentId = 999;
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement>());
        var request = new DbManagementRequest { Name = "SomeName" };

        // Act
        await _service.UpdateDbAsync(nonExistentId, request, TestContext.Current.CancellationToken);

        // Assert
        _cryptoServiceMock.Verify(c => c.EncryptText(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never, "SaveChanges should not be called when DB does not exist.");
    }

    [Fact]
    public async Task DeleteDbAsync_ShouldRemoveEntityAndSaveChanges_WhenDbExists()
    {
        // Arrange
        int existingId = 1;
        var existingDb = new DbManagement { Id = existingId };
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement> { existingDb });

        // Act
        await _service.DeleteDbAsync(existingId, TestContext.Current.CancellationToken);

        // Assert
        _contextMock.Verify(c => c.DbManagement.Remove(existingDb), Times.Once, "Remove was not called on the DbSet.");
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once, "SaveChanges was not called after removal.");
    }

    [Fact]
    public async Task DeleteDbAsync_ShouldSilentlyFailAndNotSaveChanges_WhenDbDoesNotExist()
    {
        // Arrange
        int nonExistentId = 999;
        _contextMock.Setup(c => c.DbManagement).ReturnsDbSet(new List<DbManagement>());

        // Act
        await _service.DeleteDbAsync(nonExistentId, TestContext.Current.CancellationToken);

        // Assert
        _contextMock.Verify(c => c.DbManagement.Remove(It.IsAny<DbManagement>()), Times.Never, "Remove should not be called when DB does not exist.");
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never, "SaveChanges should not be called when DB does not exist.");
    }
}
