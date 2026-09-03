using System.Security.Cryptography;
using System.Text;
using Admin.Service.Data;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Background;

public class OutboundDeliveryService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IOptions<OperabilitySettings> settings,
    ILogger<OutboundDeliveryService> logger,
    IOutboundDeliverySignal? signal = null) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var selected = await DispatchBatchAsync(stoppingToken);
                if (selected >= BatchSize)
                    continue;

                var nextWakeAt = await GetNextWakeAtAsync(stoppingToken);
                if (!await WaitForWorkAsync(nextWakeAt, stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbound delivery worker cycle failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    internal async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        List<long> ids;
        var now = DateTime.UtcNow;
        var staleBefore = now - ProcessingLease;
        using (var scope = scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();
            ids = await context.OutboundDeliveries.AsNoTracking()
                .Where(x =>
                    (x.Status == "pending" && x.NextAttemptAt <= now) ||
                    (x.Status == "processing" &&
                     (!x.LastAttemptAt.HasValue || x.LastAttemptAt < staleBefore)))
                .OrderBy(x => x.NextAttemptAt)
                .Select(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        if (ids.Count == 0)
            return 0;

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(settings.Value.DeliveryMaxConcurrency, 1, 32)
        };
        await Parallel.ForEachAsync(ids, parallelOptions, async (id, token) =>
        {
            await DispatchOneAsync(id, token);
        });
        return ids.Count;
    }

    internal async Task<bool> DispatchOneAsync(long id, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();
        var claimAt = DateTime.UtcNow;
        var staleBefore = claimAt - ProcessingLease;

        var claimed = await context.OutboundDeliveries
            .Where(x =>
                x.Id == id &&
                ((x.Status == "pending" && x.NextAttemptAt <= claimAt) ||
                 (x.Status == "processing" &&
                  (!x.LastAttemptAt.HasValue || x.LastAttemptAt < staleBefore))))
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(x => x.Status, "processing")
                    .SetProperty(x => x.LastAttemptAt, claimAt)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1),
                cancellationToken);
        if (claimed != 1)
            return false;

        var item = await context.OutboundDeliveries
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null)
            return false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, item.TargetUrl);
            request.Content = new StringContent(item.Payload, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("X-Hs-Delivery-Id", item.Id.ToString());
            var secret = item.Category == "siem"
                ? settings.Value.SiemWebhookSecret
                : settings.Value.AlertWebhookSecret;
            if (!string.IsNullOrWhiteSpace(secret))
            {
                var signature = CreateSignature(secret, item.Payload);
                request.Headers.TryAddWithoutValidation("X-Hs-Signature", $"sha256={signature}");
            }

            using var client = httpClientFactory.CreateClient("operability-webhook");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            item.Status = "delivered";
            item.DeliveredAt = DateTime.UtcNow;
            item.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            item.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            if (item.AttemptCount >= Math.Max(1, settings.Value.DeliveryMaxAttempts))
            {
                item.Status = "dead-letter";
            }
            else
            {
                item.Status = "pending";
                item.NextAttemptAt = DateTime.UtcNow.AddSeconds(
                    Math.Min(3600, Math.Pow(2, item.AttemptCount)));
            }

            logger.LogWarning(
                ex,
                "Webhook delivery {DeliveryId} failed on attempt {Attempt}.",
                item.Id,
                item.AttemptCount);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(
                ex,
                "Webhook delivery {DeliveryId} completion lost its processing lease.",
                item.Id);
            return false;
        }

        return true;
    }

    internal async Task<DateTime?> GetNextWakeAtAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();

        var nextPending = await context.OutboundDeliveries.AsNoTracking()
            .Where(x => x.Status == "pending")
            .OrderBy(x => x.NextAttemptAt)
            .Select(x => (DateTime?)x.NextAttemptAt)
            .FirstOrDefaultAsync(cancellationToken);

        var oldestProcessing = await context.OutboundDeliveries.AsNoTracking()
            .Where(x => x.Status == "processing" && x.LastAttemptAt.HasValue)
            .OrderBy(x => x.LastAttemptAt)
            .Select(x => x.LastAttemptAt)
            .FirstOrDefaultAsync(cancellationToken);
        var processingWakeAt = oldestProcessing?.Add(ProcessingLease);

        if (!nextPending.HasValue)
            return processingWakeAt;
        if (!processingWakeAt.HasValue)
            return nextPending;
        return nextPending <= processingWakeAt ? nextPending : processingWakeAt;
    }

    private async Task<bool> WaitForWorkAsync(DateTime? nextWakeAt, CancellationToken cancellationToken)
    {
        var delay = nextWakeAt.HasValue
            ? nextWakeAt.Value - DateTime.UtcNow
            : Timeout.InfiniteTimeSpan;
        if (delay != Timeout.InfiniteTimeSpan && delay <= TimeSpan.Zero)
            return true;

        if (signal is null)
        {
            if (delay == Timeout.InfiniteTimeSpan)
                return false;
            await Task.Delay(delay, cancellationToken);
            return true;
        }

        if (delay == Timeout.InfiniteTimeSpan)
        {
            if (!await signal.WaitAsync(cancellationToken))
                return false;
            DrainSignal();
            return true;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = signal.WaitAsync(waitCancellation.Token).AsTask();
        var delayTask = Task.Delay(delay, waitCancellation.Token);
        var completed = await Task.WhenAny(signalTask, delayTask);

        if (completed == signalTask)
        {
            var hasSignal = await signalTask;
            waitCancellation.Cancel();
            if (hasSignal)
                DrainSignal();
            return hasSignal;
        }

        waitCancellation.Cancel();
        try
        {
            await signalTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        return true;
    }

    private void DrainSignal()
    {
        while (signal?.TryRead() == true)
        {
        }
    }

    internal static string CreateSignature(string secret, string payload)
        => Convert.ToHexString(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(secret),
                    Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
}
