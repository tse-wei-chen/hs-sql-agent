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

public sealed partial class DurableDmlApprovalLifecycleTests
{
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

    [Fact]
    public async Task IndependentWitnessInspection_AcceptsFixedWidthCharPadding()
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

        await using (var target = new SqliteConnection(fixture.TargetConnectionString))
        {
            await target.OpenAsync(ct);
            await using var ddl = target.CreateCommand();
            ddl.CommandText = "CREATE TABLE HS_SQL_AGENT_DML_WITNESS (" +
                "REQUEST_ID VARCHAR(64) PRIMARY KEY, EVIDENCE_FINGERPRINT CHAR(70) NOT NULL)";
            await ddl.ExecuteNonQueryAsync(ct);
            await using var insert = target.CreateCommand();
            insert.CommandText = "INSERT INTO HS_SQL_AGENT_DML_WITNESS " +
                "(REQUEST_ID, EVIDENCE_FINGERPRINT) VALUES (@id, @digest)";
            insert.Parameters.AddWithValue("@id", approval.RequestId);
            insert.Parameters.AddWithValue("@digest", state.EvidenceFingerprint + "      ");
            await insert.ExecuteNonQueryAsync(ct);
        }

        var inspection = fixture.CreateWitnessInspection(
            scope.ServiceProvider, new RelationalDmlTargetCommitWitness());
        var result = await inspection.InspectAsync(approval.RequestId, ct);
        Assert.NotNull(result);
        Assert.Equal("CommittedWitnessMatch", result.WitnessStatus);
        Assert.True(result.MatchingCommitWitness);
        Assert.Equal("OutcomeUnknown",
            (await context.DmlApprovalRequests.AsNoTracking().SingleAsync(ct)).Status);
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

}
