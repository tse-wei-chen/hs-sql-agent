using System.Collections.Immutable;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Execution;

public sealed record DmlPreview(
    DmlOperation Operation,
    string TableName,
    int AffectedRows,
    ImmutableArray<IReadOnlyDictionary<string, object?>> Rows,
    DmlApprovalChallenge Challenge);

public sealed record DmlApprovalChallenge(
    string PlanFingerprint,
    string? RowSetFingerprint,
    int AffectedRows,
    string PolicyVersion,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Nonce);

public sealed record DmlCommitResult(
    bool Committed,
    int AffectedRows,
    string Message);

public interface IDmlCoordinator
{
    Task<DmlPreview> PreviewAsync(
        string connectionString,
        object validatedPlan,
        CancellationToken cancellationToken = default);

    Task<DmlCommitResult> CommitAsync(
        string connectionString,
        object validatedPlan,
        DmlApprovalChallenge approvedChallenge,
        CancellationToken cancellationToken = default);
}
