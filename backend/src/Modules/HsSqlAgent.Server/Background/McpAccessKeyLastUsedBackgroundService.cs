using Admin.Service.Interfaces;

namespace HsSqlAgent.Server.Background;

public class McpAccessKeyLastUsedBackgroundService(
    IMcpAccessKeyLastUsedQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<McpAccessKeyLastUsedBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var keyId in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var keyService = scope.ServiceProvider.GetRequiredService<IMcpAccessKeyService>();
                await keyService.TouchLastUsedAsync(keyId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to update LastUsed for key {KeyId}", keyId);
            }
        }
    }
}
