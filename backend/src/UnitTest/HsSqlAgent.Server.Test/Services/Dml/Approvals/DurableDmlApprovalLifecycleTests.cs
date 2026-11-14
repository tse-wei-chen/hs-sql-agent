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

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task PendingApproval_UnknownAdminStoreAdmissionProtocol_DoesNotExecute(int version)
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await PersistPendingInsertAsync(fixture);
        var ct = TestContext.Current.CancellationToken;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdminContext>();
            var record = await db.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == request.RequestId, ct);
            record.AdmissionProtocolVersion = version;
            await db.SaveChangesAsync(ct);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(request.RequestId, request.ApprovalFingerprint), ct);

            Assert.Equal(DmlApprovalCompletionStatus.ConfigurationError, result.Status);
            Assert.Equal("dml.admission_protocol_incompatible", result.ErrorCode);
            var record = await scope.ServiceProvider.GetRequiredService<AdminContext>()
                .DmlApprovalRequests.AsNoTracking().SingleAsync(
                    x => x.RequestId == request.RequestId, ct);
            Assert.Equal("Pending", record.Status);
            Assert.Equal(version, record.AdmissionProtocolVersion);
        }
        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
    }

    [Fact]
    public async Task NewPendingApproval_WritesVersionedProtectedExecutionPayload()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        await using var scope = fixture.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var state = await services.GetRequiredService<AdminContext>().DmlApprovalRequests
            .AsNoTracking().SingleAsync(
                x => x.RequestId == approval.RequestId,
                TestContext.Current.CancellationToken);

        var crypto = services.GetRequiredService<ICryptoService>();
        var json = crypto.DecryptText(
            state.ProtectedExecutionPayload,
            "dml-payload:" + state.RequestId);
        Assert.NotNull(json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(DurableDmlResumePayloadCodec.CurrentSchemaVersion,
            document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(DurableDmlResumePayloadCodec.PayloadKind,
            document.RootElement.GetProperty("Kind").GetString());
        Assert.Equal("execute_dml_sql",
            document.RootElement.GetProperty("Payload").GetProperty("RequiredToolName").GetString());
    }

    [Fact]
    public async Task FutureDurablePayloadVersion_FailsClosedAfterRestartWithoutTargetCommit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var approval = await PersistPendingInsertAsync(fixture);
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<AdminContext>();
            var state = await db.DmlApprovalRequests.SingleAsync(
                x => x.RequestId == approval.RequestId,
                TestContext.Current.CancellationToken);
            var crypto = services.GetRequiredService<ICryptoService>();
            var secret = System.Text.Encoding.UTF8.GetBytes(HmacSecret);
            var json = crypto.DecryptText(state.ProtectedExecutionPayload, "dml-payload:" + state.RequestId);
            Assert.NotNull(json);
            var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            root["SchemaVersion"] = DurableDmlResumePayloadCodec.CurrentSchemaVersion + 1;
            state.ProtectedExecutionPayload = crypto.EncryptText(root.ToJsonString(), "dml-payload:" + state.RequestId)
                ?? throw new InvalidOperationException("Could not reencrypt test fixture.");
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider, new TypedDmlRuntime());
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(
                    approval.RequestId, approval.ApprovalFingerprint),
                TestContext.Current.CancellationToken);
            Assert.Equal(DmlApprovalCompletionStatus.Failed, result.Status);
            Assert.False(result.Retryable);
            Assert.Contains("schema version", result.Message, StringComparison.OrdinalIgnoreCase);
            var state = await scope.ServiceProvider.GetRequiredService<AdminContext>()
                .DmlApprovalRequests.AsNoTracking().SingleAsync(
                    x => x.RequestId == approval.RequestId,
                    TestContext.Current.CancellationToken);
            Assert.Equal("Failed", state.Status);
        }
        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyPendingApproval_EnablingTargetFenceRequiresFreshFencedApproval(
        bool legacyHadCommitWitness)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var fence = new RelationalDmlTargetExecutionFence();
        var witness = new RelationalDmlTargetCommitWitness();
        await ProvisionTargetFenceAsync(fixture, ct);

        var legacyRuntime = legacyHadCommitWitness
            ? new TypedDmlRuntime(targetCommitWitness: witness)
            : new TypedDmlRuntime();
        var approval = await PersistPendingInsertAsync(fixture, legacyRuntime);

        // The upgraded process has target fencing, but the persisted v2 request
        // never obtained a versioned target-admission contract. It cannot use
        // this new component as retroactive proof and may not execute unfenced.
        await using (var restarted = fixture.CreateRestartedServices())
        await using (var scope = restarted.CreateAsyncScope())
        {
            var lifecycle = fixture.CreateLifecycle(scope.ServiceProvider,
                new TypedDmlRuntime(targetCommitWitness: witness, targetExecutionFence: fence));
            var result = await lifecycle.CompleteAsync(
                DmlApprovalCompletion.Approve(approval.RequestId, approval.ApprovalFingerprint), ct);

            Assert.Equal(DmlApprovalCompletionStatus.Failed, result.Status);
            Assert.False(result.Retryable);
            Assert.Contains("Legacy durable DML approval", result.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Failed", (await scope.ServiceProvider.GetRequiredService<AdminContext>()
                .DmlApprovalRequests.AsNoTracking().SingleAsync(ct)).Status);
        }

        Assert.Equal(0L, await fixture.CountTargetRowsAsync());
        await using var inspector = new SqliteConnection(fixture.TargetConnectionString);
        await inspector.OpenAsync(ct);
        Assert.Null(await fence.ReadAsync(inspector, SqlAgentToolType.Sqlite, approval.RequestId, ct));
        Assert.Null(await witness.ReadFingerprintAsync(
            inspector, SqlAgentToolType.Sqlite, approval.RequestId, ct));

        await using var secondRestart = fixture.CreateRestartedServices();
        await using var secondScope = secondRestart.CreateAsyncScope();
        var replay = await fixture.CreateLifecycle(secondScope.ServiceProvider,
                new TypedDmlRuntime(targetCommitWitness: witness, targetExecutionFence: fence))
            .CompleteAsync(DmlApprovalCompletion.Approve(
                approval.RequestId, approval.ApprovalFingerprint), ct);
        Assert.Equal(DmlApprovalCompletionStatus.AlreadyCompleted, replay.Status);
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

}
