namespace HsSqlAgent.Approvals;

/// <summary>
/// Host-selected approval integration for DML execution. Implementations decide how a human or
/// external workflow grants approval; HsSqlAgent retains ownership of SQL validation, evidence
/// binding, commit-time revalidation, and transaction execution.
/// </summary>
public interface IDmlApprovalProvider
{
    ValueTask<DmlApprovalResult> RequestApprovalAsync(
        DmlApprovalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Completion endpoint used by asynchronous approval integrations after a provider previously
/// returned Pending. Implementations never receive SQL execution primitives; HsSqlAgent reloads
/// the durable request, rebuilds current evidence, and owns any eventual commit.
/// </summary>
public interface IDmlApprovalCompletionSink
{
    ValueTask<DmlApprovalCompletionResult> CompleteAsync(
        DmlApprovalCompletion completion,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Transport-neutral, execution-primitive-free description of the exact DML evidence being
/// presented for approval. ExpiresAt is the short-lived synchronous execution challenge deadline;
/// DurableUntil is the latest time a Pending request may be completed asynchronously.
/// </summary>
public sealed record DmlApprovalRequest(
    string RequestId,
    string Title,
    string RequesterIdentity,
    string TargetIdentity,
    string DatabaseProvider,
    string DatabaseIdentity,
    IReadOnlyList<DmlApprovalStatement> Statements,
    int TotalAffectedRows,
    string ApprovalFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DurableUntil = null)
{
    public bool IsTransaction => Statements.Count > 1;
}

public sealed record DmlApprovalStatement(
    int Index,
    string Operation,
    string TableName,
    int AffectedRows,
    string PreviewJson);

public enum DmlApprovalDecision
{
    Approved,
    Rejected,
    Pending
}

/// <summary>
/// Decision returned by an approval provider. Approved decisions must preserve the approval
/// fingerprint supplied in the request; HsSqlAgent rejects mismatched fingerprints before commit.
/// Pending creates a durable approval request when the host has the Admin Store capability.
/// </summary>
public sealed record DmlApprovalResult(
    DmlApprovalDecision Decision,
    string ApprovalFingerprint,
    string? ApproverIdentity = null,
    string? ExternalReference = null,
    string? Reason = null)
{
    public static DmlApprovalResult Approve(
        DmlApprovalRequest request,
        string? approverIdentity = null,
        string? externalReference = null) =>
        new(DmlApprovalDecision.Approved, request.ApprovalFingerprint, approverIdentity, externalReference);

    public static DmlApprovalResult Reject(
        DmlApprovalRequest request,
        string? reason = null,
        string? approverIdentity = null,
        string? externalReference = null) =>
        new(DmlApprovalDecision.Rejected, request.ApprovalFingerprint, approverIdentity, externalReference, reason);

    public static DmlApprovalResult Pending(
        DmlApprovalRequest request,
        string externalReference,
        string? reason = null) =>
        new(
            DmlApprovalDecision.Pending,
            request.ApprovalFingerprint,
            ExternalReference: externalReference,
            Reason: reason);
}

/// <summary>
/// Final decision delivered by an external approval adapter for a previously-pending request.
/// Pending is not a valid completion decision.
/// </summary>
public sealed record DmlApprovalCompletion(
    string RequestId,
    DmlApprovalDecision Decision,
    string ApprovalFingerprint,
    string? ApproverIdentity = null,
    string? ExternalReference = null,
    string? Reason = null)
{
    public static DmlApprovalCompletion Approve(
        string requestId,
        string approvalFingerprint,
        string? approverIdentity = null,
        string? externalReference = null) =>
        new(requestId, DmlApprovalDecision.Approved, approvalFingerprint, approverIdentity, externalReference);

    public static DmlApprovalCompletion Reject(
        string requestId,
        string approvalFingerprint,
        string? reason = null,
        string? approverIdentity = null,
        string? externalReference = null) =>
        new(
            requestId,
            DmlApprovalDecision.Rejected,
            approvalFingerprint,
            approverIdentity,
            externalReference,
            reason);
}

public enum DmlApprovalCompletionStatus
{
    Executed,
    Rejected,
    Stale,
    Expired,
    NotFound,
    AlreadyCompleted,
    AlreadyProcessing,
    InvalidApproval,
    ConfigurationError,
    Failed
}

public sealed record DmlApprovalCompletionResult(
    DmlApprovalCompletionStatus Status,
    string Message,
    int? AffectedRows = null);
