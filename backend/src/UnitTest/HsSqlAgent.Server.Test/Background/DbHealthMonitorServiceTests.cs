using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Models;
using Common.Interfaces;
using HsSqlAgent.Server.Background;
using HsSqlAgent.SqlCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Interfaces;
using Xunit;

namespace HsSqlAgent.Server.Test.Background;

public class DbHealthMonitorServiceTests
{
    [Fact]
    public async Task ProbeAllAsync_ShouldBoundProbeConcurrencyAndPersistHealthOnce()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AdminContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setup = new AdminContext(dbOptions))
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            for (var i = 1; i <= 4; i++)
            {
                setup.DbManagement.Add(new DbManagement
                {
                    Id = i,
                    Name = $"db-{i}",
                    SqlProvider = "Postgres",
                    Host = "localhost",
                    Port = "5432",
                    Username = "user",
                    PasswordHash = "encrypted",
                    Database = "test",
                    ExtraSettings = string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            await setup.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var persistence = new PersistenceCounter();
        var concurrency = new ProbeConcurrencyTracker();
        var services = new ServiceCollection();
        services.AddScoped<IAdminContext>(_ => new CountingAdminContext(dbOptions, persistence));
        services.AddSingleton<ICryptoService, PassthroughCryptoService>();
        services.AddSingleton(Options.Create(new McpKeySettings
        {
            HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes"
        }));
        services.AddScoped<IDbSetterService>(_ => new TrackingDbSetterService(concurrency));
        using var provider = services.BuildServiceProvider();

        var settings = Options.Create(new OperabilitySettings
        {
            HealthProbeEnabled = true,
            HealthProbeTimeoutSeconds = 5,
            HealthProbeMaxConcurrency = 2
        });
        var service = new DbHealthMonitorService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            settings,
            NullLogger<DbHealthMonitorService>.Instance);

        await service.ProbeAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, concurrency.MaxConcurrency);
        Assert.Equal(1, persistence.SaveChangesCalls);

        await using var verify = new AdminContext(dbOptions);
        var states = await verify.DbHealthStates
            .OrderBy(x => x.DbManagementId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(4, states.Count);
        Assert.All(states, state =>
        {
            Assert.Equal("healthy", state.Status);
            Assert.Equal(0, state.ConsecutiveFailures);
            Assert.NotNull(state.LastCheckedAt);
            Assert.NotNull(state.LastSuccessAt);
            Assert.NotNull(state.LatencyMs);
        });
    }

    private sealed class CountingAdminContext(
        DbContextOptions<AdminContext> options,
        PersistenceCounter counter) : AdminContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class PersistenceCounter
    {
        private int _saveChangesCalls;
        public int SaveChangesCalls => Volatile.Read(ref _saveChangesCalls);
        public void Increment() => Interlocked.Increment(ref _saveChangesCalls);
    }

    private sealed class ProbeConcurrencyTracker
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

    private sealed class TrackingDbSetterService(ProbeConcurrencyTracker tracker) : IDbSetterService
    {
        public async Task<TestDbConnectionVM> TestDbConnectionAsync(
            TestDbConnectionBase request,
            CancellationToken cancellationToken = default)
        {
            tracker.Enter();
            try
            {
                await Task.Delay(50, cancellationToken);
                return new TestDbConnectionVM { IsSuccess = true };
            }
            finally
            {
                tracker.Exit();
            }
        }

        public Task<string?> BuildDbConnectionAsync(
            BuildDbConnectionModel model,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class PassthroughCryptoService : ICryptoService
    {
        public string? EncryptText(string? plainText, byte[] secretKey) => plainText;
        public string? DecryptText(string? cipherText, byte[] secretKey) => cipherText;
    }
}
