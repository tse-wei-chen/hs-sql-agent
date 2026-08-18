using System.Net.Http.Headers;
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
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await DispatchBatchAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (signal != null)
                {
                    await signal.WaitAsync(stoppingToken);
                    signal.TryRead();
                    await DispatchBatchAsync(stoppingToken);
                }
                else
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();
        var now = DateTime.UtcNow;
        var ids = await context.OutboundDeliveries.AsNoTracking()
            .Where(x => (x.Status == "pending" && x.NextAttemptAt <= now) ||
                        (x.Status == "processing" && x.LastAttemptAt < now.AddMinutes(-5)))
            .OrderBy(x => x.NextAttemptAt).Select(x => x.Id).Take(20).ToListAsync(cancellationToken);
        foreach (var id in ids) await DispatchOneAsync(id, cancellationToken);
    }

    internal async Task DispatchOneAsync(long id, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IAdminContext>();
        var item = await context.OutboundDeliveries.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null || (item.Status != "pending" && item.Status != "processing")) return;
        item.Status = "processing";
        item.LastAttemptAt = DateTime.UtcNow;
        item.AttemptCount++;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return; }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, item.TargetUrl);
            request.Content = new StringContent(item.Payload, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("X-Hs-Delivery-Id", item.Id.ToString());
            var secret = item.Category == "siem" ? settings.Value.SiemWebhookSecret : settings.Value.AlertWebhookSecret;
            if (!string.IsNullOrWhiteSpace(secret))
            {
                var signature = CreateSignature(secret, item.Payload);
                request.Headers.TryAddWithoutValidation("X-Hs-Signature", $"sha256={signature}");
            }
            using var client = httpClientFactory.CreateClient("operability-webhook");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            item.Status = "delivered"; item.DeliveredAt = DateTime.UtcNow; item.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            item.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            if (item.AttemptCount >= Math.Max(1, settings.Value.DeliveryMaxAttempts)) item.Status = "dead-letter";
            else
            {
                item.Status = "pending";
                item.NextAttemptAt = DateTime.UtcNow.AddSeconds(Math.Min(3600, Math.Pow(2, item.AttemptCount)));
            }
            logger.LogWarning(ex, "Webhook delivery {DeliveryId} failed on attempt {Attempt}.", item.Id, item.AttemptCount);
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    internal static string CreateSignature(string secret, string payload)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}
