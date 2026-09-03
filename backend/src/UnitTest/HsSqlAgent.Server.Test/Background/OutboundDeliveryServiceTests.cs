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

        var dispatched = await service.DispatchOneAsync(1, TestContext.Current.CancellationToken);

        Assert.True(dispatched);
        await using var verify = new AdminContext(dbOptions);
        var item = await verify.OutboundDeliveries.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("delivered", item.Status);
        Assert.Equal(1, item.AttemptCount);
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

        var dispatched = await service.DispatchOneAsync(1, TestContext.Current.CancellationToken);

        Assert.True(dispatched);
        await using var verify = new AdminContext(dbOptions);
        var item = await verify.OutboundDeliveries.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("dead-letter", item.Status);
        Assert.Equal(3, item.AttemptCount);
        Assert.NotNull(item.LastError);
    }

    [Fact]
    public async Task DispatchBatchAsync_ShouldBoundConcurrentHttpDeliveries()
    {
        var (anchor, dbOptions) = await CreateSharedDatabaseAsync(6);
        await using (anchor)
        {
            var services = new ServiceCollection();
            services.AddScoped<IAdminContext>(_ => new AdminContext(dbOptions));
            using var provider = services.BuildServiceProvider();

            var concurrency = new ConcurrencyTracker();
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(x => x.CreateClient("operability-webhook"))
                .Returns(() => new HttpClient(new AsyncRecordingHandler(async (_, cancellationToken) =>
                {
                    concurrency.Enter();
                    try
                    {
                        await Task.Delay(75, cancellationToken);
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    }
                    finally
                    {
                        concurrency.Exit();
                    }
                })));
            var settings = Options.Create(new OperabilitySettings
            {
                DeliveryMaxAttempts = 3,
                DeliveryMaxConcurrency = 2
            });
            var service = new OutboundDeliveryService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                factory.Object,
                settings,
                NullLogger<OutboundDeliveryService>.Instance);

            var selected = await service.DispatchBatchAsync(TestContext.Current.CancellationToken);

            Assert.Equal(6, selected);
            Assert.Equal(2, concurrency.MaxConcurrency);
            await using var verify = new AdminContext(dbOptions);
            Assert.Equal(6, await verify.OutboundDeliveries.CountAsync(
                x => x.Status == "delivered",
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task DispatchOneAsync_ConcurrentClaims_ShouldSendOnlyOnce()
    {
        var (anchor, dbOptions) = await CreateSharedDatabaseAsync(1);
        await using (anchor)
        {
            var services = new ServiceCollection();
            services.AddScoped<IAdminContext>(_ => new AdminContext(dbOptions));
            using var provider = services.BuildServiceProvider();

            var sendCount = 0;
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(x => x.CreateClient("operability-webhook"))
                .Returns(() => new HttpClient(new AsyncRecordingHandler(async (_, cancellationToken) =>
                {
                    Interlocked.Increment(ref sendCount);
                    await Task.Delay(50, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                })));
            var settings = Options.Create(new OperabilitySettings
            {
                DeliveryMaxAttempts = 3,
                DeliveryMaxConcurrency = 2
            });
            var service = new OutboundDeliveryService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                factory.Object,
                settings,
                NullLogger<OutboundDeliveryService>.Instance);

            var results = await Task.WhenAll(
                service.DispatchOneAsync(1, TestContext.Current.CancellationToken),
                service.DispatchOneAsync(1, TestContext.Current.CancellationToken));

            Assert.Single(results, result => result);
            Assert.Equal(1, Volatile.Read(ref sendCount));
            await using var verify = new AdminContext(dbOptions);
            var item = await verify.OutboundDeliveries.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("delivered", item.Status);
            Assert.Equal(1, item.AttemptCount);
        }
    }

    [Fact]
    public async Task GetNextWakeAtAsync_ShouldSchedulePendingRetryWithoutSignal()
    {
        var (anchor, dbOptions) = await CreateSharedDatabaseAsync(0);
        await using (anchor)
        {
            var expected = DateTime.UtcNow.AddMinutes(2);
            await using (var setup = new AdminContext(dbOptions))
            {
                setup.OutboundDeliveries.Add(new OutboundDelivery
                {
                    Id = 1,
                    Category = "alert",
                    DedupeKey = "future-retry",
                    TargetUrl = "https://alerts.example/events",
                    Payload = "{}",
                    Status = "pending",
                    CreatedAt = DateTime.UtcNow,
                    NextAttemptAt = expected
                });
                await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var services = new ServiceCollection();
            services.AddScoped<IAdminContext>(_ => new AdminContext(dbOptions));
            using var provider = services.BuildServiceProvider();
            var factory = new Mock<IHttpClientFactory>();
            var service = new OutboundDeliveryService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                factory.Object,
                Options.Create(new OperabilitySettings()),
                NullLogger<OutboundDeliveryService>.Instance);

            var nextWakeAt = await service.GetNextWakeAtAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(nextWakeAt);
            Assert.Equal(expected, nextWakeAt.Value, TimeSpan.FromMilliseconds(10));
        }
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

    private static async Task<(SqliteConnection Anchor, DbContextOptions<AdminContext> Options)>
        CreateSharedDatabaseAsync(int deliveryCount)
    {
        var connectionString = $"Data Source=outbound-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var setup = new AdminContext(options);
        await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        for (var i = 1; i <= deliveryCount; i++)
        {
            setup.OutboundDeliveries.Add(new OutboundDelivery
            {
                Id = i,
                Category = "alert",
                DedupeKey = $"delivery-{i}",
                TargetUrl = "https://alerts.example/events",
                Payload = "{}",
                Status = "pending",
                CreatedAt = DateTime.UtcNow,
                NextAttemptAt = DateTime.UtcNow.AddSeconds(-1)
            });
        }
        await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (anchor, options);
    }

    private sealed class ConcurrencyTracker
    {
        private int _active;
        private int _maxConcurrency;

        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrency);
                if (active <= observed ||
                    Interlocked.CompareExchange(ref _maxConcurrency, active, observed) == observed)
                    break;
            }
        }

        public void Exit() => Interlocked.Decrement(ref _active);
    }

    private sealed class AsyncRecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
