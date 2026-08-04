using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Admin.Service.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Admin.Test.Services;

public class OperabilityServiceTests
{
    [Fact]
    public async Task MetricsAndKeyUsage_ShouldAggregateLatencySuccessAndRateLimits()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options;
        await using var context = new AdminContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var now = DateTime.UtcNow;
        context.McpAccessKeys.Add(new McpAccessKey
        {
            Id = 1, Name = "reporting", KeyPrefix = "prefix", KeyHash = "hash", CreatedAt = now
        });
        context.AuditLogs.AddRange(
            new AuditLog { EventId = Guid.NewGuid(), Action = "mcp.query.executed", Target = "a", Result = "success", DurationMs = 10, AccessKeyId = 1, CreatedAt = now },
            new AuditLog { EventId = Guid.NewGuid(), Action = "mcp.query.executed", Target = "b", Result = "failed", DurationMs = 100, AccessKeyId = 1, CreatedAt = now },
            new AuditLog { EventId = Guid.NewGuid(), Action = "mcp.dml.executed", Target = "c", Result = "success", DurationMs = 1000, AccessKeyId = 1, CreatedAt = now });
        context.RateLimitMetrics.Add(new RateLimitMetric { BucketStart = now, Layer = "key", AccessKeyId = 1, RejectedCount = 2 });
        context.DbManagement.Add(new DbManagement { Id = 7, Name = "warehouse", SqlProvider = "Postgres", CreatedAt = now, UpdatedAt = now });
        context.DbHealthStates.Add(new DbHealthState { DbManagementId = 7, Status = "unhealthy", ConsecutiveFailures = 3, LastCheckedAt = now, OutageStartedAt = now.AddMinutes(-3) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new OperabilityService(context, Options.Create(new OperabilitySettings { SlowQueryThresholdMs = 500 }));

        var metrics = await service.GetMetricsAsync(new OperabilityFilter { From = now.AddMinutes(-1), To = now.AddMinutes(1) }, TestContext.Current.CancellationToken);
        var usage = Assert.Single(await service.GetKeyUsageAsync(new OperabilityFilter { From = now.AddMinutes(-1), To = now.AddMinutes(1) }, TestContext.Current.CancellationToken));
        var health = Assert.Single(await service.GetDbHealthAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, metrics.QueryCount);
        Assert.Equal(1, metrics.DmlCount);
        Assert.Equal(100, metrics.P50LatencyMs);
        Assert.Equal(1, metrics.SlowQueryCount);
        Assert.Equal(2, metrics.KeyRateLimitCount);
        Assert.Equal(3, usage.RequestCount);
        Assert.Equal(2, usage.RateLimitCount);
        Assert.Equal(2, usage.SuccessCount);
        Assert.Equal(1, usage.FailureCount);
        Assert.Equal(0.4, usage.RateLimitRejectionRate, 3);
        Assert.Equal("unhealthy", health.Status);
        Assert.Equal(3, health.ConsecutiveFailures);
    }
}
