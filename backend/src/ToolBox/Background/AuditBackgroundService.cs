using Admin.Service.Data;
using Admin.Service.Interfaces;
using ToolBox.Background;

namespace ToolBox.Background;

public class AuditBackgroundService(
    IAuditQueue queue,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<AuditBackgroundService> logger) : BackgroundService
{
    private readonly IAuditQueue _queue = queue;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly ILogger<AuditBackgroundService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var log in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
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
                _logger.LogError(ex, "Failed to save audit log in background. Action={Action}, Target={Target}", log.Action, log.Target);
            }
        }
    }
}
