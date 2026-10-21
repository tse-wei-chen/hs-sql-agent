using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using HsSqlAgent.Server.Authorization;
using HsSqlAgent.Server.Controllers;
using HsSqlAgent.Server.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DurableDmlReconciliationServiceTests
{
    [Theory]
    [InlineData("OutcomeUnknown", "committed", "AttestedCommitted")]
    [InlineData("OutcomeUnknown", "not_committed", "AttestedNotCommitted")]
    [InlineData("OutcomeUnknown", "indeterminate", "AttestedIndeterminate")]
    [InlineData("ApprovalStateUnknown", "committed", "AttestedCommitted")]
    [InlineData("RollbackOutcomeUnknown", "indeterminate", "AttestedIndeterminate")]
    public async Task OperatorAttestation_IsPersistedWithAuditAndCannotBeReissued(
        string initialStatus, string resolution, string expectedTerminalStatus)
    {
        await using var fixture = await Fixture.CreateAsync(initialStatus, minutesOld: 60);
        await using var db = fixture.NewContext();
        var service = new DurableDmlReconciliationService(db);
        var ct = TestContext.Current.CancellationToken;
        var pending = Assert.Single(await service.ListAsync(10, ct));

        Assert.Equal("request-1", pending.RequestId);
        Assert.Equal(initialStatus, pending.Status);
        Assert.Equal(initialStatus switch
        {
            "ApprovalStateUnknown" => "TargetCommitAcknowledged",
            "RollbackOutcomeUnknown" => "RollbackUnconfirmed",
            _ => "PhysicalCommitUnknown"
        }, pending.CommitEvidence);
        Assert.Contains(resolution, pending.AllowedResolutions);
        if (initialStatus == "ApprovalStateUnknown")
            Assert.Equal(["committed"], pending.AllowedResolutions);
        var settled = await service.ReconcileAsync("request-1",
            new(initialStatus, pending.UpdatedAt, resolution, "CASE-12345",
                "Operator checked target database state against the approval window."),
            "admin-operator", ct);
        Assert.NotNull(settled);
        Assert.Equal(expectedTerminalStatus, settled.Status);
        Assert.Equal("TerminalAttestation", settled.CommitEvidence);
        Assert.Empty(settled.AllowedResolutions);

        var updated = await db.DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
        Assert.Equal(expectedTerminalStatus, updated.Status);
        Assert.Contains("CASE-12345", updated.Reason, StringComparison.Ordinal);
        Assert.Contains("admin-operator", updated.Reason, StringComparison.Ordinal);
        Assert.NotEqual("Pending", updated.Status);
        Assert.Empty(await service.ListAsync(10, ct));

        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(ct);
        Assert.Equal("dml.approval.reconciled", audit.Action);
        Assert.Equal("request-1", audit.Target);
        Assert.Equal("admin-operator", audit.ActorId);
        Assert.Equal(expectedTerminalStatus, audit.ApprovalStatus);
        Assert.Contains("CASE-12345", audit.Detail, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileAsync("request-1",
                new(initialStatus, pending.UpdatedAt, resolution, "CASE-12345", "Duplicate completion"),
                "admin-operator", ct));
        Assert.Equal(1, await db.AuditLogs.CountAsync(ct));
    }

    [Fact]
    public async Task Inventory_OnlyShowsConsumedUncertainApprovalsAndNeverProtectedSql()
    {
        await using var fixture = await Fixture.CreateAsync("OutcomeUnknown", 60);
        await using var db = fixture.NewContext();
        db.DmlApprovalRequests.Add(Fixture.CreateRequest("pending-2", "Pending", DateTime.UtcNow));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new DurableDmlReconciliationService(db);
        var list = await service.ListAsync(10, TestContext.Current.CancellationToken);
        var item = Assert.Single(list);
        Assert.Equal("request-1", item.RequestId);
        Assert.Equal("PhysicalCommitUnknown", item.CommitEvidence);
        Assert.Equal(["committed", "not_committed", "indeterminate"], item.AllowedResolutions);
        var serialized = System.Text.Json.JsonSerializer.Serialize(list);
        Assert.DoesNotContain("secret sql text", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("protected resume", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 64), serialized, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ListAsync(101, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SqliteInventory_UtcTimestampJsonRoundTrip_AllowsExpectedStateCheck()
    {
        await using var fixture = await Fixture.CreateAsync("OutcomeUnknown", minutesOld: 60);
        await using var context = fixture.NewContext();
        var ct = TestContext.Current.CancellationToken;

        // SQLite does not preserve DateTime.Kind on database round trips.
        // Without explicit UTC normalization the API serializes an ambiguous
        // timestamp, causing timezone-dependent optimistic check failures.
        var raw = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
        Assert.Equal(DateTimeKind.Unspecified, raw.UpdatedAt.Kind);
        var service = new DurableDmlReconciliationService(context);
        var item = Assert.Single(await service.ListAsync(10, ct));
        Assert.Equal(DateTimeKind.Utc, item.UpdatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, item.CreatedAt.Kind);

        var json = System.Text.Json.JsonSerializer.Serialize(item);
        Assert.Contains("Z", json, StringComparison.Ordinal);
        var fromWire = System.Text.Json.JsonSerializer.Deserialize<DurableDmlReconciliationItem>(json);
        Assert.NotNull(fromWire);

        var result = await service.ReconcileAsync(fromWire.RequestId,
            new(fromWire.Status, fromWire.UpdatedAt, "indeterminate", "INC-UTC",
                "Physical COMMIT remains unknown after review."),
            "operator-utc", ct);
        Assert.NotNull(result);
        Assert.Equal("AttestedIndeterminate", result.Status);
        Assert.Equal(DateTimeKind.Utc, result.UpdatedAt.Kind);
    }

    [Fact]
    public async Task Reconciliation_RejectsAmbiguousLocalTimestampInsteadOfGuessingTimezone()
    {
        await using var fixture = await Fixture.CreateAsync("OutcomeUnknown", minutesOld: 60);
        await using var context = fixture.NewContext();
        var ct = TestContext.Current.CancellationToken;
        var service = new DurableDmlReconciliationService(context);
        var item = Assert.Single(await service.ListAsync(10, ct));
        var noTimezone = DateTime.SpecifyKind(item.UpdatedAt, DateTimeKind.Unspecified);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReconcileAsync(item.RequestId,
            new(item.Status, noTimezone, "committed", "INC-UTC",
                "Timestamp arrived without any timezone designator."),
            "operator-utc", ct));
        Assert.Equal("OutcomeUnknown", (await context.DmlApprovalRequests.AsNoTracking().SingleAsync(ct)).Status);
        Assert.Equal(0, await context.AuditLogs.CountAsync(ct));
    }

    [Fact]
    public async Task Executing_RemainsReadOnlyEvenWhenOldWithoutWorkerFencing()
    {
        await using var fixture = await Fixture.CreateAsync("Executing", minutesOld: 120);
        await using var db = fixture.NewContext();
        var service = new DurableDmlReconciliationService(db);
        var pending = Assert.Single(await service.ListAsync(10, TestContext.Current.CancellationToken));
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("WorkerFencingRequired", pending.CommitEvidence);
        Assert.Empty(pending.AllowedResolutions);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReconcileAsync(
            pending.RequestId,
            new(pending.Status, pending.UpdatedAt, "committed", "CASE-123", "Verified log evidence"),
            "operator", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReconcileAsync(
            pending.RequestId,
            new("OutcomeUnknown", pending.UpdatedAt, "committed", "CASE-123", "Verified log evidence"),
            "operator", ct));
        Assert.Equal("Executing", (await db.DmlApprovalRequests.AsNoTracking().SingleAsync(ct)).Status);
        Assert.Equal(0, await db.AuditLogs.CountAsync(ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameStatusConcurrentEvidenceChange_CannotBeOverwrittenByStaleOperator(
        bool changeLegacyCommitReason)
    {
        await using var fixture = await Fixture.CreateAsync("OutcomeUnknown", 60);
        await using var staleContext = fixture.NewContext();
        await using var newerContext = fixture.NewContext();
        var ct = TestContext.Current.CancellationToken;
        var older = await staleContext.DmlApprovalRequests.SingleAsync(ct);
        var observation = new DurableDmlReconciliationService(staleContext);
        var item = Assert.Single(await observation.ListAsync(10, ct));
        Assert.Equal("PhysicalCommitUnknown", item.CommitEvidence);

        // A different writer changes the evidence but leaves Status exactly
        // OutcomeUnknown, so Status-only concurrency protection is insufficient.
        var newer = await newerContext.DmlApprovalRequests.SingleAsync(ct);
        if (changeLegacyCommitReason)
        {
            newer.Reason =
                "Target database COMMIT succeeded, but the Admin Store completion status could not be confirmed. " +
                "The approval is already consumed. Reconcile database and approval state; do not retry this approval.";
        }
        else
        {
            newer.UpdatedAt = newer.UpdatedAt.AddSeconds(10);
        }
        await newerContext.SaveChangesAsync(ct);

        var attestation = new DurableDmlReconciliationRequest(
            item.Status, item.UpdatedAt, "not_committed", "INC-REVIEW",
            "Old operator snapshot must never overwrite newer evidence.");
        var siem = Options.Create(new OperabilitySettings
        {
            SiemWebhookUrl = "https://siem.example.test/reconcile"
        });
        var service = new DurableDmlReconciliationService(staleContext, siem);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            service.ReconcileAsync(item.RequestId, attestation, "stale-operator", ct));

        await using var verify = fixture.NewContext();
        var persisted = await verify.DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
        Assert.Equal("OutcomeUnknown", persisted.Status);
        if (changeLegacyCommitReason)
            Assert.True(DurableDmlApprovalLifecycle.HasConfirmedTargetCommit(persisted));
        else
            Assert.Equal(newer.UpdatedAt, persisted.UpdatedAt);
        Assert.Equal(0, await verify.AuditLogs.CountAsync(ct));
        Assert.Equal(0, await verify.OutboundDeliveries.CountAsync(ct));
    }

    [Fact]
    public async Task ConcurrentOperator_StaleStatusRejectsAndRollsBackAudit()
    {
        await using var fixture = await Fixture.CreateAsync("OutcomeUnknown", 60);
        await using var firstContext = fixture.NewContext();
        await using var staleContext = fixture.NewContext();
        var ct = TestContext.Current.CancellationToken;
        // Hold the original status concurrency token in a second DbContext.
        _ = await staleContext.DmlApprovalRequests.SingleAsync(ct);
        var siem = Options.Create(new OperabilitySettings { SiemWebhookUrl = "https://siem.example.test/audit" });
        var first = new DurableDmlReconciliationService(firstContext, siem);
        var second = new DurableDmlReconciliationService(staleContext, siem);
        var item = Assert.Single(await first.ListAsync(10, ct));
        var command = new DurableDmlReconciliationRequest(item.Status, item.UpdatedAt,
            "committed", "CASE-777", "Physical rows inspected by the operator.");

        Assert.NotNull(await first.ReconcileAsync(item.RequestId, command, "first-admin", ct));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            second.ReconcileAsync(item.RequestId, command with { Note = "Second conflicting claim" },
                "second-admin", ct));
        Assert.Equal(1, await firstContext.AuditLogs.AsNoTracking().CountAsync(ct));
        Assert.Equal(1, await firstContext.OutboundDeliveries.AsNoTracking().CountAsync(ct));
        var saved = await firstContext.DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
        Assert.Equal("AttestedCommitted", saved.Status);
    }

    [Fact]
    public async Task InvalidEvidenceAndTerminalStatusesFailWithoutAudit()
    {
        await using var fixture = await Fixture.CreateAsync("OutcomeUnknown", 60);
        await using var db = fixture.NewContext();
        var service = new DurableDmlReconciliationService(db);
        var ct = TestContext.Current.CancellationToken;
        var item = Assert.Single(await service.ListAsync(10, ct));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReconcileAsync(item.RequestId,
            new(item.Status, item.UpdatedAt, "committed", "", "Evidence"), "operator", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReconcileAsync(item.RequestId,
            new(item.Status, item.UpdatedAt, "retry", "CASE-3", "Evidence"), "operator", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReconcileAsync(item.RequestId,
            new(item.Status, item.UpdatedAt, "committed", "CASE-3", "Evidence"), "", ct));
        Assert.Equal(0, await db.AuditLogs.CountAsync(ct));
        Assert.Equal("OutcomeUnknown", (await db.DmlApprovalRequests.AsNoTracking().SingleAsync(ct)).Status);
    }

    [Theory]
    [InlineData("ApprovalStateUnknown", false)]
    [InlineData("OutcomeUnknown", true)]
    public async Task ConfirmedTargetCommit_CannotBeContradictedByOperatorAttestation(
        string status, bool legacyRecord)
    {
        await using var fixture = await Fixture.CreateAsync(status, 60);
        await using var db = fixture.NewContext();
        var ct = TestContext.Current.CancellationToken;
        if (legacyRecord)
        {
            var original = await db.DmlApprovalRequests.SingleAsync(ct);
            original.Reason =
                "Target database COMMIT succeeded, but the Admin Store completion status could not be confirmed. " +
                "The approval is already consumed. Reconcile database and approval state; do not retry this approval.";
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        var service = new DurableDmlReconciliationService(db);
        var item = Assert.Single(await service.ListAsync(10, ct));
        Assert.Equal("TargetCommitAcknowledged", item.CommitEvidence);
        Assert.Equal(["committed"], item.AllowedResolutions);
        foreach (var resolution in new[] { "not_committed", "indeterminate" })
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ReconcileAsync(item.RequestId,
                    new(item.Status, item.UpdatedAt, resolution, "CASE-CONFIRMED", "Operator observation"),
                    "operator", ct));
            Assert.Contains("COMMIT is already confirmed", failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(status, (await db.DmlApprovalRequests.AsNoTracking().SingleAsync(ct)).Status);
        Assert.Equal(0, await db.AuditLogs.CountAsync(ct));
        Assert.Equal(0, await db.OutboundDeliveries.CountAsync(ct));

        var result = await service.ReconcileAsync(item.RequestId,
            new(item.Status, item.UpdatedAt, "committed", "CASE-CONFIRMED", "Reviewed confirmed transaction"),
            "operator", ct);
        Assert.NotNull(result);
        Assert.Equal("AttestedCommitted", result.Status);
    }

    [Fact]
    public async Task Reconciliation_WritesRedactedAtomicSiemOutbox_AndIgnoresSignalFailure()
    {
        await using var fixture = await Fixture.CreateAsync("OutcomeUnknown", 60);
        await using var db = fixture.NewContext();
        var ct = TestContext.Current.CancellationToken;
        var service = new DurableDmlReconciliationService(
            db,
            Options.Create(new OperabilitySettings { SiemWebhookUrl = "https://siem.example.test/events" }),
            new FailingDeliverySignal());
        var pending = Assert.Single(await service.ListAsync(10, ct));

        var outcome = await service.ReconcileAsync("request-1",
            new(pending.Status, pending.UpdatedAt, "committed",
                "INC-123 password=sensitive123",
                "Verified receipt; Bearer abc123token"),
            "admin-operator", ct);
        Assert.NotNull(outcome);
        Assert.Equal("AttestedCommitted", outcome.Status);

        var stored = await db.DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(ct);
        var delivery = await db.OutboundDeliveries.AsNoTracking().SingleAsync(ct);

        Assert.Equal("AttestedCommitted", stored.Status);
        Assert.Equal("siem", delivery.Category);
        Assert.Equal($"siem:{audit.EventId:N}", delivery.DedupeKey);
        Assert.Equal("pending", delivery.Status);
        Assert.Equal("https://siem.example.test/events", delivery.TargetUrl);
        Assert.Contains("dml.approval.reconciled", delivery.Payload, StringComparison.Ordinal);
        foreach (var raw in new[] { stored.Reason, audit.Detail, delivery.Payload })
        {
            Assert.DoesNotContain("sensitive123", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("abc123token", raw, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", raw, StringComparison.Ordinal);
        }
    }

    private sealed class FailingDeliverySignal : IOutboundDeliverySignal
    {
        public void Notify() => throw new InvalidOperationException("Worker signal unavailable");
        public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public bool TryRead() => false;
    }

    [Fact]
    public void ReconciliationEndpoint_CannotBeAuthorizedByManagementApiKey()
    {
        var list = typeof(DurableDmlReconciliationController).GetMethod("List")!;
        var resolve = typeof(DurableDmlReconciliationController).GetMethod("Resolve")!;
        foreach (var method in new[] { list, resolve })
        {
            var permission = Assert.Single(method.GetCustomAttributes(typeof(HasPermissionAttribute), true));
            Assert.Equal("/runtime/security.edit", ((HasPermissionAttribute)permission).Permission);
            Assert.False(ManagementApiKeyPermissions.IsGrantable(((HasPermissionAttribute)permission).Permission));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AdminContext> _options;

        private Fixture(SqliteConnection connection, DbContextOptions<AdminContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public AdminContext NewContext() => new(_options);

        public static async Task<Fixture> CreateAsync(string status, int minutesOld)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AdminContext>().UseSqlite(connection).Options;
            var fixture = new Fixture(connection, options);
            await using var db = fixture.NewContext();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.DmlApprovalRequests.Add(CreateRequest("request-1", status,
                DateTime.UtcNow.AddMinutes(-minutesOld)));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return fixture;
        }

        public static DmlApprovalRequestState CreateRequest(string requestId, string status, DateTime updatedAt)
            => new()
            {
                RequestId = requestId,
                Status = status,
                ApprovalFingerprint = new string('a', 64),
                EvidenceFingerprint = new string('b', 64),
                ProtectedExecutionPayload = "protected resume with secret sql text",
                RequesterIdentity = "mcp-key:7",
                TargetIdentity = "db-management:11",
                DatabaseProvider = "Sqlite",
                DatabaseIdentity = "test-target",
                AccessKeyId = 7,
                DbManagementId = 11,
                RequiredToolName = "execute_dml_sql",
                StatementCount = 1,
                TotalAffectedRows = 1,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt,
                ExpiresAt = updatedAt.AddHours(2)
            };

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
