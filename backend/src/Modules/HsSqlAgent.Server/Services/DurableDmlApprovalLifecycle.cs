using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Common.Interfaces;
using Common.Services;
using HsSqlAgent.Approvals;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.Server.Tools;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlAgent.Service.Factories;
using SqlAgent.Service.Core.Execution;

namespace HsSqlAgent.Server.Services;

internal sealed record DmlApprovalResumeContext(
    string Sql,
    string RequiredToolName,
    int AccessKeyId,
    int DbManagementId,
    SqlAgentToolType SourceDialect,
    int? CustomToolId = null,
    int? CustomToolRevisionId = null);

internal interface IDurableDmlApprovalLifecycle
{
    Task PersistPendingAsync(
        DmlApprovalRequest request,
        DmlApprovalResult pendingResult,
        string evidenceFingerprint,
        DmlApprovalResumeContext resumeContext,
        string connectionString,
        CancellationToken cancellationToken);
}

internal sealed class DurableDmlApprovalLifecycle(
    IServiceProvider services,
    TypedDmlRuntime runtime,
    ISecurityPolicyRuntimeState securityPolicyRuntimeState,
    ISqlExecutionConcurrencyLimiter concurrencyLimiter,
    ISqlProviderFactory sqlProviderFactory,
    ISqlConnectionStringFactory connectionStringFactory)
    : IDmlApprovalCompletionSink, IDurableDmlApprovalLifecycle
{
    private const string Pending = "Pending";
    private const string Executing = "Executing";
    private const string Executed = "Executed";
    private const string Rejected = "Rejected";
    private const string Stale = "Stale";
    private const string Expired = "Expired";
    private const string Failed = "Failed";
    private const string OutcomeUnknown = "OutcomeUnknown";
    // A failed ROLLBACK is not evidence of an actual rollback. This state
    // remains consumed and intentionally distinct from a lost COMMIT reply.
    private const string RollbackOutcomeUnknown = "RollbackOutcomeUnknown";
    // Distinct, persisted Admin Store uncertainty. Fits the existing 24-character Status column.
    private const string ApprovalStateUnknown = "ApprovalStateUnknown";
    // The target COMMIT was acknowledged, but the separate Admin Store write
    // could not be confirmed. Keep the human explanation separate from the
    // machine-readable Status so editing Reason never changes the error code.
    private const string ConfirmedCommitStateUnknownReason =
        "Target database COMMIT succeeded, but the Admin Store completion status could not be confirmed. " +
        "The approval is already consumed. Reconcile database and approval state; do not retry this approval.";
    private const string ApprovalStateUnknownCode = "dml.approval_state_unknown";
    internal const string TargetFenceProtocol = "target-fence-v1";
    internal const int AdminStoreAdmissionProtocolVersion = 1;

    public async Task PersistPendingAsync(
        DmlApprovalRequest request,
        DmlApprovalResult pendingResult,
        string evidenceFingerprint,
        DmlApprovalResumeContext resumeContext,
        string connectionString,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pendingResult);
        ArgumentNullException.ThrowIfNull(resumeContext);
        if (pendingResult.Decision != DmlApprovalDecision.Pending)
            throw new ArgumentException("Only Pending approval results can be persisted.", nameof(pendingResult));
        if (string.IsNullOrWhiteSpace(pendingResult.ExternalReference))
            throw new InvalidOperationException("A Pending DML approval must include an external reference.");

        var context = RequireAdminContext();
        var secret = RequireHmacSecret();
        var crypto = RequireCrypto();
        var now = DateTime.UtcNow;
        var durableUntil = request.DurableUntil?.UtcDateTime
                           ?? throw new InvalidOperationException("DML approval request has no durable completion deadline.");
        if (durableUntil <= now)
            throw new InvalidOperationException("DML approval durable completion deadline has already expired.");

        if (runtime.HasTargetExecutionFence && !runtime.HasTargetCommitWitness)
            throw new InvalidOperationException("Target execution fencing requires target commit witness to be configured.");

        var payload = new DurableDmlResumePayload(
            resumeContext.Sql,
            resumeContext.RequiredToolName,
            resumeContext.AccessKeyId,
            resumeContext.DbManagementId,
            resumeContext.SourceDialect,
            resumeContext.CustomToolId,
            resumeContext.CustomToolRevisionId,
            ComputeConnectionFingerprint(connectionString, secret),
            runtime.HasTargetExecutionFence ? TargetFenceProtocol : null,
            RequiresCommitWitness: runtime.HasTargetCommitWitness);
        var protectedPayload = crypto.EncryptText(DurableDmlResumePayloadCodec.Serialize(payload), secret)
                               ?? throw new InvalidOperationException("Could not protect durable DML approval payload.");

        context.DmlApprovalRequests.Add(new DmlApprovalRequestState
        {
            RequestId = request.RequestId,
            ApprovalFingerprint = request.ApprovalFingerprint,
            EvidenceFingerprint = evidenceFingerprint,
            Status = Pending,
            AdmissionProtocolVersion = AdminStoreAdmissionProtocolVersion,
            ProtectedExecutionPayload = protectedPayload,
            RequesterIdentity = request.RequesterIdentity,
            TargetIdentity = request.TargetIdentity,
            DatabaseProvider = request.DatabaseProvider,
            DatabaseIdentity = request.DatabaseIdentity,
            AccessKeyId = resumeContext.AccessKeyId,
            DbManagementId = resumeContext.DbManagementId,
            RequiredToolName = resumeContext.RequiredToolName,
            CustomToolId = resumeContext.CustomToolId,
            CustomToolRevisionId = resumeContext.CustomToolRevisionId,
            StatementCount = request.Statements.Count,
            TotalAffectedRows = request.TotalAffectedRows,
            ExternalReference = pendingResult.ExternalReference,
            Reason = pendingResult.Reason,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = durableUntil
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<DmlApprovalCompletionResult> CompleteAsync(
        DmlApprovalCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (string.IsNullOrWhiteSpace(completion.RequestId)
            || string.IsNullOrWhiteSpace(completion.ApprovalFingerprint)
            || completion.Decision == DmlApprovalDecision.Pending)
        {
            return new DmlApprovalCompletionResult(
                DmlApprovalCompletionStatus.InvalidApproval,
                "A durable DML completion must identify a request, preserve its approval fingerprint, and be Approved or Rejected.");
        }

        IAdminContext context;
        try
        {
            context = RequireAdminContext();
        }
        catch (InvalidOperationException ex)
        {
            return new DmlApprovalCompletionResult(DmlApprovalCompletionStatus.ConfigurationError, ex.Message);
        }

        var state = await context.DmlApprovalRequests
            .FirstOrDefaultAsync(x => x.RequestId == completion.RequestId, cancellationToken);
        if (state is null)
            return new DmlApprovalCompletionResult(DmlApprovalCompletionStatus.NotFound, "DML approval request was not found.");

        if (!FixedTimeEquals(state.ApprovalFingerprint, completion.ApprovalFingerprint))
            return new DmlApprovalCompletionResult(
                DmlApprovalCompletionStatus.InvalidApproval,
                "DML approval completion fingerprint does not match the persisted approval request.");

        if (!string.Equals(state.Status, Pending, StringComparison.Ordinal))
        {
            // Read legacy OutcomeUnknown records distinguished by Reason text,
            // but new Admin Store failures persist a dedicated machine state.
            var approvalStateUnknown = HasConfirmedTargetCommit(state);
            var commitOutcomeUnknown = string.Equals(state.Status, OutcomeUnknown, StringComparison.Ordinal)
                && !approvalStateUnknown;
            var rollbackOutcomeUnknown = string.Equals(state.Status, RollbackOutcomeUnknown, StringComparison.Ordinal);

            return new DmlApprovalCompletionResult(
                string.Equals(state.Status, Executing, StringComparison.Ordinal)
                    ? DmlApprovalCompletionStatus.AlreadyProcessing
                    : approvalStateUnknown || commitOutcomeUnknown || rollbackOutcomeUnknown
                        ? DmlApprovalCompletionStatus.OutcomeUnknown
                        : DmlApprovalCompletionStatus.AlreadyCompleted,
                approvalStateUnknown
                    ? ConfirmedCommitStateUnknownReason
                    : commitOutcomeUnknown
                        ? "DML commit outcome remains unknown. Approval is consumed; verify the database state. Do not retry this approval."
                        : rollbackOutcomeUnknown
                            ? "DML rollback could not be confirmed. Approval is consumed; inspect the target database. Do not retry this approval."
                        : string.Equals(state.Status, Executing, StringComparison.Ordinal)
                            ? "DML approval is already executing or requires reconciliation. Do not retry; inspect the target database state."
                            : $"DML approval request is already in terminal state '{state.Status}'.")
            {
                ErrorCode = approvalStateUnknown
                    ? ApprovalStateUnknownCode
                    : commitOutcomeUnknown ? "dml.commit_outcome_unknown"
                    : rollbackOutcomeUnknown ? "dml.rollback_outcome_unknown" : null
            };
        }

        if (DateTime.UtcNow >= state.ExpiresAt)
        {
            await MarkTerminalAsync(context, state, Expired, completion, "Durable DML approval expired before completion.", cancellationToken);
            return new DmlApprovalCompletionResult(DmlApprovalCompletionStatus.Expired, "Durable DML approval has expired.");
        }

        if (completion.Decision == DmlApprovalDecision.Rejected)
        {
            var reason = completion.Reason ?? "DML approval was rejected by the external approval system.";
            await MarkTerminalAsync(context, state, Rejected, completion, reason, cancellationToken);
            await AuditCompletionAsync(state, "cancelled", "declined", reason, null, cancellationToken);
            return new DmlApprovalCompletionResult(DmlApprovalCompletionStatus.Rejected, reason);
        }

        // Never interpret a future or absent Admin Store admission protocol
        // as v3 consent. An upgraded reader may support it later, but this
        // worker must not transition the Pending row into Executing.
        if (state.AdmissionProtocolVersion != AdminStoreAdmissionProtocolVersion)
            return new DmlApprovalCompletionResult(
                DmlApprovalCompletionStatus.ConfigurationError,
                "Durable DML approval admission protocol is incompatible with this worker. " +
                "Do not execute or replay this approval; use a compatible worker or re-preview.")
            {
                ErrorCode = "dml.admission_protocol_incompatible"
            };

        state.Status = Executing;
        state.ApproverIdentity = completion.ApproverIdentity;
        state.ExternalReference = completion.ExternalReference ?? state.ExternalReference;
        state.Reason = completion.Reason;
        state.UpdatedAt = DateTime.UtcNow;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new DmlApprovalCompletionResult(
                DmlApprovalCompletionStatus.AlreadyProcessing,
                "Another completion is already processing this DML approval request.");
        }

        try
        {
            var execution = await ExecuteApprovedAsync(context, state, cancellationToken);
            var terminalStatus = execution.Status switch
            {
                DmlApprovalCompletionStatus.Executed => Executed,
                DmlApprovalCompletionStatus.Stale => Stale,
                DmlApprovalCompletionStatus.ConfigurationError => Failed,
                _ => Failed
            };
            state.Status = terminalStatus;
            state.Reason = execution.Message;
            state.UpdatedAt = DateTime.UtcNow;
            if (execution.Status == DmlApprovalCompletionStatus.Executed)
            {
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (Exception persistenceFailure)
                {
                    // A confirmed target COMMIT precedes this separate Admin
                    // Store save. Never fall into the generic Failed handler:
                    // the primary operation was already physically committed.
                    return await HandleConfirmedCommitStateFailureAsync(
                        context, state, execution, persistenceFailure);
                }
            }
            else
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            // The target DB has already committed for Executed. Audit I/O is
            // secondary and must never rewrite a committed approval as Failed.
            // The terminal state was persisted before attempting this audit.
            try
            {
                using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await AuditCompletionAsync(
                    state,
                    execution.Status == DmlApprovalCompletionStatus.Executed ? "success" : "failed",
                    execution.Status == DmlApprovalCompletionStatus.Executed ? "approved" : terminalStatus.ToLowerInvariant(),
                    execution.Message,
                    execution.AffectedRows,
                    auditTimeout.Token);
            }
            catch (Exception)
            {
                // Preserve the persisted terminal result; audit transport
                // failures cannot undo an already committed target transaction.
            }
            return execution;
        }
        catch (DmlCommitOutcomeUnknownException ex)
        {
            // This is NOT a definitive failure. COMMIT may have succeeded.
            // Persist a separate terminal state, and never reopen Pending.
            // Failure to persist still leaves Executing (also non-retryable).
            state.Status = OutcomeUnknown;
            state.Reason = ex.Message;
            state.UpdatedAt = DateTime.UtcNow;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await context.SaveChangesAsync(cleanup.Token);
            }
            catch (Exception)
            {
                // The ambiguous physical commit must not be masked by audit DB I/O.
            }

            try
            {
                using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await AuditCompletionAsync(state, "outcome_unknown", "outcome_unknown",
                    ex.Message, null, auditTimeout.Token);
            }
            catch (Exception)
            {
                // The completion response is more important than a secondary audit failure.
            }

            return new DmlApprovalCompletionResult(
                DmlApprovalCompletionStatus.OutcomeUnknown, ex.Message)
            {
                ErrorCode = "dml.commit_outcome_unknown"
            };
        }
        catch (DmlRollbackOutcomeUnknownException ex)
        {
            // A mutation failed, then rollback could not be confirmed.
            // Treat this as target-state uncertainty, not definite Failed.
            state.Status = RollbackOutcomeUnknown;
            state.Reason = ex.Message;
            state.UpdatedAt = DateTime.UtcNow;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await context.SaveChangesAsync(cleanup.Token);
            }
            catch (Exception)
            {
                // The durably claimed Executing state is also non-replayable.
            }

            try
            {
                using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await AuditCompletionAsync(state, "outcome_unknown", "rollback_outcome_unknown",
                    ex.Message, null, auditTimeout.Token);
            }
            catch (Exception)
            {
                // Secondary audit failure never changes the target outcome.
            }

            return new DmlApprovalCompletionResult(DmlApprovalCompletionStatus.OutcomeUnknown, ex.Message)
            {
                ErrorCode = "dml.rollback_outcome_unknown"
            };
        }
        catch (DmlApprovalStaleException ex)
        {
            state.Status = Stale;
            state.Reason = ex.Message;
            state.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await AuditCompletionAsync(state, "failed", "stale", ex.Message, null, cancellationToken);
            return new DmlApprovalCompletionResult(DmlApprovalCompletionStatus.Stale, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Executing is claimed before touching the target database. Never auto-retry an ambiguous failure:
            // the target commit may have succeeded even if the Admin Store update did not.
            state.Status = Failed;
            state.Reason = ex.Message;
            state.UpdatedAt = DateTime.UtcNow;
            try { await context.SaveChangesAsync(cancellationToken); } catch { /* preserve at-most-once over status cosmetics */ }
            await AuditCompletionAsync(state, "failed", "failed", ex.Message, null, cancellationToken);
            return new DmlApprovalCompletionResult(
                DmlApprovalCompletionStatus.Failed,
                "Durable DML approval execution failed and will not be retried automatically: " + ex.Message);
        }
    }

    /// <summary>
    /// Acknowledged target COMMIT is durable evidence, independent of the
    /// Admin Store terminal-write acknowledgement. Older versions persisted
    /// these records as OutcomeUnknown with a known, exact reason prefix.
    /// Never allow reconciliation to contradict this confirmed COMMIT.
    /// </summary>
    internal static bool HasConfirmedTargetCommit(DmlApprovalRequestState state) =>
        HasConfirmedTargetCommit(state.Status, state.Reason);

    // Reconciliation inventory projects only these two status/evidence fields
    // rather than loading the encrypted SQL resume payload for every row.
    internal static bool HasConfirmedTargetCommit(string status, string? reason) =>
        string.Equals(status, ApprovalStateUnknown, StringComparison.Ordinal)
        || (string.Equals(status, OutcomeUnknown, StringComparison.Ordinal)
            && reason?.StartsWith(ConfirmedCommitStateUnknownReason, StringComparison.Ordinal) == true);

    private async Task<DmlApprovalCompletionResult> HandleConfirmedCommitStateFailureAsync(
        IAdminContext context,
        DmlApprovalRequestState state,
        DmlApprovalCompletionResult confirmedCommit,
        Exception persistenceFailure)
    {
        // A failed SaveChanges acknowledgement may mean the Admin Store
        // already persisted Executed; if not, Executing is durably claimed.
        // A best-effort separate write can mark ApprovalStateUnknown, but
        // must never make the request Pending, Failed, or executable again.
        state.Status = ApprovalStateUnknown;
        state.Reason = ConfirmedCommitStateUnknownReason;
        state.UpdatedAt = DateTime.UtcNow;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await context.SaveChangesAsync(cleanup.Token);
        }
        catch (Exception)
        {
            // At least the earlier Executing claim remains non-replayable.
            // Do not infer that the target database transaction rolled back.
        }

        try
        {
            using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await AuditCompletionAsync(
                state, "admin_state_unknown", "outcome_unknown",
                ConfirmedCommitStateUnknownReason + " Admin error: " +
                    persistenceFailure.GetType().Name,
                confirmedCommit.AffectedRows, auditTimeout.Token);
        }
        catch (Exception)
        {
            // Failure to record diagnostics must not hide the confirmed
            // physical write or imply that retrying the approval is safe.
        }

        return new DmlApprovalCompletionResult(
            DmlApprovalCompletionStatus.OutcomeUnknown,
            ConfirmedCommitStateUnknownReason,
            confirmedCommit.AffectedRows)
        {
            ErrorCode = ApprovalStateUnknownCode
        };
    }

    private async Task<DmlApprovalCompletionResult> ExecuteApprovedAsync(
        IAdminContext context,
        DmlApprovalRequestState state,
        CancellationToken cancellationToken)
    {
        var secret = RequireHmacSecret();
        var payloadJson = DurableDmlPayloadCiphertextReader.Decrypt(
            state.ProtectedExecutionPayload, state.RequestId,
            RequireCrypto(), secret, services.GetService<ISecretProtector>())
            ?? throw new InvalidOperationException("Could not decrypt durable DML approval payload.");
        var payload = DurableDmlResumePayloadCodec.Deserialize(payloadJson);
        if (payload.RequiresCommitWitness && !runtime.HasTargetCommitWitness)
            throw new InvalidOperationException(
                "The durable approval requires a target-enlisted COMMIT witness, but this runtime has no witness writer. " +
                "Do not downgrade this approval to a witness-free execution.");
        if (payload.FenceProtocol is not null &&
            (payload.FenceProtocol != TargetFenceProtocol
             || !runtime.HasTargetExecutionFence || !runtime.HasTargetCommitWitness))
            throw new InvalidOperationException(
                "Versioned target execution fencing is required by this approval but is unavailable. " +
                "Do not downgrade to an unfenced execution.");
        // A fence-enabled runtime must not execute a historical Pending request
        // which was created before admission fencing was enrolled. Treating a
        // missing protocol as permission to bypass admission would silently
        // downgrade precisely the fleet whose operator opted into fencing.
        // Upgrade rollout must drain/reject these requests and re-preview
        // with a fresh, versioned human authorization instead.
        if (runtime.HasTargetExecutionFence && payload.FenceProtocol != TargetFenceProtocol)
            throw new InvalidOperationException(
                "Legacy durable DML approval predates target execution fencing. " +
                "Request a new fenced preview and approval after draining older workers; " +
                "never execute the legacy approval without target admission.");

        var current = await ResolveCurrentExecutionAsync(context, state, payload, secret, cancellationToken);
        var parsedBatch = await runtime.ParseDmlBatchWithVerifiedRuntimeProfileAsync(
            current.Provider,
            current.ConnectionString,
            payload.Sql,
            payload.SourceDialect,
            cancellationToken);
        foreach (var statement in parsedBatch.Statements)
            TypedDmlRuntime.EnsureSupportedStatement(statement.Statement);

        var approvalContext = new DmlApprovalExecutionContext(
            state.RequesterIdentity,
            state.TargetIdentity,
            current.ProviderType,
            state.DatabaseIdentity);
        var previewPolicy = securityPolicyRuntimeState.GetCurrent();
        TypedDmlTransactionApprovalSession freshSession;
        await using (var lease = await concurrencyLimiter.TryAcquireAsync(cancellationToken))
        {
            if (lease is null)
                throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");
            freshSession = await runtime.PreviewTransactionAsync(
                current.Provider,
                current.ConnectionString,
                parsedBatch,
                previewPolicy,
                current.AllowedTables,
                approvalContext,
                cancellationToken);
        }

        var freshEvidence = DmlApprovalRequestFactory.ComputeEvidenceFingerprint(freshSession);
        if (!FixedTimeEquals(state.EvidenceFingerprint, freshEvidence))
            throw new DmlApprovalStaleException(
                "Approved DML evidence is stale because the current plan, row set, policy, or authorization context changed.");

        // Reload authorization and database configuration immediately before commit. Passing the latest
        // whitelist/policy into CommitTransactionAsync makes any change after preview fail closed.
        var commitCurrent = await ResolveCurrentExecutionAsync(context, state, payload, secret, cancellationToken);
        var currentPolicy = securityPolicyRuntimeState.GetCurrent();
        await using var commitLease = await concurrencyLimiter.TryAcquireAsync(cancellationToken);
        if (commitLease is null)
            throw new InvalidOperationException("Server busy: maximum concurrent SQL operations reached.");

        var commit = await runtime.CommitTransactionAsync(
            commitCurrent.Provider,
            commitCurrent.ConnectionString,
            freshSession,
            currentPolicy,
            commitCurrent.AllowedTables,
            approvalContext,
            cancellationToken,
            runtime.HasTargetCommitWitness
                ? new DmlTargetCommitWitnessEvidence(state.RequestId, state.EvidenceFingerprint)
                : null,
            requireExecutionFence: payload.FenceProtocol == TargetFenceProtocol);
        if (!commit.Committed)
            throw new DmlApprovalStaleException(
                "Approved DML could not be committed because commit-time evidence revalidation no longer matched.");

        return new DmlApprovalCompletionResult(
            DmlApprovalCompletionStatus.Executed,
            $"Approved DML committed atomically. statements={freshSession.Statements.Length}; affectedRows={commit.AffectedRows}.",
            commit.AffectedRows);
    }

    private async Task<CurrentExecution> ResolveCurrentExecutionAsync(
        IAdminContext context,
        DmlApprovalRequestState state,
        DurableDmlResumePayload payload,
        byte[] secret,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var key = await context.McpAccessKeys.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == state.AccessKeyId, cancellationToken)
            ?? throw new DmlApprovalStaleException("The MCP access key that requested this DML no longer exists.");
        if (!key.IsActive || key.RevokedAt.HasValue || (key.ExpiresAt.HasValue && key.ExpiresAt <= now))
            throw new DmlApprovalStaleException("The MCP access key that requested this DML is no longer active.");
        if (key.DbManagementId != state.DbManagementId)
            throw new DmlApprovalStaleException("The MCP access key is no longer bound to the approved database.");
        if (!IsToolAllowed(key.AllowedTools, state.RequiredToolName))
            throw new DmlApprovalStaleException("The MCP access key no longer has permission to use the approved DML tool.");

        var db = await context.DbManagement.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == state.DbManagementId, cancellationToken)
            ?? throw new DmlApprovalStaleException("The approved target database configuration no longer exists.");
        if (!Enum.TryParse<SqlAgentToolType>(db.SqlProvider, true, out var providerType)
            || !string.Equals(providerType.ToString(), state.DatabaseProvider, StringComparison.OrdinalIgnoreCase)
            || providerType != payload.SourceDialect)
            throw new DmlApprovalStaleException("The approved target database provider changed.");
        if (!string.Equals(db.Database?.Trim(), state.DatabaseIdentity, StringComparison.Ordinal))
            throw new DmlApprovalStaleException("The approved target database identity changed.");

        if (payload.CustomToolId.HasValue)
        {
            var tool = await context.CustomSqlTools.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == payload.CustomToolId.Value, cancellationToken)
                ?? throw new DmlApprovalStaleException("The Custom DML Tool used by this approval no longer exists.");
            if (!string.Equals(tool.Status, "Published", StringComparison.OrdinalIgnoreCase)
                || tool.PublishedRevisionId != payload.CustomToolRevisionId
                || tool.DbManagementId != state.DbManagementId)
                throw new DmlApprovalStaleException("The Custom DML Tool changed or was disabled after approval was requested.");
        }

        var password = DatabasePasswordCiphertextReader.Decrypt(
            db.PasswordHash, db.Id, RequireCrypto(), secret, services.GetService<ISecretProtector>());
        var connectionString = connectionStringFactory.BuildConnectionString(providerType, new BuildDbConnectionModelBase
        {
            Host = db.Host,
            Port = db.Port,
            Username = db.Username,
            Password = password,
            Database = db.Database,
            ExtraSettings = db.ExtraSettings
        });
        if (!FixedTimeEquals(payload.ConnectionFingerprint, ComputeConnectionFingerprint(connectionString, secret)))
            throw new DmlApprovalStaleException("The approved target database connection configuration changed.");

        return new CurrentExecution(
            providerType,
            sqlProviderFactory.GetProvider(providerType),
            connectionString,
            ParseWhitelist(key.TableWhitelist));
    }

    private async Task MarkTerminalAsync(
        IAdminContext context,
        DmlApprovalRequestState state,
        string status,
        DmlApprovalCompletion completion,
        string reason,
        CancellationToken cancellationToken)
    {
        state.Status = status;
        state.ApproverIdentity = completion.ApproverIdentity;
        state.ExternalReference = completion.ExternalReference ?? state.ExternalReference;
        state.Reason = reason;
        state.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task AuditCompletionAsync(
        DmlApprovalRequestState state,
        string result,
        string approvalStatus,
        string detail,
        int? affectedRows,
        CancellationToken cancellationToken)
    {
        var audit = services.GetService<IAuditService>();
        if (audit is null) return;
        await audit.WriteEventAsync(
            "dml.approval.completed",
            state.RequestId,
            result,
            new AuditEventContext
            {
                AccessKeyId = state.AccessKeyId,
                DbManagementId = state.DbManagementId,
                DatabaseName = state.DatabaseIdentity,
                ToolName = state.RequiredToolName,
                Operation = state.StatementCount > 1 ? "transaction" : "dml",
                AffectedRows = affectedRows ?? state.TotalAffectedRows,
                ApprovalStatus = approvalStatus,
                Definition = JsonSerializer.Serialize(new
                {
                    state.RequestId,
                    state.ExternalReference,
                    state.ApproverIdentity,
                    state.StatementCount
                })
            },
            detail,
            cancellationToken);
    }

    private IAdminContext RequireAdminContext() =>
        services.GetService<IAdminContext>()
        ?? throw new InvalidOperationException(
            "Durable DML approval requires AddHsSqlAgentAdminStore so pending requests can survive process restarts.");

    private ICryptoService RequireCrypto() =>
        services.GetService<ICryptoService>()
        ?? throw new InvalidOperationException("Durable DML approval requires ICryptoService.");

    private byte[] RequireHmacSecret()
    {
        var secret = services.GetService<IOptions<McpKeySettings>>()?.Value.HmacSecretKey;
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Durable DML approval requires the MCP HMAC secret to protect resume state.");
        return Encoding.UTF8.GetBytes(secret);
    }

    private static HashSet<string>? ParseWhitelist(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsToolAllowed(string? allowedTools, string requiredTool)
    {
        if (string.IsNullOrWhiteSpace(allowedTools)) return true;
        return allowedTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x, requiredTool, StringComparison.OrdinalIgnoreCase));
    }

    private static string ComputeConnectionFingerprint(string connectionString, byte[] secret)
    {
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(connectionString)));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed record CurrentExecution(
        SqlAgentToolType ProviderType,
        ISqlProvider Provider,
        string ConnectionString,
        IReadOnlySet<string>? AllowedTables);

    private sealed class DmlApprovalStaleException(string message) : InvalidOperationException(message);
}
