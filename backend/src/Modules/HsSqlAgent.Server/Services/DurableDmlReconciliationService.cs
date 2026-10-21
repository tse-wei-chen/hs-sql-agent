using System.Text.Json;
using Admin.Service.Data;
using Admin.Service.Data.Entites;
using Microsoft.EntityFrameworkCore;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Human-supervised bookkeeping only. This service NEVER opens a target database,
/// recompiles SQL, reissues approval tokens or executes a DML statement.
/// The disposition is an operator attestation, not an independent commit proof.
/// </summary>
public sealed class DurableDmlReconciliationService(AdminContext adminContext)
{
    private const string Executing = "Executing";
    private const string OutcomeUnknown = "OutcomeUnknown";
    private const string ApprovalStateUnknown = "ApprovalStateUnknown";

    public async Task<IReadOnlyList<DurableDmlReconciliationItem>> ListAsync(
        int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");

        // Do not expose ProtectedExecutionPayload, SQL text, connection secrets or fingerprints.
        return await adminContext.DmlApprovalRequests.AsNoTracking()
            .Where(x => x.Status == Executing || x.Status == OutcomeUnknown
                        || x.Status == ApprovalStateUnknown)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(limit)
            .Select(x => new DurableDmlReconciliationItem(
                x.RequestId, x.Status, x.UpdatedAt, x.CreatedAt, x.DbManagementId,
                x.DatabaseProvider, x.DatabaseIdentity, x.StatementCount))
            .ToListAsync(cancellationToken);
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
        if (request.ExpectedStatus is not (OutcomeUnknown or ApprovalStateUnknown))
            throw new ArgumentException("Only OutcomeUnknown or ApprovalStateUnknown approvals may be resolved. Executing requires independently proven worker quiescence.");

        // These are *operator-attested* terminal dispositions; none can be replayed.
        var destination = request.Resolution switch
        {
            "committed" => "ReconciledCommitted",
            "not_committed" => "ReconciledNotCommitted",
            "indeterminate" => "ReconciledIndeterminate",
            _ => throw new ArgumentException("Resolution must be committed, not_committed or indeterminate.")
        };

        var state = await adminContext.DmlApprovalRequests
            .SingleOrDefaultAsync(x => x.RequestId == requestId, cancellationToken);
        if (state is null) return null;
        if (state.Status != request.ExpectedStatus || state.UpdatedAt != request.ExpectedUpdatedAt)
            throw new InvalidOperationException("Approval state changed since inspection; reload before reconciling.");
        // Age does not fence a live worker. Never override Executing while the
        // original SQL transaction might still be in flight.
        if (state.Status is not (OutcomeUnknown or ApprovalStateUnknown))
            throw new InvalidOperationException("Only terminal uncertain approvals can be reconciled; Executing requires a separate worker-fencing proof.");

        var originalStatus = state.Status;
        var now = DateTime.UtcNow;
        // The existing 2,000-character Reason field retains the operator's
        // attestation even if external audit consumers are temporarily down.
        var explanation = JsonSerializer.Serialize(new
        {
            resolution = request.Resolution,
            actor,
            evidenceReference = request.EvidenceReference,
            note = request.Note,
            previousStatus = originalStatus,
            reconciledAt = now
        });
        if (explanation.Length > 2000)
            throw new ArgumentException("The reconciliation attestation is too long.");

        state.Status = destination;
        state.Reason = explanation;
        state.UpdatedAt = now;
        adminContext.AuditLogs.Add(new AuditLog
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
        });

        // Status is an EF concurrency token. Audit insert and transition are
        // committed in a single SaveChanges transaction; any stale writer
        // rolls back both, including the audit insert.
        await adminContext.SaveChangesAsync(cancellationToken);
        return new DurableDmlReconciliationItem(
            state.RequestId, state.Status, state.UpdatedAt, state.CreatedAt,
            state.DbManagementId, state.DatabaseProvider, state.DatabaseIdentity,
            state.StatementCount);
    }
}

public sealed record DurableDmlReconciliationItem(
    string RequestId,
    string Status,
    DateTime UpdatedAt,
    DateTime CreatedAt,
    int DbManagementId,
    string DatabaseProvider,
    string DatabaseIdentity,
    int StatementCount);

public sealed record DurableDmlReconciliationRequest(
    string ExpectedStatus,
    DateTime ExpectedUpdatedAt,
    string Resolution,
    string EvidenceReference,
    string Note);
