using Admin.Service.Data;
using Admin.Service.Interfaces;

namespace HsSqlAgent.Server.Background;

public class AuditBackgroundService(
    IAuditQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<AuditBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var log in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();
                context.AuditLogs.Add(log);
                await context.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to write audit log entry");
            }
        }
    }
}
