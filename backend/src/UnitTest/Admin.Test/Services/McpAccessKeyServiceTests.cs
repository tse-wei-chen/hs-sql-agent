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

    public McpAccessKeyServiceTests()
    {
        _contextMock = new Mock<IAdminContext>();
        
        _settingsMock = new Mock<IOptions<McpKeySettings>>();
        _settingsMock.Setup(s => s.Value).Returns(new McpKeySettings { HmacSecretKey = "12345678901234567890123456789012" });

        _cryptoServiceMock = new Mock<ICryptoService>();
        _cryptoServiceMock.Setup(c => c.EncryptText(It.IsAny<string>(), It.IsAny<byte[]>()))
            .Returns((string? plain, byte[] key) => plain != null ? $"ENCRYPTED_{plain}" : null);
        _cryptoServiceMock.Setup(c => c.DecryptText(It.IsAny<string>(), It.IsAny<byte[]>()))
            .Returns((string? cipher, byte[] key) => cipher?.Replace("ENCRYPTED_", ""));

        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>());

        _service = new McpAccessKeyService(_contextMock.Object, _settingsMock.Object, _cryptoServiceMock.Object);
    }

    [Fact]
    public async Task IssueKeyAsync_WithValidRequest_ReturnsResult()
    {
        var request = new IssueMcpAccessKeyRequest
        {
            Name = "Test Key",
            SqlProvider = "PostgreSQL",
            SqlConnectionString = "Host=localhost;Database=test",
            CorsAllowedOrigins = "http://localhost:3000"
        };

        var result = await _service.IssueKeyAsync(request, "tester", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Test Key", result.Name);
        Assert.NotNull(result.PlaintextKey);
        _contextMock.Verify(c => c.McpAccessKeys.Add(It.IsAny<McpAccessKey>()), Times.Once);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_WithMissingKey_ReturnsFalse()
    {
        _contextMock.Setup(c => c.McpAccessKeys).ReturnsDbSet(new List<McpAccessKey>());

        var result = await _service.ValidateAsync("invalid-key", TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal("Key not found.", result.Reason);
    }
}
