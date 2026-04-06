using Microsoft.Extensions.DependencyInjection;
using Modules.Interfaces;

namespace ToolBox.Background;

public class McpAccessKeyLastUsedBackgroundService(
    IMcpAccessKeyLastUsedQueue queue,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<McpAccessKeyLastUsedBackgroundService> logger) : BackgroundService
{
    private readonly IMcpAccessKeyLastUsedQueue _queue = queue;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly ILogger<McpAccessKeyLastUsedBackgroundService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var keyId in _queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var keyService = scope.ServiceProvider.GetRequiredService<IMcpAccessKeyService>();
                await keyService.TouchLastUsedAsync(keyId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update MCP key last-used timestamp for keyId={KeyId}", keyId);
            }
        }
    }
}
