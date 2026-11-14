using HsSqlAgent.SqlCore.Ddl;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Independent SQLite file-backed DDL gate. SQLite's schema_version is a
/// schema cookie, not a PostgreSQL OID or live authorization token.
/// Nothing is enabled by the ordinary MCP/DML hosting registration.
/// </summary>
public sealed class SqliteDdlExecutionOptions
{
    public bool Enabled { get; init; }
    public bool AllowCreateTable { get; init; }
    public bool AllowAddColumn { get; init; }
    public bool AllowCreateIndex { get; init; }
    public string PolicyRevision { get; init; } = "sqlite-ddl-policy-v1";
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed record SqliteDdlPhysicalSnapshot(
    string DatabaseIdentity,
    string SchemaName,
    long SchemaVersion,
    string TableName,
    string? TableDefinition,
    IReadOnlyList<DdlPhysicalColumn> Columns,
    IReadOnlyList<DdlPhysicalIndex> Indexes,
    string Fingerprint);

public sealed record SqliteDdlRuntimePreview(
    DdlSchemaPreview StaticPreview,
    SqliteDdlPhysicalSnapshot Before,
    IReadOnlyList<DdlPhysicalColumn> ExpectedColumns,
    IReadOnlyList<DdlPhysicalIndex> ExpectedIndexes,
    string ApprovalFingerprint,
    DateTimeOffset ExpiresAt,
    string RequestId,
    string HumanReadableDiff);

public sealed record SqliteDdlRuntimeResult(
    DdlRuntimeStatus Status,
    SqliteDdlRuntimePreview? Preview,
    string Message,
    string? ErrorCode = null)
{
    public bool CommitConfirmed => Status == DdlRuntimeStatus.Committed;
    public bool IsOutcomeUnknown => Status == DdlRuntimeStatus.OutcomeUnknown;
}
