using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Pipeline;

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
    ImmutableArray<ImmutableDictionary<string, object?>> InsertRows = default,
    SqlSchemaSnapshot? SchemaSnapshot = null)
{
    /// <summary>
    /// An optional live, provider-minted SQL Server INT source-key proof. A
    /// plan carrying this proof must be re-attested on the actual commit
    /// connection and transaction. It does not authorize unproven CTE DML.
    /// </summary>
    public SqlServerCteUniqueKeyAssurance? SqlServerCteUniqueKeyAssurance { get; init; }
}

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
    string ApprovalContextFingerprint,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Nonce)
{
    /// <summary>
    /// Canonical binding of the complete approved plan, including match SQL,
    /// row identity and preview payload. Legacy challenges lacking this field
    /// are no longer executable and must be previewed again.
    /// </summary>
    public string? ApprovalPlanBinding { get; init; }
}

public sealed record DmlCommitResult(
    bool Committed,
    int AffectedRows,
    string Message)
{
    /// <summary>
    /// Rows produced by an approved DML result clause. Empty for ordinary mutations and failed or
    /// cancelled commits. Each returned row corresponds to one directly affected database row.
    /// </summary>
    public ImmutableArray<IReadOnlyDictionary<string, object?>> ReturnedRows { get; init; } =
        ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty;
}

public interface IDmlCoordinator
{
    Task<DmlPreview> PreviewAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        string approvalContextFingerprint,
        CancellationToken cancellationToken,
        string expectedServerVersionIdentity);

    Task<DmlCommitResult> CommitAsync(
        string connectionString,
        ValidatedDmlPlan plan,
        DmlApprovalChallenge approvedChallenge,
        string approvalContextFingerprint,
        CancellationToken cancellationToken,
        string expectedServerVersionIdentity);
}
