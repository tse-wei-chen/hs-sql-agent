using System.Collections.ObjectModel;
using HsSqlAgent.SqlCore.Ddl;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Never enabled implicitly by installing HsSqlAgent.Server or selecting DML.
/// Registered explicitly via AddHsSqlAgentPostgresDdl, with an allowlist and
/// operation-by-operation opt-in. The options object is a startup configuration.
/// </summary>
public sealed class PostgresDdlExecutionOptions
{
    public bool Enabled { get; init; }
    public IReadOnlySet<string> AllowedSchemas { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public bool AllowCreateTable { get; init; }
    public bool AllowAddColumn { get; init; }
    public bool AllowCreateIndex { get; init; }
    // Separate high-risk opt-in. Never inherited from additive DDL or DML grants.
    public bool AllowDropEmptyTable { get; init; }
    // Tiered key authorization: these are authenticated MCP principal IDs,
    // e.g. mcp-key:123, not caller-supplied SQL identities.
    public IReadOnlySet<string> AllowedDropPrincipals { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public string PolicyRevision { get; init; } = "ddl-policy-v1";
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed record DdlActorContext(
    string PrincipalIdentity,
    string TargetIdentity,
    string DatabaseName);

public enum DdlRuntimeStatus
{
    Preview,
    Committed,
    Rejected,
    Pending,
    Failed,
    OutcomeUnknown
}

public sealed record DdlPhysicalColumn(string Name, string TypeName, bool Nullable);

public sealed record DdlPhysicalIndex(
    string Name, IReadOnlyList<string> Columns,
    bool Unique, bool Partial, bool Expression);

/// <summary>
/// Immutable observational physical metadata, captured from a PostgreSQL
/// connection, not passed to the core as a fabricated Known virtual Catalog.
/// TableOid prevents accidental reuse of an identity after DROP+recreate.
/// </summary>
public sealed record DdlPhysicalSnapshot(
    string ServerIdentity,
    string DatabaseName,
    string SchemaName,
    long? SchemaOid,
    string TableName,
    long? TableOid,
    IReadOnlyList<DdlPhysicalColumn> Columns,
    IReadOnlyList<DdlPhysicalIndex> Indexes,
    string Fingerprint);

public sealed record DdlRuntimePreview(
    DdlSchemaPreview StaticPreview,
    DdlPhysicalSnapshot Before,
    IReadOnlyList<DdlPhysicalColumn> ExpectedColumns,
    IReadOnlyList<DdlPhysicalIndex> ExpectedIndexes,
    string ApprovalFingerprint,
    DateTimeOffset ExpiresAt,
    string RequestId,
    string HumanReadableDiff);

public sealed record DdlRuntimeResult(
    DdlRuntimeStatus Status,
    DdlRuntimePreview? Preview,
    string Message,
    string? ErrorCode = null)
{
    public bool CommitConfirmed => Status == DdlRuntimeStatus.Committed;
    public bool IsOutcomeUnknown => Status == DdlRuntimeStatus.OutcomeUnknown;
}

/// <summary>
/// DDL approval is wholly separate from DML row-set approval. No DML
/// approval result/nonce can authorize a schema change.
/// </summary>
public interface IDdlApprovalProvider
{
    ValueTask<DdlApprovalDecision> RequestAsync(
        DdlApprovalRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record DdlApprovalRequest(
    string RequestId,
    string ApprovalFingerprint,
    string PlanFingerprint,
    string PrincipalIdentity,
    string TargetIdentity,
    string DatabaseIdentity,
    string PolicyRevision,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string HumanReadableDiff);

public enum DdlApprovalState
{
    Approved,
    Rejected,
    Pending
}

public sealed record DdlApprovalDecision(
    DdlApprovalState State,
    string RequestId,
    string ApprovalFingerprint,
    string? ApproverIdentity = null);
