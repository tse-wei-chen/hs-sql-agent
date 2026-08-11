using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;

namespace HsSqlAgent.Server.Background;

public class DbHealthMonitorService(
    IServiceScopeFactory scopeFactory,
    IOptions<OperabilitySettings> settings,
    ILogger<DbHealthMonitorService> logger,
    IHsSqlAgentMetrics? metrics = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.HealthProbeEnabled) return;
        await ProbeAllAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, settings.Value.HealthProbeIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await ProbeAllAsync(stoppingToken);
    }

    private async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();
            var crypto = scope.ServiceProvider.GetRequiredService<ICryptoService>();
            var keySettings = scope.ServiceProvider.GetRequiredService<IOptions<McpKeySettings>>().Value;
            var tester = scope.ServiceProvider.GetRequiredService<IDbSetterService>();
            var operability = scope.ServiceProvider.GetRequiredService<IOperabilityService>();
            var databases = await context.DbManagement.AsNoTracking().ToListAsync(cancellationToken);
            foreach (var db in databases)
            {
                var stopwatch = Stopwatch.StartNew();
                TestDbConnectionVM result;
                try
                {
                    if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var provider))
                        throw new InvalidOperationException("Invalid SQL provider.");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.Value.HealthProbeTimeoutSeconds)));
                    result = await tester.TestDbConnectionAsync(new TestDbConnectionBase
                    {
                        SqlProvider = provider, Host = db.Host, Port = db.Port, Username = db.Username,
                        Password = crypto.DecryptText(db.PasswordHash, Encoding.UTF8.GetBytes(keySettings.HmacSecretKey)),
                        Database = db.Database, ExtraSettings = db.ExtraSettings
                    }, timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    result = new TestDbConnectionVM { IsSuccess = false, ErrorMessage = ex.Message };
                }

                var now = DateTime.UtcNow;
                var state = await context.DbHealthStates.FirstOrDefaultAsync(x => x.DbManagementId == db.Id, cancellationToken);
                if (state is null)
                {
                    state = new DbHealthState { DbManagementId = db.Id };
                    context.DbHealthStates.Add(state);
                }
                state.LastCheckedAt = now;
                state.LatencyMs = stopwatch.ElapsedMilliseconds;
                if (result.IsSuccess)
                {
                    state.Status = "healthy"; state.LastSuccessAt = now; state.ConsecutiveFailures = 0;
                    state.OutageStartedAt = null; state.LastError = null;
                }
                else
                {
                    if (state.ConsecutiveFailures == 0) state.OutageStartedAt = now;
                    state.ConsecutiveFailures++;
                    state.Status = state.ConsecutiveFailures >= 3 ? "unhealthy" : "degraded";
                    state.LastError = result.ErrorMessage?.Length > 2000 ? result.ErrorMessage[..2000] : result.ErrorMessage;
                }
                await context.SaveChangesAsync(cancellationToken);
                metrics?.RecordDbHealth(db.Id, db.SqlProvider ?? "unknown", state.Status, stopwatch.ElapsedMilliseconds);

                if (state.Status == "unhealthy" && !string.IsNullOrWhiteSpace(settings.Value.AlertWebhookUrl))
                {
                    var outage = state.OutageStartedAt?.Ticks ?? 0;
                    await operability.QueueDeliveryAsync(
                        "alert", $"db-health:{db.Id}:{outage}", settings.Value.AlertWebhookUrl,
                        JsonSerializer.Serialize(new { type = "db_health", dbId = db.Id, db.Name, state.Status, state.ConsecutiveFailures, state.LastError, occurredAt = now }),
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "Scheduled database health probe failed."); }
    }
}
