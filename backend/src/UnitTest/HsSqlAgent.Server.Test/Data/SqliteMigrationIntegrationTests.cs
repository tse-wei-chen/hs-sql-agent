using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Auth.Service.Data;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HsSqlAgent.Server.Test.Data;

public sealed class SqliteMigrationIntegrationTests
{
    private static DmlApprovalRequestState NewConsumedApproval(string id) => new()
    {
        RequestId = id,
        ApprovalFingerprint = new string('A', 64),
        EvidenceFingerprint = new string('B', 64),
        Status = "OutcomeUnknown",
        ProtectedExecutionPayload = "opaque-encrypted-v2-resume-record",
        RequesterIdentity = "operator:test",
        TargetIdentity = "db-management:1",
        DatabaseProvider = "Sqlite",
        DatabaseIdentity = "test",
        AccessKeyId = 1,
        DbManagementId = 1,
        RequiredToolName = "execute_dml_sql",
        StatementCount = 1,
        TotalAffectedRows = 1,
        CreatedAt = DateTime.UtcNow.AddMinutes(-2),
        UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
        ExpiresAt = DateTime.UtcNow.AddMinutes(3)
    };

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
                // Materialize the actual v2 durable-approval schema before
                // applying the newer v3-compatible migration set.
                await adminContext.GetService<IMigrator>().MigrateAsync(
                    "20260905130453_AddDurableDmlApprovals", cancellationToken);
                var consumed = NewConsumedApproval("sqlite-v2-upgrade");
                adminContext.DmlApprovalRequests.Add(consumed);
                await adminContext.SaveChangesAsync(cancellationToken);

                await adminContext.Database.MigrateAsync(cancellationToken);
                var preserved = await adminContext.DmlApprovalRequests
                    .AsNoTracking().SingleAsync(x => x.RequestId == consumed.RequestId, cancellationToken);
                Assert.Equal("OutcomeUnknown", preserved.Status);
                Assert.Equal(consumed.EvidenceFingerprint, preserved.EvidenceFingerprint);
                Assert.Equal(consumed.ProtectedExecutionPayload, preserved.ProtectedExecutionPayload);

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

            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'DmlApprovalRequests';";
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
