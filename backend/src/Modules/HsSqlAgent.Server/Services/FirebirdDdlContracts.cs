using HsSqlAgent.SqlCore.Ddl;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// Explicit Firebird 5 CREATE TABLE-only Host opt-in. A GUID allowlist is mandatory:
/// the Admin DB label is not a physical Firebird database identity.
/// </summary>
public sealed class FirebirdDdlExecutionOptions
{
    public bool Enabled { get; init; }
    public bool AllowCreateTable { get; init; }
    public IReadOnlySet<string> AllowedDatabaseGuids { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public string PolicyRevision { get; init; } = "firebird-ddl-policy-v1";
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The physical Firebird GUID/file/creation identity is hashed; filesystem paths,
/// connection credentials and raw system metadata are never sent to MCP clients.
/// </summary>
public sealed record FirebirdDdlPhysicalSnapshot(
    string DatabaseGuid,
    string PhysicalIdentity,
    string PrincipalIdentity,
    string TableName,
    bool TableExists,
    IReadOnlyList<DdlPhysicalColumn> Columns,
    string Fingerprint);

public sealed record FirebirdDdlRuntimePreview(
    DdlSchemaPreview StaticPreview,
    FirebirdDdlPhysicalSnapshot Before,
    IReadOnlyList<DdlPhysicalColumn> ExpectedColumns,
    string ApprovalFingerprint,
    DateTimeOffset ExpiresAt,
    string RequestId,
    string HumanReadableDiff);

public sealed record FirebirdDdlRuntimeResult(
    DdlRuntimeStatus Status,
    FirebirdDdlRuntimePreview? Preview,
    string Message,
    string? ErrorCode = null)
{
    public bool CommitConfirmed => Status == DdlRuntimeStatus.Committed;
}
