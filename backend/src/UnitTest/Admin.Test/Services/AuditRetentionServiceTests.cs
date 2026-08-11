using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Admin.Service.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Admin.Test.Services;

public class AuditRetentionServiceTests
{
    [Fact]
    public async Task GetPolicy_ShouldReportZeroDayRetentionAsDisabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options;
        await using var context = new AdminContext(dbOptions);
        var settings = Options.Create(new OperabilitySettings());
        var audit = new AuditService(context, new HttpContextAccessor(), settings);
        var service = new AuditRetentionService(context, audit, settings);

        var policy = service.GetPolicy();

        Assert.False(policy.Enabled);
        Assert.Equal(0, policy.RetentionDays);
        Assert.Equal("Purge", policy.Mode);
        Assert.Equal(2, policy.RunHourUtc);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSupportDryRunAndAuditThePurge()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options;
        await using var context = new AdminContext(dbOptions);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.AuditLogs.AddRange(
            new AuditLog { EventId = Guid.NewGuid(), Action = "old", Target = "x", Result = "success", CreatedAt = DateTime.UtcNow.AddDays(-100) },
            new AuditLog { EventId = Guid.NewGuid(), Action = "new", Target = "x", Result = "success", CreatedAt = DateTime.UtcNow });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var settings = Options.Create(new OperabilitySettings { AuditRetentionDays = 30, AuditRetentionMode = "Purge" });
        var audit = new AuditService(context, new HttpContextAccessor(), settings);
        var service = new AuditRetentionService(context, audit, settings);

        var preview = await service.ExecuteAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(1, preview.MatchingCount);
        Assert.Equal(2, await context.AuditLogs.CountAsync(TestContext.Current.CancellationToken));

        var result = await service.ExecuteAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.DeletedCount);
        Assert.DoesNotContain(await context.AuditLogs.ToListAsync(TestContext.Current.CancellationToken), x => x.Action == "old");
        Assert.Contains(await context.AuditLogs.ToListAsync(TestContext.Current.CancellationToken), x => x.Action == "audit.retention.executed");
    }
}
