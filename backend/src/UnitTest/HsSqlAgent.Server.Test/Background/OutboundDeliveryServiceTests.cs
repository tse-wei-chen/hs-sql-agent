using HsSqlAgent.Server.Background;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using Xunit;

namespace HsSqlAgent.Server.Test.Background;

public class OutboundDeliveryServiceTests
{
    [Fact]
    public void CreateSignature_ShouldProduceStableHmacSha256()
    {
        var signature = OutboundDeliveryService.CreateSignature("01234567890123456789012345678901", "{\"event\":1}");

        Assert.Equal(64, signature.Length);
        Assert.Equal(signature, OutboundDeliveryService.CreateSignature("01234567890123456789012345678901", "{\"event\":1}"));
        Assert.NotEqual(signature, OutboundDeliveryService.CreateSignature("01234567890123456789012345678901", "{\"event\":2}"));
    }

    [Fact]
    public async Task DispatchOneAsync_ShouldSignAndPersistDeliveryStatus()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options;
        await using (var setup = new AdminContext(dbOptions))
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            setup.OutboundDeliveries.Add(new OutboundDelivery
            {
                Id = 1, Category = "siem", DedupeKey = "event-1", TargetUrl = "https://siem.example/events",
                Payload = "{\"event\":1}", Status = "pending", CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var services = new ServiceCollection();
        services.AddScoped<IAdminContext>(_ => new AdminContext(dbOptions));
        using var provider = services.BuildServiceProvider();
        string? signature = null;
        var handler = new RecordingHandler(request =>
        {
            signature = request.Headers.GetValues("X-Hs-Signature").Single();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("operability-webhook")).Returns(new HttpClient(handler));
        var settings = Options.Create(new OperabilitySettings
        {
            SiemWebhookSecret = "01234567890123456789012345678901", DeliveryMaxAttempts = 3
        });
        var service = new OutboundDeliveryService(provider.GetRequiredService<IServiceScopeFactory>(), factory.Object, settings, NullLogger<OutboundDeliveryService>.Instance);

        await service.DispatchOneAsync(1, TestContext.Current.CancellationToken);

        await using var verify = new AdminContext(dbOptions);
        var item = await verify.OutboundDeliveries.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("delivered", item.Status);
        Assert.NotNull(item.DeliveredAt);
        Assert.StartsWith("sha256=", signature);
    }

    [Fact]
    public async Task DispatchOneAsync_ShouldMoveExhaustedDeliveryToDeadLetter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options;
        await using (var setup = new AdminContext(dbOptions))
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            setup.OutboundDeliveries.Add(new OutboundDelivery
            {
                Id = 1, Category = "alert", DedupeKey = "alert-1", TargetUrl = "https://alerts.example/events",
                Payload = "{}", Status = "pending", AttemptCount = 2, CreatedAt = DateTime.UtcNow, NextAttemptAt = DateTime.UtcNow
            });
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var services = new ServiceCollection();
        services.AddScoped<IAdminContext>(_ => new AdminContext(dbOptions));
        using var provider = services.BuildServiceProvider();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("operability-webhook"))
            .Returns(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        var settings = Options.Create(new OperabilitySettings
        {
            AlertWebhookSecret = "01234567890123456789012345678901", DeliveryMaxAttempts = 3
        });
        var service = new OutboundDeliveryService(provider.GetRequiredService<IServiceScopeFactory>(), factory.Object, settings, NullLogger<OutboundDeliveryService>.Instance);

        await service.DispatchOneAsync(1, TestContext.Current.CancellationToken);

        await using var verify = new AdminContext(dbOptions);
        var item = await verify.OutboundDeliveries.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("dead-letter", item.Status);
        Assert.Equal(3, item.AttemptCount);
        Assert.NotNull(item.LastError);
    }

    [Fact]
    public async Task OutboundDeliverySignal_Notify_ShouldTriggerWaitAsync()
    {
        var signal = new Admin.Service.Services.OutboundDeliverySignal();
        signal.Notify();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var hasSignal = await signal.WaitAsync(cts.Token);
        Assert.True(hasSignal);
        Assert.True(signal.TryRead());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
