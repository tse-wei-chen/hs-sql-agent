using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Admin.Service.Models;
using Microsoft.Extensions.Options;

namespace Admin.Test.Services;

public class AuditServiceTests
{
    private readonly Mock<IAdminContext> _contextMock;
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock;
    private readonly AuditService _service;

    public AuditServiceTests()
    {
        _contextMock = new Mock<IAdminContext>();
        _httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(new List<AuditLog>());
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _service = new AuditService(_contextMock.Object, _httpContextAccessorMock.Object);
    }

    #region WriteAsync Tests

    [Fact]
    public async Task WriteAsync_ShouldSetDefaultActorTypeToSystem_WhenActorTypeIsNullOrWhitespace()
    {
        // Act
        await _service.WriteAsync("login", "target_user", "success", null, "   ", null, null, null, CancellationToken.None);

        // Assert
        _contextMock.Verify(c => c.AuditLogs.Add(It.Is<AuditLog>(a =>
            a.Action == "login" &&
            a.Target == "target_user" &&
            a.Result == "success" &&
            a.ActorType == "system" // Default fallback
        )), Times.Once);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteAsync_ShouldMapAllPropertiesCorrectly_WhenAllProvided()
    {
        // Act
        await _service.WriteAsync("query", "db1", "failed", "details", "user", "u1", "127.0.0.1", "Mozilla", CancellationToken.None);

        // Assert
        _contextMock.Verify(c => c.AuditLogs.Add(It.Is<AuditLog>(a =>
            a.Action == "query" &&
            a.Target == "db1" &&
            a.Result == "failed" &&
            a.Detail == "details" &&
            a.ActorType == "user" &&
            a.ActorId == "u1" &&
            a.IpAddress == "127.0.0.1" &&
            a.UserAgent == "Mozilla" &&
            a.EventId != Guid.Empty &&
            a.CreatedAt != default
        )), Times.Once);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WriteAsync_ShouldRetryTransientPersistenceFailures()
    {
        var attempts = 0;
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attempts++;
                return attempts < 3
                    ? Task.FromException<int>(new InvalidOperationException("temporary"))
                    : Task.FromResult(1);
            });

        await _service.WriteAsync(
            "query",
            "db1",
            "success",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, attempts);
        _contextMock.Verify(
            c => c.AuditLogs.Add(It.Is<AuditLog>(x => x.EventId != Guid.Empty)),
            Times.Once);
    }

    [Fact]
    public async Task WriteAsync_ShouldRedactSecretsFromDetail()
    {
        await _service.WriteAsync(
            "query",
            "db1",
            "failed",
            "Password=hunter2; Authorization: Bearer abc.def; token=secret-token",
            cancellationToken: TestContext.Current.CancellationToken);

        _contextMock.Verify(c => c.AuditLogs.Add(It.Is<AuditLog>(x =>
            x.Detail != null &&
            !x.Detail.Contains("hunter2") &&
            !x.Detail.Contains("abc.def") &&
            !x.Detail.Contains("secret-token") &&
            x.Detail.Contains("[REDACTED]"))), Times.Once);
    }

    [Fact]
    public async Task WriteAsync_ShouldPropagateFailureAfterRetryLimit()
    {
        _contextMock.Setup(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.WriteAsync(
                "query",
                "db1",
                "success",
                cancellationToken: TestContext.Current.CancellationToken));

        _contextMock.Verify(
            c => c.SaveChangesAsync(TestContext.Current.CancellationToken),
            Times.Exactly(3));
    }

    #endregion

    [Fact]
    public async Task WriteAsync_ShouldQueueSignedSiemDelivery_WhenConfigured()
    {
        _contextMock.Setup(c => c.OutboundDeliveries).ReturnsDbSet(new List<OutboundDelivery>());
        var service = new AuditService(
            _contextMock.Object,
            _httpContextAccessorMock.Object,
            Options.Create(new OperabilitySettings { SiemWebhookUrl = "https://siem.example/events" }));

        await service.WriteAsync("query", "db1", "success", cancellationToken: TestContext.Current.CancellationToken);

        _contextMock.Verify(c => c.OutboundDeliveries.Add(It.Is<OutboundDelivery>(x =>
            x.Category == "siem" && x.TargetUrl == "https://siem.example/events" && x.Status == "pending")), Times.Once);
    }

    #region WriteLogAsync Tests

    [Fact]
    public async Task WriteEventAsync_ShouldPublishExecutionToMetricSinks()
    {
        var sink = new Mock<IAuditMetricSink>();
        var service = new AuditService(
            _contextMock.Object,
            _httpContextAccessorMock.Object,
            metricSinks: [sink.Object]);
        var eventContext = new AuditEventContext
        {
            ToolName = "execute_query_sql",
            Operation = "select",
            DurationMs = 42
        };

        await service.WriteEventAsync(
            "mcp.query.executed",
            "users",
            "success",
            eventContext,
            cancellationToken: TestContext.Current.CancellationToken);

        sink.Verify(x => x.Record("mcp.query.executed", "success", eventContext), Times.Once);
    }

    [Fact]
    public async Task WriteLogAsync_ShouldCaptureContextInfo_WhenHttpContextIsAvailable()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.1");
        context.Request.Headers.UserAgent = "TestAgent";

        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "user-123") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims));

        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(context);

        // Act
        await _service.WriteLogAsync("test.action", "test.target", "success", "test.detail", TestContext.Current.CancellationToken);

        // Assert
        _contextMock.Verify(c => c.AuditLogs.Add(It.Is<AuditLog>(a =>
            a.Action == "test.action" &&
            a.Target == "test.target" &&
            a.Result == "success" &&
            a.Detail == "test.detail" &&
            a.ActorType == "admin" &&
            a.ActorId == "user-123" &&
            a.IpAddress == "192.168.1.1" &&
            a.UserAgent == "TestAgent" &&
            a.RequestId == context.TraceIdentifier
        )), Times.Once);
    }

    [Fact]
    public async Task WriteLogAsync_ShouldFallbackToMcpKey_WhenUserIsMissingButItemsHaveKeyId()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 42;
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(context);

        // Act
        await _service.WriteLogAsync("mcp.action", "mcp.target", "success", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        _contextMock.Verify(c => c.AuditLogs.Add(It.Is<AuditLog>(a =>
            a.Action == "mcp.action" &&
            a.ActorType == "mcp-key" &&
            a.ActorId == "42"
        )), Times.Once);
    }

    [Fact]
    public async Task WriteEventAsync_ShouldPersistStructuredContext()
    {
        var context = new DefaultHttpContext();
        context.Items[Common.Models.McpContextItemKeys.AccessKeyId] = 42;
        context.Items[Common.Models.McpContextItemKeys.DbManagementId] = 7;
        context.Items[Common.Models.McpContextItemKeys.DatabaseName] = "sales";
        context.Request.Headers["Mcp-Session-Id"] = "session-1";
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(context);

        await _service.WriteEventAsync(
            "mcp.query.executed",
            "orders",
            "success",
            new Admin.Service.Models.AuditEventContext
            {
                ToolName = "execute_query_sql",
                Operation = "select",
                DurationMs = 18,
                ReturnedRows = 3,
                Definition = """{"tableName":"orders","whereConditionCount":1}"""
            },
            cancellationToken: TestContext.Current.CancellationToken);

        _contextMock.Verify(c => c.AuditLogs.Add(It.Is<AuditLog>(a =>
            a.AccessKeyId == 42 &&
            a.DbManagementId == 7 &&
            a.DatabaseName == "sales" &&
            a.SessionId == "session-1" &&
            a.ToolName == "execute_query_sql" &&
            a.ReturnedRows == 3 &&
            a.DurationMs == 18)), Times.Once);
    }

    #endregion

    #region QueryAsync Tests

    [Theory]
    [InlineData(0, 0, 1, 20)] // Zero mapping
    [InlineData(-5, -10, 1, 20)] // Negative mapping
    [InlineData(5, 500, 5, 200)] // Upper bound pageSize mapping
    public async Task QueryAsync_ShouldNormalizePageAndPageSize(int inputPage, int inputPageSize, int expectedPage, int expectedPageSize)
    {
        // Arrange
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(new List<AuditLog>());

        // Act
        var result = await _service.QueryAsync(inputPage, inputPageSize, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedPage, result.Page);
        Assert.Equal(expectedPageSize, result.PageSize);
    }

    [Fact]
    public async Task QueryAsync_ShouldFilterByActionAndKeyword_WhenProvided()
    {
        // Arrange
        var logs = new List<AuditLog>
        {
            new() { Id = 1, Action = "login", Target = "user1" },
            new() { Id = 2, Action = "query", Target = "db1", Detail = "select * from test" },
            new() { Id = 3, Action = "query", ActorId = "db-admin" },
        };
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(logs);

        // Act - filter by action "query" and keyword "db"
        var result = await _service.QueryAsync(1, 10, action: "query", keyword: "db", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, result.TotalCount); // Should match id 2 (Target="db1") and id 3 (ActorId="db-admin")
        Assert.Contains(result.Items, x => x.Id == 2);
        Assert.Contains(result.Items, x => x.Id == 3);
    }

    [Fact]
    public async Task QueryAsync_ShouldApplyStructuredFilters()
    {
        var now = DateTime.UtcNow;
        var logs = new List<AuditLog>
        {
            new()
            {
                Id = 1, Action = "query", Result = "success", ActorId = "user-1",
                DbManagementId = 7, AccessKeyId = 42, ToolName = "execute_query_sql",
                CreatedAt = now
            },
            new()
            {
                Id = 2, Action = "query", Result = "failed", ActorId = "user-2",
                DbManagementId = 8, AccessKeyId = 43, ToolName = "custom_report",
                CreatedAt = now
            }
        };
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(logs);

        var result = await _service.QueryAsync(
            new Admin.Service.Models.AuditLogFilter
            {
                Result = "success",
                Actor = "user-1",
                DbManagementId = 7,
                AccessKeyId = 42,
                ToolName = "execute_query_sql",
                From = now.AddMinutes(-1),
                To = now.AddMinutes(1)
            },
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(1, item.Id);
    }

    #endregion

    #region QueryDailySummaryAsync Tests

    [Theory]
    [InlineData(0, 7)]
    [InlineData(-5, 7)]
    [InlineData(100, 30)] // Max cap is 30 days
    public async Task QueryDailySummaryAsync_ShouldNormalizeDays(int inputDays, int expectedDays)
    {
        // Arrange
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(new List<AuditLog>());

        // Act
        var result = await _service.QueryDailySummaryAsync(inputDays, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedDays, result.Count);
    }

    [Fact]
    public async Task QueryDailySummaryAsync_ShouldAggregateSuccessAndFailedCaseInsensitive()
    {
        // Arrange
        var today = DateTime.UtcNow.Date;
        var logs = new List<AuditLog>
        {
            new() { Id = 1, Result = "SUCCESS", CreatedAt = today.AddHours(1) },
            new() { Id = 2, Result = "success", CreatedAt = today.AddHours(2) },
            new() { Id = 3, Result = "FAILED", CreatedAt = today.AddHours(3) },
            new() { Id = 4, Result = "Unknown", CreatedAt = today.AddHours(4) }, // Falls into failed category
        };
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(logs);

        // Act
        var result = await _service.QueryDailySummaryAsync(1, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var summary = Assert.Single(result);
        Assert.Equal(today, summary.Day);
        Assert.Equal(2, summary.SuccessCount); // "SUCCESS" and "success"
        Assert.Equal(2, summary.FailedCount); // "FAILED" and "Unknown"
    }

    [Fact]
    public async Task QueryDailySummaryAsync_ShouldApplyActionAndKeywordFilters()
    {
        // Arrange
        var today = DateTime.UtcNow.Date;
        var logs = new List<AuditLog>
        {
            new() { Id = 1, Action = "query", Target = "t1", Result = "success", CreatedAt = today },
            new() { Id = 2, Action = "query", Target = "match", Result = "success", CreatedAt = today },
            new() { Id = 3, Action = "login", Target = "match", Result = "success", CreatedAt = today },
        };
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(logs);

        // Act
        var result = await _service.QueryDailySummaryAsync(1, action: "query", keyword: "match", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var summary = Assert.Single(result);
        Assert.Equal(1, summary.SuccessCount); // Only Id 2 matches both action="query" and keyword="match"
        Assert.Equal(0, summary.FailedCount);
    }

    #endregion
}
