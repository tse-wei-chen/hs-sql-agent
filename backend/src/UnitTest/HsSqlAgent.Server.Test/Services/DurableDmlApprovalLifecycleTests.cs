using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Provider.Sqlite;
using HsSqlAgent.Server.Services;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Core.Execution;
using Xunit;

namespace HsSqlAgent.Server.Test.Services;

public sealed class DurableDmlApprovalLifecycleTests
{
    private const string HmacSecret = "durable-dml-test-hmac-secret-32-bytes-minimum";
    private const int AccessKeyId = 7;
    private const int DbManagementId = 11;
    private const string RequiredTool = "execute_dml_sql";

    [Fact]
    public async Task PendingApproval_NewServiceProviderAndRuntime_ExecutesExactlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(
                    request.RequestId,
                    request.ApprovalFingerprint,
                    approverIdentity: "reviewer@example.test",
                    externalReference: "EXT-1001"),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.Executed, completion.Status);
            Assert.Equal(1, completion.AffectedRows);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var duplicate = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(
                    request.RequestId,
                    request.ApprovalFingerprint,
                    approverIdentity: "reviewer@example.test",
                    externalReference: "EXT-1001"),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.AlreadyCompleted, duplicate.Status);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task VersionedFencedDurableApproval_CommitsAdmissionWitnessAndMutationAfterRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var fence = new RelationalDmlTargetExecutionFence();
        var witness = new RelationalDmlTargetCommitWitness();
        await ProvisionTargetFenceAsync(fixture, ct);

        var request = await PersistPendingInsertAsync(fixture,
            new TypedDmlRuntime(targetCommitWitness: witness, targetExecutionFence: fence));
        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var runtime = new TypedDmlRuntime(targetCommitWitness: witness, targetExecutionFence: fence);
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, runtime);
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint), ct);
            Assert.Equal(DmlApprovalCompletionStatus.Executed, result.Status);

            var state = await scope.ServiceProvider.GetRequiredService<AdminContext>()
                .DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
            await using var target = new SqliteConnection(fixture.TargetConnectionString);
            await target.OpenAsync(ct);
            var row = await fence.ReadAsync(target, SqlAgentToolType.Sqlite, request.RequestId, ct);
            Assert.NotNull(row);
            Assert.Equal(RelationalDmlTargetExecutionFence.AdmissionKind, row.EntryKind);
            Assert.Equal(state.EvidenceFingerprint, row.EvidenceFingerprint);
            Assert.Equal(state.EvidenceFingerprint, await witness.ReadFingerprintAsync(
                target, SqlAgentToolType.Sqlite, request.RequestId, ct));
        }
        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task FencedApproval_RestartWithoutFencingComponent_FailsClosedBeforeTargetMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var fence = new RelationalDmlTargetExecutionFence();
        var witness = new RelationalDmlTargetCommitWitness();
        await ProvisionTargetFenceAsync(fixture, ct);
        var request = await PersistPendingInsertAsync(fixture,
            new TypedDmlRuntime(targetCommitWitness: witness, targetExecutionFence: fence));

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider,
                new TypedDmlRuntime(targetCommitWitness: witness));
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint), ct);
            Assert.Equal(DmlApprovalCompletionStatus.Failed, result.Status);
            Assert.False(result.Retryable);
        }
        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
        await using var target = new SqliteConnection(fixture.TargetConnectionString);
        await target.OpenAsync(ct);
        Assert.Null(await fence.ReadAsync(target, SqlAgentToolType.Sqlite, request.RequestId, ct));
        Assert.Null(await witness.ReadFingerprintAsync(
            target, SqlAgentToolType.Sqlite, request.RequestId, ct));
    }

    private static async Task ProvisionTargetFenceAsync(Fixture fixture, CancellationToken ct)
    {
        await using var target = new SqliteConnection(fixture.TargetConnectionString);
        await target.OpenAsync(ct);
        await using var ddl = target.CreateCommand();
        ddl.CommandText =
            "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
            "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL);" +
            "CREATE TABLE HS_SQL_AGENT_DML_FENCE (" +
            "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL, " +
            "ENTRY_KIND VARCHAR(16) NOT NULL CHECK (ENTRY_KIND IN ('Admitted','Revoked')));";
        await ddl.ExecuteNonQueryAsync(ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WitnessEnrolledApproval_RestartCannotDowngradeRequiredTargetCommitWitness(
        bool witnessAvailableAfterRestart)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var witness = new RelationalDmlTargetCommitWitness();
        // Persist the witness requirement at approval creation, not by
        // consulting whatever plugins happen to be loaded after a restart.
        var approval = await PersistPendingInsertAsync(fixture,
            new TypedDmlRuntime(targetCommitWitness: witness));

        await using (var target = new SqliteConnection(fixture.TargetConnectionString))
        {
            await target.OpenAsync(ct);
            await using var ddl = target.CreateCommand();
            ddl.CommandText = "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL)";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var resumedRuntime = new TypedDmlRuntime(
                targetCommitWitness: witnessAvailableAfterRestart ? witness : null);
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, resumedRuntime);
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(approval.RequestId, approval.ApprovalFingerprint), ct);
            Assert.Equal(witnessAvailableAfterRestart
                ? DmlApprovalCompletionStatus.Executed
                : DmlApprovalCompletionStatus.Failed, completion.Status);
            Assert.False(completion.Retryable);

            var state = await scope.ServiceProvider.GetRequiredService<AdminContext>()
                .DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
            Assert.Equal(witnessAvailableAfterRestart ? "Executed" : "Failed", state.Status);
            if (!witnessAvailableAfterRestart)
                Assert.Contains("witness", completion.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using var verify = new SqliteConnection(fixture.TargetConnectionString);
        await verify.OpenAsync(ct);
        var observed = await witness.ReadFingerprintAsync(
            verify, SqlAgentToolType.Sqlite, approval.RequestId, ct);
        Assert.Equal(witnessAvailableAfterRestart ? 1L : 0L,
            await fixture.CountTargetRowsAsync());
        if (witnessAvailableAfterRestart)
            Assert.NotNull(observed);
        else
            Assert.Null(observed);

        await using var replayServices = fixture.CreateRestartedServices();
        await using var replayScope = replayServices.CreateAsyncScope();
        var replayLifecycle = fixture.CreateLifecycle(replayScope.ServiceProvider,
            new TypedDmlRuntime(targetCommitWitness: witness));
        var replay = await replayLifecycle.CompleteAsync(
            DmlApprovalCompletion.Approve(approval.RequestId, approval.ApprovalFingerprint), ct);
        Assert.Equal(DmlApprovalCompletionStatus.AlreadyCompleted, replay.Status);
    }

    [Fact]
    public async Task DurableApproval_WithTargetEnlistedWitness_BindsCommitToPersistedEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        var ct = TestContext.Current.CancellationToken;

        await using (var target = new SqliteConnection(fixture.TargetConnectionString))
        {
            await target.OpenAsync(ct);
            await using var ddl = target.CreateCommand();
            ddl.CommandText = "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL)";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        var witness = new RelationalDmlTargetCommitWitness();
        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider,
                new TypedDmlRuntime(targetCommitWitness: witness));
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(approval.RequestId, approval.ApprovalFingerprint),
                ct);
            Assert.Equal(DmlApprovalCompletionStatus.Executed, result.Status);
            var state = await scope.ServiceProvider.GetRequiredService<AdminContext>()
                .DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
            await using var target = new SqliteConnection(fixture.TargetConnectionString);
            await target.OpenAsync(ct);
            Assert.Equal(state.EvidenceFingerprint,
                await witness.ReadFingerprintAsync(target, SqlAgentToolType.Sqlite,
                    approval.RequestId, ct));
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider,
                new TypedDmlRuntime(targetCommitWitness: witness));
            var replay = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(approval.RequestId, approval.ApprovalFingerprint),
                ct);
            Assert.Equal(DmlApprovalCompletionStatus.AlreadyCompleted, replay.Status);
        }
        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task IndependentWitnessInspection_OnlyConfirmsMatchingTargetAndBlocksContraryAttestation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        await using (var target = new SqliteConnection(fixture.TargetConnectionString))
        {
            await target.OpenAsync(ct);
            await using var ddl = target.CreateCommand();
            ddl.CommandText = "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL)";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        var witness = new RelationalDmlTargetCommitWitness();
        await using var restarted = fixture.CreateRestartedServices();
        await using var scope = restarted.CreateAsyncScope();
        var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider,
            new TypedDmlRuntime(targetCommitWitness: witness));
        var committed = await lifecycle.CompleteAsync(
            DmlApprovalCompletion.Approve(approval.RequestId, approval.ApprovalFingerprint), ct);
        Assert.Equal(DmlApprovalCompletionStatus.Executed, committed.Status);

        var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
        var state = await context.DmlApprovalRequests.SingleAsync(ct);
        state.Status = "OutcomeUnknown";
        state.Reason = "Target COMMIT response to the control plane is not known.";
        state.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var inspection = fixture.CreateWitnessInspection(scope.ServiceProvider, witness);
        var result = await inspection.InspectAsync(approval.RequestId, ct);
        Assert.NotNull(result);
        Assert.Equal("OutcomeUnknown", result.ApprovalStatus);
        Assert.Equal("CommittedWitnessMatch", result.WitnessStatus);
        Assert.True(result.MatchingCommitWitness);
        var responseJson = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("INSERT INTO items", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(state.EvidenceFingerprint, responseJson, StringComparison.Ordinal);

        var pending = Assert.Single(await new DurableDmlReconciliationService(context).ListAsync(10, ct));
        var reconcile = new DurableDmlReconciliationService(context,
            witnessVerification: inspection);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconcile.ReconcileAsync(approval.RequestId,
                new(pending.Status, pending.UpdatedAt, "not_committed", "CASE-1",
                    "An operator contradicts a matching commit witness."),
                "operator", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconcile.ReconcileAsync(approval.RequestId,
                new(pending.Status, pending.UpdatedAt, "indeterminate", "CASE-1",
                    "An operator ignores a matching commit witness."),
                "operator", ct));
        Assert.Equal(0, await context.AuditLogs.CountAsync(ct));
        Assert.Equal(0, await context.OutboundDeliveries.CountAsync(ct));
        Assert.Equal("OutcomeUnknown",
            (await context.DmlApprovalRequests.AsNoTracking().SingleAsync(ct)).Status);

        var attested = await reconcile.ReconcileAsync(approval.RequestId,
            new(pending.Status, pending.UpdatedAt, "committed", "CASE-1",
                "Independently inspected target witness fingerprint."),
            "operator", ct);
        Assert.NotNull(attested);
        Assert.Equal("AttestedCommitted", attested.Status);
        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task IndependentWitnessInspection_MissingRowDoesNotFenceExecuting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        await using (var target = new SqliteConnection(fixture.TargetConnectionString))
        {
            await target.OpenAsync(ct);
            await using var ddl = target.CreateCommand();
            ddl.CommandText = "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL)";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
        var state = await context.DmlApprovalRequests.SingleAsync(ct);
        state.Status = "Executing";
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var inspection = fixture.CreateWitnessInspection(
            scope.ServiceProvider, new RelationalDmlTargetCommitWitness());
        var result = await inspection.InspectAsync(approval.RequestId, ct);
        Assert.NotNull(result);
        Assert.Equal("NotObserved", result.WitnessStatus);
        Assert.False(result.MatchingCommitWitness);
        Assert.Contains("never proves non-commit", result.Limitation, StringComparison.Ordinal);
        var recon = new DurableDmlReconciliationService(context, witnessVerification: inspection);
        var item = Assert.Single(await recon.ListAsync(10, ct));
        Assert.Empty(item.AllowedResolutions);
        await Assert.ThrowsAsync<ArgumentException>(() => recon.ReconcileAsync(
            approval.RequestId, new(item.Status, item.UpdatedAt, "not_committed",
                "CASE-2", "No witness was observed."), "operator", ct));
        Assert.Equal(0L, await fixture.CountTargetRowsAsync());

        var disabled = fixture.CreateWitnessInspection(scope.ServiceProvider, null);
        Assert.Equal("WitnessNotConfigured",
            (await disabled.InspectAsync(approval.RequestId, ct))?.WitnessStatus);
    }

    [Theory]
    [InlineData("changed-target", "TargetConfigurationChanged")]
    [InlineData("mismatch", "EvidenceMismatch")]
    [InlineData("unavailable", "ReadUnavailable")]
    public async Task IndependentWitnessInspection_RejectsDriftForgeryAndReadFailures(
        string scenario, string expectedStatus)
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        await using (var target = new SqliteConnection(fixture.TargetConnectionString))
        {
            await target.OpenAsync(ct);
            await using var ddl = target.CreateCommand();
            ddl.CommandText = "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT VARCHAR(64) NOT NULL)";
            await ddl.ExecuteNonQueryAsync(ct);
        }

        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
        var state = await context.DmlApprovalRequests.SingleAsync(ct);
        state.Status = "OutcomeUnknown";
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        if (scenario == "changed-target")
        {
            var config = await context.DbManagement.SingleAsync(ct);
            config.Database = "some-other-target.db";
            await context.SaveChangesAsync(ct);
        }
        else if (scenario == "mismatch")
        {
            await using var target = new SqliteConnection(fixture.TargetConnectionString);
            await target.OpenAsync(ct);
            await using var stmt = target.CreateCommand();
            stmt.CommandText = "INSERT INTO HS_SQL_AGENT_DML_WITNESS " +
                "(REQUEST_ID, EVIDENCE_FINGERPRINT) VALUES (@id, @digest)";
            stmt.Parameters.AddWithValue("@id", approval.RequestId);
            stmt.Parameters.AddWithValue("@digest", new string('F', 64));
            await stmt.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var target = new SqliteConnection(fixture.TargetConnectionString);
            await target.OpenAsync(ct);
            await using var stmt = target.CreateCommand();
            stmt.CommandText = "DROP TABLE HS_SQL_AGENT_DML_WITNESS";
            await stmt.ExecuteNonQueryAsync(ct);
        }

        var inspection = fixture.CreateWitnessInspection(
            scope.ServiceProvider, new RelationalDmlTargetCommitWitness());
        var result = await inspection.InspectAsync(approval.RequestId, ct);
        Assert.NotNull(result);
        Assert.Equal(expectedStatus, result.WitnessStatus);
        Assert.False(result.MatchingCommitWitness);
        Assert.DoesNotContain("some-other-target.db", result.Limitation, StringComparison.Ordinal);
        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("protected-payload")]
    [InlineData("access-key")]
    [InlineData("provider")]
    [InlineData("database-identity")]
    public async Task IndependentWitnessInspection_RejectsApprovalBindingChangedDuringProbe(
        string changedField)
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
        var state = await context.DmlApprovalRequests.SingleAsync(ct);
        state.Status = "OutcomeUnknown";
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        // A second Admin Store context mutates a binding while the independent
        // target read is in flight. The inspected snapshot is now stale.
        var fake = new CallbackWitness(async () =>
        {
            await using var concurrentServices = fixture.CreateRestartedServices();
            await using var concurrentScope = concurrentServices.CreateAsyncScope();
            var concurrent = concurrentScope.ServiceProvider.GetRequiredService<AdminContext>();
            var row = await concurrent.DmlApprovalRequests.SingleAsync(ct);
            switch (changedField)
            {
                case "fingerprint": row.EvidenceFingerprint = new string('B', 64); break;
                case "protected-payload": row.ProtectedExecutionPayload = "changed-payload"; break;
                case "access-key": row.AccessKeyId += 1; break;
                case "provider": row.DatabaseProvider = "MySQL"; break;
                case "database-identity": row.DatabaseIdentity = "other"; break;
            }
            await concurrent.SaveChangesAsync(ct);
            return null;
        });
        var inspection = fixture.CreateWitnessInspection(scope.ServiceProvider, fake);
        var result = await inspection.InspectAsync(approval.RequestId, ct);
        Assert.NotNull(result);
        Assert.Equal("ApprovalChanged", result.WitnessStatus);
        Assert.False(result.MatchingCommitWitness);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("provider")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("username")]
    [InlineData("password")]
    [InlineData("extra-settings")]
    public async Task IndependentWitnessInspection_RejectsTargetConfigurationChangedDuringProbe(
        string changedField)
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
        var state = await context.DmlApprovalRequests.SingleAsync(ct);
        state.Status = "OutcomeUnknown";
        await context.SaveChangesAsync(ct);
        context.ChangeTracker.Clear();

        var fake = new CallbackWitness(async () =>
        {
            await using var services = fixture.CreateRestartedServices();
            await using var concurrentScope = services.CreateAsyncScope();
            var concurrent = concurrentScope.ServiceProvider.GetRequiredService<AdminContext>();
            var db = await concurrent.DbManagement.SingleAsync(ct);
            switch (changedField)
            {
                case "database": db.Database = "retargeted.db"; break;
                case "provider": db.SqlProvider = "Postgres"; break;
                case "host": db.Host = "retargeted-host"; break;
                case "port": db.Port = "5432"; break;
                case "username": db.Username = "retargeted-user"; break;
                case "password": db.PasswordHash = "retargeted-ciphertext"; break;
                case "extra-settings": db.ExtraSettings = "retargeted=1"; break;
            }
            await concurrent.SaveChangesAsync(ct);
            return null;
        });
        var inspection = fixture.CreateWitnessInspection(scope.ServiceProvider, fake);
        var result = await inspection.InspectAsync(approval.RequestId, ct);
        Assert.NotNull(result);
        Assert.Equal("TargetConfigurationChanged", result.WitnessStatus);
        Assert.False(result.MatchingCommitWitness);
    }

    private sealed class CallbackWitness(Func<Task<string?>> callback) : IDmlTargetCommitWitness
    {
        public Task WriteAsync(System.Data.Common.DbConnection connection,
            System.Data.Common.DbTransaction transaction, SqlAgentToolType provider,
            DmlTargetCommitWitnessEvidence evidence, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> ReadFingerprintAsync(System.Data.Common.DbConnection openConnection,
            SqlAgentToolType provider, string requestId, CancellationToken cancellationToken) =>
            callback();
    }

    [Fact]
    public async Task DurableApproval_WithWitnessEnabledButTableMissing_FailsWithoutMutation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider,
                new TypedDmlRuntime(targetCommitWitness: new RelationalDmlTargetCommitWitness()));
            var outcome = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(approval.RequestId, approval.ApprovalFingerprint),
                ct);
            Assert.Equal(DmlApprovalCompletionStatus.Failed, outcome.Status);
            Assert.False(outcome.Retryable);
            var state = await scope.ServiceProvider.GetRequiredService<AdminContext>()
                .DmlApprovalRequests.AsNoTracking().SingleAsync(ct);
            Assert.Equal("Failed", state.Status);
        }
        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task AuditFailureAfterSuccessfulDatabaseCommit_DoesNotRewriteExecutedAsFailed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(failAudit: true))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.Executed, completion.Status);
            Assert.Equal(1, completion.AffectedRows);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var persisted = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            Assert.Equal("Executed", persisted.Status);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());

        await using (var services = fixture.CreateRestartedServices())
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var replay = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.AlreadyCompleted, replay.Status);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetCommittedButAdminStateSaveFails_NeverReturnsFailedOrReexecutes(
        bool fallbackStateSaveAlsoFails)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(
            failTerminalSave: true,
            failUnknownSave: fallbackStateSaveAlsoFails))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, result.Status);
            Assert.Equal("dml.approval_state_unknown", result.ErrorCode);
            Assert.False(result.Retryable);
            Assert.Equal(1, result.AffectedRows);
            Assert.Contains("COMMIT succeeded", result.Message, StringComparison.Ordinal);
            Assert.Contains("do not retry", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            Assert.Equal(
                fallbackStateSaveAlsoFails ? "Executing" : "ApprovalStateUnknown",
                state.Status);
            Assert.NotEqual("Failed", state.Status);

            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var retry = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(
                fallbackStateSaveAlsoFails
                    ? DmlApprovalCompletionStatus.AlreadyProcessing
                    : DmlApprovalCompletionStatus.OutcomeUnknown,
                retry.Status);
            Assert.False(retry.Retryable);
            Assert.Contains("Do not retry", retry.Message, StringComparison.OrdinalIgnoreCase);
            if (!fallbackStateSaveAlsoFails)
                Assert.Equal("dml.approval_state_unknown", retry.ErrorCode);
        }

        // A fresh service instance must not execute the approval again.
        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task AdminTerminalSaveAcknowledgedByDatabaseButResponseLost_RemainsNonReplayable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var services = fixture.CreateRestartedServices(
            failTerminalSave: true,
            failTerminalSaveAfterSuccess: true))
        await using (var scope = services.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            // A missing Admin Store acknowledgement does not change the known
            // outcome of the separate target SQL transaction.
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, result.Status);
            Assert.Equal("dml.approval_state_unknown", result.ErrorCode);
            Assert.False(result.Retryable);
            Assert.Contains("COMMIT succeeded", result.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var stored = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId, TestContext.Current.CancellationToken);
            // The first terminal write completed. The fallback might have
            // conservatively replaced it with ApprovalStateUnknown; neither permits
            // a second execution.
            Assert.Contains(stored.Status, new[] { "Executed", "ApprovalStateUnknown" });

            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var repeat = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Contains(repeat.Status, new[]
            {
                DmlApprovalCompletionStatus.AlreadyCompleted,
                DmlApprovalCompletionStatus.OutcomeUnknown
            });
            if (stored.Status == "ApprovalStateUnknown")
                Assert.Equal("dml.approval_state_unknown", repeat.ErrorCode);
        }

        Assert.Equal(1L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task LegacyApprovalStateUnknown_RemainsClassifiedWithoutReExecuting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "OutcomeUnknown";
            state.Reason =
                "Target database COMMIT succeeded, but the Admin Store completion status could not be confirmed. " +
                "The approval is already consumed. Reconcile database and approval state; do not retry this approval.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var repeat = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, repeat.Status);
            Assert.Equal("dml.approval_state_unknown", repeat.ErrorCode);
            Assert.False(repeat.Retryable);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task ApprovalStateUnknown_ClassificationDoesNotDependOnReasonText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "ApprovalStateUnknown";
            state.Reason = "Operator-supplied annotation, not a classification string.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var replay = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, replay.Status);
            Assert.Equal("dml.approval_state_unknown", replay.ErrorCode);
            Assert.False(replay.Retryable);
            Assert.Contains("COMMIT succeeded", replay.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PreviouslyUnconfirmedRollback_RemainsDistinctAndNonReplayableAfterRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "RollbackOutcomeUnknown";
            state.Reason = "Mutation failed and ROLLBACK acknowledgement was lost.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var replay = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, replay.Status);
            Assert.Equal("dml.rollback_outcome_unknown", replay.ErrorCode);
            Assert.False(replay.Retryable);
            Assert.Contains("could not be confirmed", replay.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Do not retry", replay.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PreviouslyUnknownCommitOutcome_RemainsTerminalWithoutReExecutingApproval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            state.Status = "OutcomeUnknown";
            state.Reason = "COMMIT acknowledgement lost; physical state requires reconciliation.";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var retry = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.OutcomeUnknown, retry.Status);
            Assert.Equal("dml.commit_outcome_unknown", retry.ErrorCode);
            Assert.False(retry.Retryable);
            Assert.Contains("Do not retry", retry.Message, StringComparison.OrdinalIgnoreCase);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            Assert.Equal("OutcomeUnknown", state.Status);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PendingApproval_AccessKeyRevokedBeforeCompletion_BecomesStaleWithoutCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var key = await context.McpAccessKeys.SingleAsync(
                x => x.Id == AccessKeyId,
                TestContext.Current.CancellationToken);
            key.IsActive = false;
            key.RevokedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(
                    request.RequestId,
                    request.ApprovalFingerprint,
                    approverIdentity: "reviewer@example.test"),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.Stale, completion.Status);
            Assert.Contains("access key", completion.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task PendingApproval_WrongApprovalFingerprint_IsRejectedWithoutClaimOrCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);

        await using (var restartedServices = fixture.CreateRestartedServices())
        await using (var scope = restartedServices.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var completion = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, new string('0', 64)),
                TestContext.Current.CancellationToken);

            Assert.Equal(DmlApprovalCompletionStatus.InvalidApproval, completion.Status);

            var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var state = await context.DmlApprovalRequests.AsNoTracking().SingleAsync(
                x => x.RequestId == request.RequestId,
                TestContext.Current.CancellationToken);
            Assert.Equal("Pending", state.Status);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    private static async Task<DmlApprovalRequest> PersistPendingInsertAsync(
        Fixture fixture, TypedDmlRuntime? runtimeOverride = null)
    {
        const string sql = "INSERT INTO items (id, name) VALUES (1, 'Alice')";
        var runtime = runtimeOverride ?? new TypedDmlRuntime();
        var policyState = CreatePolicyState();
        var policy = policyState.GetCurrent();
        var approvalContext = new DmlApprovalExecutionContext(
            $"mcp-key:{AccessKeyId}",
            $"db-management:{DbManagementId}",
            SqlAgentToolType.Sqlite,
            fixture.TargetDatabasePath);

        var parsed = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
            fixture.SqlProvider,
            fixture.TargetConnectionString,
            sql,
            SqlAgentToolType.Sqlite,
            TestContext.Current.CancellationToken);
        var session = await runtime.PreviewTransactionAsync(
            fixture.SqlProvider,
            fixture.TargetConnectionString,
            parsed,
            policy,
            allowedTables: null,
            approvalContext,
            TestContext.Current.CancellationToken);
        var request = DmlApprovalRequestFactory.Create("Insert test row", approvalContext, session);

        await using var scope = fixture.Services.CreateAsyncScope();
        var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, runtime, policyState);
        await lifecycle.PersistPendingAsync(
            request,
            DmlApprovalResult.Pending(request, "EXT-1001"),
            DmlApprovalRequestFactory.ComputeEvidenceFingerprint(session),
            new DmlApprovalResumeContext(
                sql,
                RequiredTool,
                AccessKeyId,
                DbManagementId,
                SqlAgentToolType.Sqlite),
            fixture.TargetConnectionString,
            TestContext.Current.CancellationToken);

        return request;
    }

    private static SecurityPolicyRuntimeState CreatePolicyState()
    {
        var state = new SecurityPolicyRuntimeState();
        state.SetCurrent(new SecurityPolicyModel
        {
            DmlMaxAffectedRows = 100,
            MaxConcurrentSql = 16,
            RequireWhereForUpdate = true,
            RequireWhereForDelete = true
        });
        return state;
    }

    /// <summary>
    /// Inject Admin Store persistence failures only after the target DB has
    /// finished committing. The first Executing claim must always persist.
    /// </summary>
    private sealed class TerminalApprovalSaveFailureInterceptor(
        bool failUnknownSave,
        bool failTerminalSaveAfterSuccess)
        : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var statuses = eventData.Context?.ChangeTracker
                .Entries<DmlApprovalRequestState>()
                .Where(x => x.State == EntityState.Modified)
                .Select(x => x.Entity.Status)
                .ToArray() ?? [];

            if ((!failTerminalSaveAfterSuccess
                    && statuses.Contains("Executed", StringComparer.Ordinal))
                || (failUnknownSave && statuses.Contains("ApprovalStateUnknown", StringComparer.Ordinal)))
                throw new IOException("Injected Admin Store terminal SaveChanges failure.");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (failTerminalSaveAfterSuccess
                && eventData.Context?.ChangeTracker
                    .Entries<DmlApprovalRequestState>()
                    .Any(x => x.Entity.Status == "Executed") == true)
            {
                // EF has persisted the status; simulate losing its reply.
                throw new IOException("Injected lost Admin Store SaveChanges acknowledgement.");
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string adminDatabasePath,
            string targetDatabasePath,
            ServiceProvider services,
            SqliteProvider sqlProvider,
            Mock<ISqlProviderFactory> providerFactory,
            Mock<ISqlConnectionStringFactory> connectionStringFactory)
        {
            AdminDatabasePath = adminDatabasePath;
            TargetDatabasePath = targetDatabasePath;
            Services = services;
            SqlProvider = sqlProvider;
            ProviderFactory = providerFactory;
            ConnectionStringFactory = connectionStringFactory;
            TargetConnectionString = sqlProvider.BuildConnectionString(new BuildDbConnectionModelBase
            {
                Database = targetDatabasePath
            });
        }

        public string AdminDatabasePath { get; }
        public string TargetDatabasePath { get; }
        public string TargetConnectionString { get; }
        public ServiceProvider Services { get; }
        public SqliteProvider SqlProvider { get; }
        private Mock<ISqlProviderFactory> ProviderFactory { get; }
        private Mock<ISqlConnectionStringFactory> ConnectionStringFactory { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var adminPath = Path.Combine(Path.GetTempPath(), $"hsqlagent-durable-admin-{Guid.NewGuid():N}.db");
            var targetPath = Path.Combine(Path.GetTempPath(), $"hsqlagent-durable-target-{Guid.NewGuid():N}.db");
            var sqlProvider = new SqliteProvider();
            var targetConnectionString = sqlProvider.BuildConnectionString(new BuildDbConnectionModelBase
            {
                Database = targetPath
            });

            var providerFactory = new Mock<ISqlProviderFactory>(MockBehavior.Strict);
            providerFactory.Setup(x => x.GetProvider(SqlAgentToolType.Sqlite)).Returns(sqlProvider);

            var connectionStringFactory = new Mock<ISqlConnectionStringFactory>(MockBehavior.Strict);
            connectionStringFactory
                .Setup(x => x.BuildConnectionString(
                    SqlAgentToolType.Sqlite,
                    It.IsAny<BuildDbConnectionModelBase>()))
                .Returns(targetConnectionString);

            var services = BuildServices(adminPath);

            await using (var scope = services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<AdminContext>();
                await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                context.DbManagement.Add(new DbManagement
                {
                    Id = DbManagementId,
                    Name = "durable-target",
                    SqlProvider = SqlAgentToolType.Sqlite.ToString(),
                    Database = targetPath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                context.McpAccessKeys.Add(new McpAccessKey
                {
                    Id = AccessKeyId,
                    Name = "durable-test-key",
                    KeyPrefix = "durable",
                    KeyHash = "test-key-hash",
                    IsActive = true,
                    AllowedTools = RequiredTool,
                    DbManagementId = DbManagementId,
                    CreatedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await using (var target = new SqliteConnection(targetConnectionString))
            {
                await target.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = target.CreateCommand();
                command.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY, name TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            return new Fixture(
                adminPath,
                targetPath,
                services,
                sqlProvider,
                providerFactory,
                connectionStringFactory);
        }

        public ServiceProvider CreateRestartedServices(
            bool failAudit = false,
            bool failTerminalSave = false,
            bool failUnknownSave = false,
            bool failTerminalSaveAfterSuccess = false) =>
            BuildServices(
                AdminDatabasePath, failAudit, failTerminalSave,
                failUnknownSave, failTerminalSaveAfterSuccess);

        private static ServiceProvider BuildServices(
            string adminDatabasePath,
            bool failAudit = false,
            bool failTerminalSave = false,
            bool failUnknownSave = false,
            bool failTerminalSaveAfterSuccess = false)
        {
            var adminConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = adminDatabasePath
            }.ToString();
            var serviceCollection = new ServiceCollection();
            var terminalFailure = failTerminalSave
                ? new TerminalApprovalSaveFailureInterceptor(
                    failUnknownSave, failTerminalSaveAfterSuccess)
                : null;
            serviceCollection.AddDbContext<AdminContext>(options =>
            {
                options.UseSqlite(adminConnectionString);
                if (terminalFailure is not null)
                    options.AddInterceptors(terminalFailure);
            });
            serviceCollection.AddScoped<IAdminContext>(sp => sp.GetRequiredService<AdminContext>());
            if (failAudit)
            {
                var audit = new Mock<IAuditService>(MockBehavior.Strict);
                audit.Setup(x => x.WriteEventAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<AuditEventContext>(), It.IsAny<string>(),
                        It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new IOException("Audit database is unavailable after target COMMIT."));
                serviceCollection.AddSingleton(audit.Object);
            }
            serviceCollection.AddSingleton<ICryptoService, CryptoService>();
            serviceCollection.Configure<McpKeySettings>(options => options.HmacSecretKey = HmacSecret);
            return serviceCollection.BuildServiceProvider();
        }

        public DurableDmlWitnessVerificationService CreateWitnessInspection(
            IServiceProvider scopedServices, IDmlTargetCommitWitness? witness) =>
            new(
                scopedServices.GetRequiredService<AdminContext>(),
                scopedServices.GetRequiredService<ICryptoService>(),
                scopedServices.GetRequiredService<IOptions<McpKeySettings>>(),
                ProviderFactory.Object,
                ConnectionStringFactory.Object,
                witness);

        public DurableDmlApprovalLifecycle CreateLifecycle(
            IServiceProvider scopedServices,
            TypedDmlRuntime runtime,
            SecurityPolicyRuntimeState? policyState = null)
        {
            var currentPolicyState = policyState ?? CreatePolicyState();
            return new DurableDmlApprovalLifecycle(
                scopedServices,
                runtime,
                currentPolicyState,
                new SqlExecutionConcurrencyLimiter(currentPolicyState),
                ProviderFactory.Object,
                ConnectionStringFactory.Object);
        }

        public async Task<long> CountTargetRowsAsync()
        {
            await using var connection = new SqliteConnection(TargetConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM items;";
            return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(AdminDatabasePath)) File.Delete(AdminDatabasePath);
            if (File.Exists(TargetDatabasePath)) File.Delete(TargetDatabasePath);
        }
    }
}
