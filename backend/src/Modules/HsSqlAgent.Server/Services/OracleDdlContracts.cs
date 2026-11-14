using HsSqlAgent.SqlCore.Ddl;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Oracle 26ai PDB-only, own-schema, single CREATE TABLE. Oracle DDL commits
/// implicitly: an external ROLLBACK is not a recovery mechanism.
/// </summary>
public sealed class OracleDdlExecutionOptions
{
    public bool Enabled { get; init; }
    public bool AllowCreateTable { get; init; }
    public IReadOnlySet<string> AllowedSchemas { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> AllowedPdbNames { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public string PolicyRevision { get; init; } = "oracle26ai-ddl-policy-v1";
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Oracle CDB/PDB DBID/GUID, session principal and database role are included
/// in an opaque identity digest; no raw instance or personal data in MCP.
/// </summary>
public sealed record OracleDdlPhysicalSnapshot(
    string PhysicalIdentity,
    string PdbName,
    string SchemaName,
    string TableName,
    string PrincipalIdentity,
    bool ObjectExists,
    IReadOnlyList<DdlPhysicalColumn> Columns,
    string Fingerprint);

public sealed record OracleDdlRuntimePreview(
    DdlSchemaPreview StaticPreview,
    OracleDdlPhysicalSnapshot Before,
    IReadOnlyList<DdlPhysicalColumn> ExpectedColumns,
    string ApprovalFingerprint,
    DateTimeOffset ExpiresAt,
    string RequestId,
    string HumanReadableDiff);

public sealed record OracleDdlRuntimeResult(
    DdlRuntimeStatus Status,
    OracleDdlRuntimePreview? Preview,
    string Message,
    string? ErrorCode = null)
{
    public bool CommitConfirmed => Status == DdlRuntimeStatus.Committed;
}
