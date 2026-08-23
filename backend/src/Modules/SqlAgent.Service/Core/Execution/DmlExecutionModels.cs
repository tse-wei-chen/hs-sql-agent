using System.Collections.Immutable;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Execution;

public enum DmlRowIdentityAssurance
{
    Strict,
    CountOnly
}

/// <summary>
/// Describes what must be revalidated between preview and commit. UPDATE/DELETE approvals bind to
/// an existing row set. INSERT VALUES approvals bind to the exact compiled payload instead because
/// there is no pre-existing target row set to fingerprint.
/// </summary>
public enum DmlApprovalMode
{
    RowSetMutation,
    InsertValues
}

public sealed record ValidatedDmlPlan(
    DmlOperation Operation,
    string TableName,
    CompiledSqlCommand MutationCommand,
    CompiledSqlCommand? MatchQueryCommand,
    ImmutableArray<string> RowIdentityColumns,
    DmlRowIdentityAssurance RowIdentityAssurance,
    string PlanFingerprint,
    string PolicyVersion,
    TimeSpan ApprovalTtl,
    int MaxAffectedRows = 0,
    DmlApprovalMode ApprovalMode = DmlApprovalMode.RowSetMutation,
    ImmutableArray<ImmutableDictionary<string, object?>> InsertRows = default);

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
        ValidatedDmlPlan plan,
        CancellationToken cancellationToken = default);

    Task<DmlCommitResult> CommitAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        CancellationToken cancellationToken = default);
}
