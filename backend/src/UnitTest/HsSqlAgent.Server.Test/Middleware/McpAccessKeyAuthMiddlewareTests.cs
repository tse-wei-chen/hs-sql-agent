using System.Net;
using System.Text;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using HsSqlAgent.Server.Background;
using HsSqlAgent.Server.Middleware;
using Xunit;

namespace HsSqlAgent.Server.Test.Middleware;

public class McpAccessKeyAuthMiddlewareTests
{
    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void GetStripedLockIndex_AlwaysReturnsValidIndex(int hashCode)
    {
        var index = McpAccessKeyAuthMiddleware.GetStripedLockIndex(hashCode, 64);

        Assert.InRange(index, 0, 63);
    }

    private readonly Mock<IMcpAccessKeyService> _keyServiceMock;
    private readonly Mock<IAuditService> _auditServiceMock;
    private readonly Mock<IMcpAccessKeyLastUsedQueue> _lastUsedQueueMock;
    private readonly Mock<IDbManagementService> _dbManagementServiceMock;
    private readonly Mock<IDbSetterService> _dbSetterServiceMock;
    private readonly Mock<ICryptoService> _cryptoServiceMock;
    private readonly Mock<ICacheService> _cacheMock;
    private readonly IOptions<McpKeySettings> _settings;
    private readonly Mock<ILogger<McpAccessKeyAuthMiddleware>> _loggerMock;
    private readonly McpAccessKeyAuthMiddleware _middleware;

    public McpAccessKeyAuthMiddlewareTests()
    {
        _keyServiceMock = new Mock<IMcpAccessKeyService>();
        _auditServiceMock = new Mock<IAuditService>();
        _lastUsedQueueMock = new Mock<IMcpAccessKeyLastUsedQueue>();
        _cacheMock = new Mock<ICacheService>();
        _dbManagementServiceMock = new Mock<IDbManagementService>();
        _dbSetterServiceMock = new Mock<IDbSetterService>();
        _cryptoServiceMock = new Mock<ICryptoService>();
        _settings = Options.Create(new McpKeySettings { HmacSecretKey = "test-secret-key-at-least-32-bytes-long!" });
        _loggerMock = new Mock<ILogger<McpAccessKeyAuthMiddleware>>();

        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c.GetSection("SqlConfig")).Returns(new Mock<IConfigurationSection>().Object);

        _middleware = new McpAccessKeyAuthMiddleware(
            _keyServiceMock.Object,
            _auditServiceMock.Object,
            _lastUsedQueueMock.Object,
            _cacheMock.Object,
            configMock.Object,
            _dbManagementServiceMock.Object,
            _dbSetterServiceMock.Object,
            _cryptoServiceMock.Object,
            _settings,
            _loggerMock.Object
        );
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn401_WhenMissingKey()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Response.Body = new MemoryStream();

        RequestDelegate next = _ => Task.CompletedTask;

        await _middleware.InvokeAsync(context, next);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn401_WhenInvalidKey()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer invalid-key-format";
        context.Response.Body = new MemoryStream();

        _keyServiceMock.Setup(k => k.ValidateAsync("invalid-key-format", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpAccessKeyValidationResult { IsValid = false, Reason = "Key not found." });

        RequestDelegate next = _ => Task.CompletedTask;

        await _middleware.InvokeAsync(context, next);

        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenValidKey()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer valid-prefix-validkey123";
        context.Response.Body = new MemoryStream();

        var validationResult = new McpAccessKeyValidationResult
        {
            IsValid = true,
            KeyId = 1,
            Name = "Test Key",
            DbManagementId = 10,
            AllowedTools = "execute_query_sql,get_tables",
            CorsAllowedOrigins = "http://localhost:3000",
            TableWhitelist = "dbo.users,dbo.orders"
        };

        _keyServiceMock.Setup(k => k.ValidateAsync("valid-prefix-validkey123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResult);

        _lastUsedQueueMock.Setup(q => q.TryEnqueue(1)).Returns(true);

        var dbc = new DbManagementPwdVM
        {
            Id = 10,
            Name = "TestDB",
            SqlProvider = "PostgreSQL",
            Host = "localhost",
            Port = "5432",
            Database = "testdb",
            Username = "admin",
            PasswordHash = "encrypted-password"
        };
        _dbManagementServiceMock.Setup(d => d.GetDbByIdAsync(10, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dbc);
        _dbSetterServiceMock.Setup(d => d.BuildDbConnectionAsync(It.IsAny<BuildDbConnectionModel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Host=localhost;Port=5432;Database=testdb;Username=admin;Password=decrypted");

        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            Assert.Equal("PostgreSQL", ctx.Items[Common.Models.McpContextItemKeys.SqlProvider]);
            Assert.Equal("Host=localhost;Port=5432;Database=testdb;Username=admin;Password=decrypted", ctx.Items[Common.Models.McpContextItemKeys.SqlConnectionString]);
            Assert.Equal(1, ctx.Items[Common.Models.McpContextItemKeys.AccessKeyId]);
            Assert.Equal(10, ctx.Items[Common.Models.McpContextItemKeys.DbManagementId]);
            Assert.Equal("execute_query_sql,get_tables", ctx.Items[Common.Models.McpContextItemKeys.AllowedTools]);
            Assert.Equal("dbo.users,dbo.orders", ctx.Items[Common.Models.McpContextItemKeys.TableWhitelist]);
            return Task.CompletedTask;
        };

        await _middleware.InvokeAsync(context, next);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldUseXMcpServerKeyHeader_WhenAuthorizationIsMissing()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers["X-MCP-Server-Key"] = "header-key-value";
        context.Response.Body = new MemoryStream();

        _keyServiceMock.Setup(k => k.ValidateAsync("header-key-value", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpAccessKeyValidationResult { IsValid = true, KeyId = 2, Name = "Header Key" });

        _lastUsedQueueMock.Setup(q => q.TryEnqueue(2)).Returns(true);

        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        await _middleware.InvokeAsync(context, next);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn403_WhenCorsOriginNotAllowed()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer test-key";
        context.Request.Headers.Origin = "http://evil-site.com";
        context.Response.Body = new MemoryStream();

        _keyServiceMock.Setup(k => k.ValidateAsync("test-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpAccessKeyValidationResult
            {
                IsValid = true,
                KeyId = 1,
                Name = "Restricted Key",
                CorsAllowedOrigins = "http://localhost:3000",
                CorsAllowedOriginsSet = new HashSet<string> { "http://localhost:3000" }
            });

        _lastUsedQueueMock.Setup(q => q.TryEnqueue(1)).Returns(true);

        RequestDelegate next = _ => Task.CompletedTask;

        await _middleware.InvokeAsync(context, next);

        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn401_WhenCachedKeyHasExpired()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer cached-expired-key";
        context.Response.Body = new MemoryStream();

        _cacheMock.Setup(c => c.GetAsync<McpAccessKeyValidationResult>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpAccessKeyValidationResult
            {
                IsValid = true,
                KeyId = 1,
                ExpiresAt = DateTime.UtcNow.AddSeconds(-1)
            });

        await _middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        _keyServiceMock.Verify(
            k => k.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturn401_WhenCachedKeyWasRevoked()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer cached-revoked-key";
        context.Response.Body = new MemoryStream();

        _cacheMock.Setup(c => c.GetAsync<McpAccessKeyValidationResult>(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpAccessKeyValidationResult
            {
                IsValid = true,
                KeyId = 42
            });
        _cacheMock.Setup(c => c.GetAsync<bool>(
                McpAccessKeyCacheKeys.ForRevokedKeyId(42),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        _keyServiceMock.Verify(
            k => k.ValidateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_ShouldNotCacheValidKeyBeyondItsExpiration()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer short-lived-key";
        context.Response.Body = new MemoryStream();
        var expiresAt = DateTime.UtcNow.AddSeconds(30);
        TimeSpan? capturedCacheExpiry = null;

        _keyServiceMock.Setup(k => k.ValidateAsync("short-lived-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpAccessKeyValidationResult
            {
                IsValid = true,
                KeyId = 1,
                ExpiresAt = expiresAt
            });
        _cacheMock.Setup(c => c.SetAsync(
                It.IsAny<string>(),
                It.IsAny<McpAccessKeyValidationResult>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, McpAccessKeyValidationResult, TimeSpan?, CancellationToken>(
                (_, _, expiry, _) => capturedCacheExpiry = expiry)
            .Returns(Task.CompletedTask);
        _lastUsedQueueMock.Setup(q => q.TryEnqueue(1)).Returns(true);

        await _middleware.InvokeAsync(context, _ => Task.CompletedTask);

        Assert.NotNull(capturedCacheExpiry);
        Assert.True(capturedCacheExpiry > TimeSpan.Zero);
        Assert.True(DateTime.UtcNow + capturedCacheExpiry <= expiresAt.AddMilliseconds(100));
        Assert.True(capturedCacheExpiry < TimeSpan.FromMinutes(5));
    }
}
