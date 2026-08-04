using Admin.Service.Data;
using Admin.Service.Data.Entites;
using HsSqlAgent.Server.Services;

namespace HsSqlAgent.Server.Background;

public class OperationalMetricFlushService(
    IOperationalMetricRecorder recorder,
    IServiceScopeFactory scopeFactory,
    ILogger<OperationalMetricFlushService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do { await FlushAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var snapshots = recorder.Drain();
        if (snapshots.Count == 0) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();
            foreach (var item in snapshots)
                context.RateLimitMetrics.Add(new RateLimitMetric
                {
                    BucketStart = item.BucketStart, Layer = item.Layer, AccessKeyId = item.AccessKeyId,
                    DbManagementId = item.DbManagementId, ToolName = item.ToolName, RejectedCount = item.Count
                });
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to persist aggregated rate-limit metrics.");
            recorder.Restore(snapshots);
        }
    }
}
