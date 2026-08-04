using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Background;

public class AuditRetentionBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<OperabilitySettings> settings,
    ILogger<AuditRetentionBackgroundService> logger) : BackgroundService
{
    private DateTime? _lastRunDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do { await RunIfDueAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var now = DateTime.UtcNow;
        if (options.AuditRetentionDays <= 0 || now.Hour < Math.Clamp(options.AuditRetentionRunHourUtc, 0, 23) || _lastRunDay == now.Date) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IAuditRetentionService>().ExecuteAsync(false, cancellationToken);
            _lastRunDay = now.Date;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "Scheduled audit retention failed."); }
    }
}
