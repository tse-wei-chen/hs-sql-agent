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
    [Fact]
    public void V3ApprovalDrainMigration_RefusesUnsafeDowngrade()
    {
        var error = Assert.Throws<NotSupportedException>(() =>
        {
            _ = new HsSqlAgent.SqliteMigrations.Admin.V3ApprovalDrainGate().DownOperations;
        });
        Assert.Contains("one-way security migration", error.Message, StringComparison.Ordinal);
    }

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

    private static DmlApprovalRequestState NewLegacyPendingApproval()
    {
        var row = NewConsumedApproval("sqlite-v2-late-worker");
        row.Status = "Pending";
        return row;
    }

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
                var pending = NewConsumedApproval("sqlite-v2-pending");
                pending.Status = "Pending";
                var executing = NewConsumedApproval("sqlite-v2-executing");
                executing.Status = "Executing";
                await LegacyDmlApprovalMigrationFixture.InsertAsync(adminContext, consumed, cancellationToken);
                await LegacyDmlApprovalMigrationFixture.InsertAsync(adminContext, pending, cancellationToken);
                await LegacyDmlApprovalMigrationFixture.InsertAsync(adminContext, executing, cancellationToken);

                // A v3 upgrade must not silently pass while approvals could
                // still be consumed or while physical writes are uncertain.
                var blocked = await Record.ExceptionAsync(
                    () => adminContext.Database.MigrateAsync(cancellationToken));
                Assert.NotNull(blocked);
                Assert.Contains("V3UpgradeRequiresDrainedDmlApprovals",
                    blocked.ToString(), StringComparison.OrdinalIgnoreCase);

                // Resolving only Pending is insufficient: Executing may still
                // represent a live worker or an unacknowledged target COMMIT.
                await adminContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"DmlApprovalRequests\" SET \"Status\" = {"Rejected"} WHERE \"RequestId\" = {pending.RequestId}", cancellationToken);
                Assert.NotNull(await Record.ExceptionAsync(
                    () => adminContext.Database.MigrateAsync(cancellationToken)));

                // Test fixture explicitly simulates verified terminal state.
                // Production must never infer quiescence from expiration.
                await adminContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"DmlApprovalRequests\" SET \"Status\" = {"OutcomeUnknown"} WHERE \"RequestId\" = {executing.RequestId}", cancellationToken);
                await adminContext.Database.MigrateAsync(cancellationToken);
                var preserved = await adminContext.DmlApprovalRequests
                    .AsNoTracking().SingleAsync(x => x.RequestId == consumed.RequestId, cancellationToken);
                Assert.Equal("OutcomeUnknown", preserved.Status);
                Assert.Equal(consumed.EvidenceFingerprint, preserved.EvidenceFingerprint);
                Assert.Equal(consumed.ProtectedExecutionPayload, preserved.ProtectedExecutionPayload);
                Assert.Equal("Rejected", (await adminContext.DmlApprovalRequests.AsNoTracking()
                    .SingleAsync(x => x.RequestId == pending.RequestId, cancellationToken)).Status);
                Assert.Equal("OutcomeUnknown", (await adminContext.DmlApprovalRequests.AsNoTracking()
                    .SingleAsync(x => x.RequestId == executing.RequestId, cancellationToken)).Status);
                // Terminal v2 records are preserved with protocol 0; they must not
                // be promoted to Pending or Executing by legacy v2 SQL after upgrade.
                Assert.Equal(0, preserved.AdmissionProtocolVersion);
                var resurrect = await Record.ExceptionAsync(() =>
                    adminContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"DmlApprovalRequests\" SET \"Status\" = {"Pending"} WHERE \"RequestId\" = {consumed.RequestId}",
                        cancellationToken));
                Assert.NotNull(resurrect);

                // A pre-v3 worker omits AdmissionProtocolVersion entirely. Its
                // Pending write now fails at the database constraint, even when
                // it ignores every v3 application-level guard.
                var legacy = NewLegacyPendingApproval();
                Assert.NotNull(await Record.ExceptionAsync(() =>
                    LegacyDmlApprovalMigrationFixture.InsertAsync(adminContext, legacy, cancellationToken)));

                // New v3 workers explicitly set the accepted protocol in the
                // same durable row and can continue to create Pending approvals.
                legacy.AdmissionProtocolVersion = 1;
                adminContext.DmlApprovalRequests.Add(legacy);
                await adminContext.SaveChangesAsync(cancellationToken);
                Assert.Equal(1, (await adminContext.DmlApprovalRequests.AsNoTracking()
                    .SingleAsync(x => x.RequestId == legacy.RequestId, cancellationToken)).AdmissionProtocolVersion);

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
