using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Admin.Service.Interfaces;
using Admin.Service.Models;
using Admin.Service.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Human-supervised bookkeeping only. This service NEVER opens a target database,
/// recompiles SQL, reissues approval tokens or executes a DML statement.
/// The disposition is an operator attestation, not an independent commit proof.
/// </summary>
public sealed class DurableDmlReconciliationService(
    AdminContext adminContext,
    IOptions<OperabilitySettings>? operabilitySettings = null,
    IOutboundDeliverySignal? deliverySignal = null)
{
    private const string Executing = "Executing";
    private const string OutcomeUnknown = "OutcomeUnknown";
    private const string ApprovalStateUnknown = "ApprovalStateUnknown";

    public async Task<IReadOnlyList<DurableDmlReconciliationItem>> ListAsync(
        int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");

        // Project only the bounded operational fields and the legacy reason
        // needed for commit-evidence classification. Never load or return the
        // encrypted SQL resume payload, approval token, or fingerprints.
        var states = await adminContext.DmlApprovalRequests.AsNoTracking()
            .Where(x => x.Status == Executing || x.Status == OutcomeUnknown
                        || x.Status == ApprovalStateUnknown)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.RequestId)
            .Take(limit)
            .Select(x => new
            {
                x.RequestId, x.Status, x.Reason, x.UpdatedAt, x.CreatedAt,
                x.DbManagementId, x.DatabaseProvider, x.DatabaseIdentity,
                x.StatementCount
            })
            .ToListAsync(cancellationToken);

        return states.Select(x => CreateItem(
            x.RequestId, x.Status, x.Reason, x.UpdatedAt, x.CreatedAt,
            x.DbManagementId, x.DatabaseProvider, x.DatabaseIdentity,
            x.StatementCount)).ToArray();
    }

    public async Task<DurableDmlReconciliationItem?> ReconcileAsync(
        string requestId,
        DurableDmlReconciliationRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 64)
            throw new ArgumentException("A valid approval request ID is required.", nameof(requestId));
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 64)
            throw new ArgumentException("A valid human operator identity is required.", nameof(actor));
        if (string.IsNullOrWhiteSpace(request.EvidenceReference) || request.EvidenceReference.Length > 256
            || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 700)
            throw new ArgumentException("An evidence reference (up to 256 characters) and a note (up to 700 characters) are required.");
        if (request.ExpectedUpdatedAt == default)
            throw new ArgumentException("ExpectedUpdatedAt is required.");
        if (request.ExpectedUpdatedAt.Kind == DateTimeKind.Unspecified)
            throw new ArgumentException(
                "ExpectedUpdatedAt must carry an explicit UTC marker or offset, not a timezone-ambiguous local timestamp.");
        if (request.ExpectedStatus is not (OutcomeUnknown or ApprovalStateUnknown))
            throw new ArgumentException("Only OutcomeUnknown or ApprovalStateUnknown approvals may be resolved. Executing requires independently proven worker quiescence.");

        // These are *operator-attested* terminal dispositions; none can be replayed.
        var destination = request.Resolution switch
        {
            "committed" => "AttestedCommitted",
            "not_committed" => "AttestedNotCommitted",
            "indeterminate" => "AttestedIndeterminate",
            _ => throw new ArgumentException("Resolution must be committed, not_committed or indeterminate.")
        };

        var state = await adminContext.DmlApprovalRequests
            .SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);
        if (state is null) return null;
        var expectedUpdatedAtUtc = request.ExpectedUpdatedAt.ToUniversalTime();
        if (state.Status != request.ExpectedStatus || state.UpdatedAt != expectedUpdatedAtUtc)
            throw new InvalidOperationException("Approval state changed since inspection; reload before reconciling.");
        // Age does not fence a live worker. Never override Executing while the
        // original SQL transaction might still be in flight.
        if (state.Status is not (OutcomeUnknown or ApprovalStateUnknown))
            throw new InvalidOperationException("Only terminal uncertain approvals can be reconciled; Executing requires a separate worker-fencing proof.");

        // An ApprovalStateUnknown state (or its legacy OutcomeUnknown encoding)
        // means that the target database COMMIT was positively acknowledged.
        // A human attestation must not contradict that historical fact.
        if (DurableDmlApprovalLifecycle.HasConfirmedTargetCommit(state)
            && request.Resolution != "committed")
            throw new InvalidOperationException(
                "The target database COMMIT is already confirmed. This approval cannot be attested as not committed or indeterminate.");

        var originalStatus = state.Status;
        var now = DateTime.UtcNow;
        // The existing 2,000-character Reason field retains the operator's
        // attestation even if external audit consumers are temporarily down.
        var explanation = JsonSerializer.Serialize(new
        {
            resolution = request.Resolution,
            actor,
            evidenceReference = AuditTextRedactor.Redact(request.EvidenceReference.Trim()),
            note = AuditTextRedactor.Redact(request.Note.Trim()),
            previousStatus = originalStatus,
            reconciledAt = now
        });
        if (explanation.Length > 2000)
            throw new ArgumentException("The reconciliation attestation is too long.");

        state.Status = destination;
        state.Reason = explanation;
        state.UpdatedAt = now;
        var audit = new AuditLog
        {
            EventId = Guid.NewGuid(),
            Action = "dml.approval.reconciled",
            Target = state.RequestId,
            Result = "reconciled",
            ActorType = "admin",
            ActorId = actor,
            DbManagementId = state.DbManagementId,
            AccessKeyId = state.AccessKeyId,
            DatabaseName = state.DatabaseIdentity,
            ToolName = state.RequiredToolName,
            Operation = state.StatementCount > 1 ? "transaction" : "dml",
            ApprovalStatus = destination,
            Detail = explanation,
            CreatedAt = now
        };
        adminContext.AuditLogs.Add(audit);

        // Match AuditService's durable SIEM outbox contract. Both the audit
        // event and its delivery request must be committed with the approval
        // transition, not sent directly over the network during the request.
        var siemUrl = operabilitySettings?.Value.SiemWebhookUrl;
        if (!string.IsNullOrWhiteSpace(siemUrl))
        {
            adminContext.OutboundDeliveries.Add(new OutboundDelivery
            {
                Category = "siem",
                DedupeKey = $"siem:{audit.EventId:N}",
                TargetUrl = siemUrl,
                Payload = JsonSerializer.Serialize(audit),
                Status = "pending",
                CreatedAt = now,
                NextAttemptAt = now
            });
        }

        // Status is an EF concurrency token. The state change, audit and SIEM
        // outbox entry are one relational transaction; a stale writer rolls
        // all three back.
        await adminContext.SaveChangesAsync(cancellationToken);
        // Notification is an acceleration hint, not a second transaction.
        // Once SaveChanges succeeded it must not be reported as failed
        // because a process-local signal is unavailable.
        try { deliverySignal?.Notify(); }
        catch (Exception) { /* Durable pending outbox remains available for polling. */ }
        return CreateItem(
            state.RequestId, state.Status, state.Reason, state.UpdatedAt,
            state.CreatedAt, state.DbManagementId, state.DatabaseProvider,
            state.DatabaseIdentity, state.StatementCount);
    }

    private static DurableDmlReconciliationItem CreateItem(
        string requestId, string status, string? reason,
        DateTime updatedAt, DateTime createdAt, int dbManagementId,
        string databaseProvider, string databaseIdentity, int statementCount)
    {
        // This metadata describes what the server can actually prove, not
        // what an operator might conclude after investigating target effects.
        // Reconciled terminal states are permanently consumed and expose no
        // additional actions. Keep the decision policy here and reuse it
        // for both inventory and mutation responses.
        var knownCommit = DurableDmlApprovalLifecycle.HasConfirmedTargetCommit(status, reason);
        var evidence = knownCommit ? "TargetCommitAcknowledged"
            : status == Executing ? "WorkerFencingRequired"
            : status == OutcomeUnknown ? "PhysicalCommitUnknown"
            : "TerminalAttestation";
        string[] resolutions = status == Executing
            ? []
            : status is OutcomeUnknown or ApprovalStateUnknown
                ? knownCommit ? ["committed"] : ["committed", "not_committed", "indeterminate"]
                : [];
        return new DurableDmlReconciliationItem(
            requestId, status, AsUtc(updatedAt), AsUtc(createdAt),
            dbManagementId, databaseProvider, databaseIdentity, statementCount,
            evidence, resolutions);
    }

    // Approval timestamps have always been written as UTC. SQLite can read
    // them back with DateTimeKind.Unspecified, which would produce a JSON
    // timestamp without 'Z'. Preserve the stored ticks and restore the UTC
    // contract for API round trips; never reinterpret ticks in local time.
    private static DateTime AsUtc(DateTime timestamp) =>
        DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
}

public sealed record DurableDmlReconciliationItem(
    string RequestId,
    string Status,
    DateTime UpdatedAt,
    DateTime CreatedAt,
    int DbManagementId,
    string DatabaseProvider,
    string DatabaseIdentity,
    int StatementCount,
    string CommitEvidence,
    IReadOnlyList<string> AllowedResolutions);

public sealed record DurableDmlReconciliationRequest(
    string ExpectedStatus,
    DateTime ExpectedUpdatedAt,
    string Resolution,
    string EvidenceReference,
    string Note);
