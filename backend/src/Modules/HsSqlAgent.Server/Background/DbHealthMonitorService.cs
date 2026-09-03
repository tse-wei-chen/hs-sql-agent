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
using SqlAgent.Service.Interfaces;

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

    internal async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<DbManagement> databases;
            using (var inventoryScope = scopeFactory.CreateScope())
            {
                var context = inventoryScope.ServiceProvider.GetRequiredService<IAdminContext>();
                databases = await context.DbManagement.AsNoTracking().ToListAsync(cancellationToken);
            }

            if (databases.Count == 0) return;

            var results = new DbProbeResult[databases.Count];
            var indexedDatabases = databases
                .Select(static (database, index) => (Database: database, Index: index))
                .ToArray();
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(settings.Value.HealthProbeMaxConcurrency, 1, 32)
            };

            await Parallel.ForEachAsync(indexedDatabases, parallelOptions, async (item, token) =>
            {
                results[item.Index] = await ProbeOneAsync(item.Database, token);
            });

            await PersistResultsAsync(results, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "Scheduled database health probe failed."); }
    }

    private async Task<DbProbeResult> ProbeOneAsync(DbManagement db, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var crypto = scope.ServiceProvider.GetRequiredService<ICryptoService>();
        var keySettings = scope.ServiceProvider.GetRequiredService<IOptions<McpKeySettings>>().Value;
        var tester = scope.ServiceProvider.GetRequiredService<IDbSetterService>();

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
                SqlProvider = provider,
                Host = db.Host,
                Port = db.Port,
                Username = db.Username,
                Password = crypto.DecryptText(
                    db.PasswordHash,
                    Encoding.UTF8.GetBytes(keySettings.HmacSecretKey)),
                Database = db.Database,
                ExtraSettings = db.ExtraSettings
            }, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            result = new TestDbConnectionVM { IsSuccess = false, ErrorMessage = ex.Message };
        }

        return new DbProbeResult(
            db.Id,
            db.Name,
            db.SqlProvider ?? "unknown",
            DateTime.UtcNow,
            stopwatch.ElapsedMilliseconds,
            result.IsSuccess,
            result.ErrorMessage);
    }

    private async Task PersistResultsAsync(
        IReadOnlyCollection<DbProbeResult> results,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();
        var currentDatabases = await context.DbManagement
            .GroupJoin(
                context.DbHealthStates,
                db => db.Id,
                health => health.DbManagementId,
                (db, health) => new { db.Id, Health = health.FirstOrDefault() })
            .ToListAsync(cancellationToken);
        var states = currentDatabases.ToDictionary(x => x.Id, x => x.Health);
        var persisted = new List<(DbProbeResult Probe, DbHealthState State)>(results.Count);

        foreach (var probe in results)
        {
            if (!states.TryGetValue(probe.DbManagementId, out var state))
                continue;

            if (state is null)
            {
                state = new DbHealthState { DbManagementId = probe.DbManagementId };
                context.DbHealthStates.Add(state);
                states[probe.DbManagementId] = state;
            }

            state.LastCheckedAt = probe.CheckedAt;
            state.LatencyMs = probe.LatencyMs;
            if (probe.IsSuccess)
            {
                state.Status = "healthy";
                state.LastSuccessAt = probe.CheckedAt;
                state.ConsecutiveFailures = 0;
                state.OutageStartedAt = null;
                state.LastError = null;
            }
            else
            {
                if (state.ConsecutiveFailures == 0)
                    state.OutageStartedAt = probe.CheckedAt;
                state.ConsecutiveFailures++;
                state.Status = state.ConsecutiveFailures >= 3 ? "unhealthy" : "degraded";
                state.LastError = probe.ErrorMessage?.Length > 2000
                    ? probe.ErrorMessage[..2000]
                    : probe.ErrorMessage;
            }

            persisted.Add((probe, state));
        }

        if (persisted.Count == 0) return;

        await context.SaveChangesAsync(cancellationToken);

        foreach (var item in persisted)
            metrics?.RecordDbHealth(
                item.Probe.DbManagementId,
                item.Probe.Provider,
                item.State.Status,
                item.Probe.LatencyMs);

        if (string.IsNullOrWhiteSpace(settings.Value.AlertWebhookUrl))
            return;

        var unhealthy = persisted.Where(x => x.State.Status == "unhealthy").ToArray();
        if (unhealthy.Length == 0)
            return;

        var operability = scope.ServiceProvider.GetRequiredService<IOperabilityService>();
        foreach (var item in unhealthy)
        {
            var outage = item.State.OutageStartedAt?.Ticks ?? 0;
            await operability.QueueDeliveryAsync(
                "alert",
                $"db-health:{item.Probe.DbManagementId}:{outage}",
                settings.Value.AlertWebhookUrl,
                JsonSerializer.Serialize(new
                {
                    type = "db_health",
                    dbId = item.Probe.DbManagementId,
                    item.Probe.Name,
                    item.State.Status,
                    item.State.ConsecutiveFailures,
                    item.State.LastError,
                    occurredAt = item.Probe.CheckedAt
                }),
                cancellationToken);
        }
    }

    private sealed record DbProbeResult(
        int DbManagementId,
        string Name,
        string Provider,
        DateTime CheckedAt,
        long LatencyMs,
        bool IsSuccess,
        string? ErrorMessage);
}
