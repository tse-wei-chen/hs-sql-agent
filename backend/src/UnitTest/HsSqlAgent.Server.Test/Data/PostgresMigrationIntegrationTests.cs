using Admin.Service.Data;
using Auth.Service.Data;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace HsSqlAgent.Server.Test.Data;

public sealed class PostgresMigrationFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:15-alpine")
        .WithDatabase("hsqlagent_migrations")
        .WithUsername("test_user")
        .WithPassword("TestPass123!")
        .WithCommand("-c", "fsync=off", "-c", "full_page_writes=off")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() =>
        await _container.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public sealed class PostgresMigrationIntegrationTests(PostgresMigrationFixture fixture)
    : IClassFixture<PostgresMigrationFixture>
{
    [Fact]
    public async Task Migrations_ShouldApplyToRealPostgres_AndBeIdempotent()
    {
        var services = new ServiceCollection();
        services.AddHsSqlAgent(new HsSqlAgentServiceOptions
        {
            AdminDatabaseProvider = "Postgres",
            AdminConnectionString = fixture.ConnectionString,
            HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
            JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
        });

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var authContext = scope.ServiceProvider.GetRequiredService<AuthContext>();
        var adminContext = scope.ServiceProvider.GetRequiredService<AdminContext>();
        var cancellationToken = TestContext.Current.CancellationToken;

        await authContext.Database.MigrateAsync(cancellationToken);
        await adminContext.Database.MigrateAsync(cancellationToken);

        Assert.NotEmpty(await authContext.Database.GetAppliedMigrationsAsync(cancellationToken));
        Assert.NotEmpty(await adminContext.Database.GetAppliedMigrationsAsync(cancellationToken));
        Assert.Empty(await authContext.Database.GetPendingMigrationsAsync(cancellationToken));
        Assert.Empty(await adminContext.Database.GetPendingMigrationsAsync(cancellationToken));

        // A second run exercises the same behavior used on every application startup.
        await authContext.Database.MigrateAsync(cancellationToken);
        await adminContext.Database.MigrateAsync(cancellationToken);
    }
}
