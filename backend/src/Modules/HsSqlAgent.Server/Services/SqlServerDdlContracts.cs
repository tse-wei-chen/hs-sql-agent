using HsSqlAgent.SqlCore.Ddl;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Explicit SQL Server 2022 DDL opt-in. The first proven executable subset is
/// a single schema-qualified CREATE TABLE with bounded simple INT columns.
/// </summary>
public sealed class SqlServerDdlExecutionOptions
{
    public bool Enabled { get; init; }
    public IReadOnlySet<string> AllowedSchemas { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool AllowCreateTable { get; init; }
    public string PolicyRevision { get; init; } = "sqlserver-ddl-policy-v1";
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// SQL Server catalog identities are DB_ID / schema_id / object_id, not
/// PostgreSQL OIDs or SQLite schema cookies. Identity is opaque in MCP output.
/// </summary>
public sealed record SqlServerDdlPhysicalSnapshot(
    string ServerIdentity,
    string DatabaseName,
    int DatabaseId,
    int SchemaId,
    string SchemaName,
    string TableName,
    int? ObjectId,
    IReadOnlyList<DdlPhysicalColumn> Columns,
    string Fingerprint);

public sealed record SqlServerDdlRuntimePreview(
    DdlSchemaPreview StaticPreview,
    SqlServerDdlPhysicalSnapshot Before,
    IReadOnlyList<DdlPhysicalColumn> ExpectedColumns,
    string ApprovalFingerprint,
    DateTimeOffset ExpiresAt,
    string RequestId,
    string HumanReadableDiff);

public sealed record SqlServerDdlRuntimeResult(
    DdlRuntimeStatus Status,
    SqlServerDdlRuntimePreview? Preview,
    string Message,
    string? ErrorCode = null)
{
    public bool CommitConfirmed => Status == DdlRuntimeStatus.Committed;
}
