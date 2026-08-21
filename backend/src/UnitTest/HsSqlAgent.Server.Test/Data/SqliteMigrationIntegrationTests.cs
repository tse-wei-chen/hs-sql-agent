using Admin.Service.Data;
using Auth.Service.Data;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Data;

public sealed class SqliteMigrationIntegrationTests
{
    [Fact]
    public async Task Migrations_ShouldApplyToRealSqliteFile_AndBeIdempotent()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hsqlagent-migrations-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

        try
        {
            var services = new ServiceCollection();
            services.AddHsSqlAgent(new HsSqlAgentServiceOptions
            {
                AdminDatabaseProvider = "Sqlite",
                AdminConnectionString = connectionString,
                HmacSecretKey = "test-hmac-key-that-is-at-least-32-bytes",
                JwtSecretKey = "test-jwt-key-that-is-at-least-32-bytes"
            });

            await using (var provider = services.BuildServiceProvider())
            await using (var scope = provider.CreateAsyncScope())
            {
                var authContext = scope.ServiceProvider.GetRequiredService<AuthContext>();
                var adminContext = scope.ServiceProvider.GetRequiredService<AdminContext>();
                var cancellationToken = TestContext.Current.CancellationToken;

                // Keep the production startup order and the existing shared history table.
                await authContext.Database.MigrateAsync(cancellationToken);
                await adminContext.Database.MigrateAsync(cancellationToken);

                Assert.NotEmpty(await authContext.Database.GetAppliedMigrationsAsync(cancellationToken));
                Assert.NotEmpty(await adminContext.Database.GetAppliedMigrationsAsync(cancellationToken));
                Assert.Empty(await authContext.Database.GetPendingMigrationsAsync(cancellationToken));
                Assert.Empty(await adminContext.Database.GetPendingMigrationsAsync(cancellationToken));

                await authContext.Database.MigrateAsync(cancellationToken);
                await adminContext.Database.MigrateAsync(cancellationToken);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';";

            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }
}
