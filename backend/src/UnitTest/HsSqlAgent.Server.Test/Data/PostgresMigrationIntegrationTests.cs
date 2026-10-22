using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Auth.Service.Data;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
        // Upgrade a real v2 schema containing consumed DML evidence, not
        // only a fresh empty database. The migration must not erase consent
        // or turn an OutcomeUnknown state back into executable Pending.
        await adminContext.GetService<IMigrator>().MigrateAsync(
            "20260905130459_AddDurableDmlApprovalsPostgres", cancellationToken);
        var consumed = new DmlApprovalRequestState
        {
            RequestId = "postgres-v2-upgrade",
            ApprovalFingerprint = new string('A', 64),
            EvidenceFingerprint = new string('B', 64),
            Status = "OutcomeUnknown",
            ProtectedExecutionPayload = "opaque-encrypted-v2-resume-record",
            RequesterIdentity = "operator:test",
            TargetIdentity = "db-management:1",
            DatabaseProvider = "Postgres",
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
        var pending = new DmlApprovalRequestState
        {
            RequestId = "postgres-v2-pending",
            ApprovalFingerprint = new string('C', 64),
            EvidenceFingerprint = new string('D', 64),
            Status = "Pending",
            ProtectedExecutionPayload = "opaque-encrypted-v2-pending",
            RequesterIdentity = "operator:test",
            TargetIdentity = "db-management:1",
            DatabaseProvider = "Postgres",
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
        var executing = new DmlApprovalRequestState
        {
            RequestId = "postgres-v2-executing",
            ApprovalFingerprint = new string('E', 64),
            EvidenceFingerprint = new string('F', 64),
            Status = "Executing",
            ProtectedExecutionPayload = "opaque-encrypted-v2-executing",
            RequesterIdentity = "operator:test",
            TargetIdentity = "db-management:1",
            DatabaseProvider = "Postgres",
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
        adminContext.DmlApprovalRequests.AddRange(consumed, pending, executing);
        await adminContext.SaveChangesAsync(cancellationToken);

        // Fail-closed even when migrations are invoked directly without the
        // HsSqlAgent application startup checks.
        var blocked = await Record.ExceptionAsync(
            () => adminContext.Database.MigrateAsync(cancellationToken));
        Assert.NotNull(blocked);
        Assert.Contains("requires draining Pending/Executing", blocked.ToString(),
            StringComparison.OrdinalIgnoreCase);

        pending.Status = "Rejected";
        await adminContext.SaveChangesAsync(cancellationToken);
        Assert.NotNull(await Record.ExceptionAsync(
            () => adminContext.Database.MigrateAsync(cancellationToken)));

        // Simulates an independently verified terminal reconciliation. A
        // production operator must never blindly relabel an Executing request.
        executing.Status = "OutcomeUnknown";
        await adminContext.SaveChangesAsync(cancellationToken);
        await adminContext.Database.MigrateAsync(cancellationToken);
        var preserved = await adminContext.DmlApprovalRequests.AsNoTracking()
            .SingleAsync(x => x.RequestId == consumed.RequestId, cancellationToken);
        Assert.Equal("OutcomeUnknown", preserved.Status);
        Assert.Equal(consumed.EvidenceFingerprint, preserved.EvidenceFingerprint);
        Assert.Equal(consumed.ProtectedExecutionPayload, preserved.ProtectedExecutionPayload);
        Assert.Equal("Rejected", (await adminContext.DmlApprovalRequests.AsNoTracking()
            .SingleAsync(x => x.RequestId == pending.RequestId, cancellationToken)).Status);
        Assert.Equal("OutcomeUnknown", (await adminContext.DmlApprovalRequests.AsNoTracking()
            .SingleAsync(x => x.RequestId == executing.RequestId, cancellationToken)).Status);

        Assert.NotEmpty(await authContext.Database.GetAppliedMigrationsAsync(cancellationToken));
        Assert.NotEmpty(await adminContext.Database.GetAppliedMigrationsAsync(cancellationToken));
        Assert.Empty(await authContext.Database.GetPendingMigrationsAsync(cancellationToken));
        Assert.Empty(await adminContext.Database.GetPendingMigrationsAsync(cancellationToken));

        // A second run exercises the same behavior used on every application startup.
        await authContext.Database.MigrateAsync(cancellationToken);
        await adminContext.Database.MigrateAsync(cancellationToken);
    }
}
