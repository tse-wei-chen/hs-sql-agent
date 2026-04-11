using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Services;
using Moq;
using Moq.EntityFrameworkCore;
using Xunit;

namespace Admin.Test.Services;

public class AuditServiceTests
{
    private readonly Mock<IAdminContext> _contextMock;
    private readonly AuditService _service;

    public AuditServiceTests()
    {
        _contextMock = new Mock<IAdminContext>();
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(new List<AuditLog>());
        _service = new AuditService(_contextMock.Object);
    }

    [Fact]
    public async Task WriteAsync_ShouldAddAuditLogAndSaveChanges()
    {
        await _service.WriteAsync("auth", "user1", "success", cancellationToken: TestContext.Current.CancellationToken);

        _contextMock.Verify(c => c.AuditLogs.Add(It.IsAny<AuditLog>()), Times.Once);
        _contextMock.Verify(c => c.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryAsync_ShouldReturnPagedResults()
    {
        var logs = new List<AuditLog>
        {
            new() { Id = 1, Action = "login", CreatedAt = DateTime.UtcNow },
            new() { Id = 2, Action = "logout", CreatedAt = DateTime.UtcNow }
        };
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(logs);

        var result = await _service.QueryAsync(1, 10, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result.Page);
        Assert.Equal(10, result.PageSize);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Count());
    }

    [Fact]
    public async Task QueryDailySummaryAsync_ShouldReturnBuckets()
    {
        var logs = new List<AuditLog>
        {
            new() { Id = 1, Result = "success", CreatedAt = DateTime.UtcNow },
            new() { Id = 2, Result = "failed", CreatedAt = DateTime.UtcNow }
        };
        _contextMock.Setup(c => c.AuditLogs).ReturnsDbSet(logs);

        var result = await _service.QueryDailySummaryAsync(7, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(7, result.Count);
        var today = result.Last();
        Assert.Equal(1, today.SuccessCount);
        Assert.Equal(1, today.FailedCount);
    }
}
