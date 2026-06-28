using Auth.Service.Data;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Background;

public class TokenBlacklistCleanupService(IServiceScopeFactory scopeFactory) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<IAuthContext>();
                var now = DateTime.UtcNow;

                var expired = await context.TokenBlacklistEntries
                    .Where(x => x.ExpiresAt <= now)
                    .ToListAsync(stoppingToken);

                if (expired.Count > 0)
                {
                    context.TokenBlacklistEntries.RemoveRange(expired);
                    await context.SaveChangesAsync(stoppingToken);
                }
            }
            catch
            {
                // swallow to avoid crashing the background service
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
