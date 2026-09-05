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
/// Transport-neutral, execution-primitive-free description of the exact DML evidence being
/// presented for approval.
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
    DateTimeOffset ExpiresAt)
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
/// Pending is reserved for integrations that create an external approval request. The current
/// server returns pending without committing; durable resume/completion is a separate lifecycle.
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
        new(
            DmlApprovalDecision.Approved,
            request.ApprovalFingerprint,
            approverIdentity,
            externalReference);

    public static DmlApprovalResult Reject(
        DmlApprovalRequest request,
        string? reason = null,
        string? approverIdentity = null,
        string? externalReference = null) =>
        new(
            DmlApprovalDecision.Rejected,
            request.ApprovalFingerprint,
            approverIdentity,
            externalReference,
            reason);

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
