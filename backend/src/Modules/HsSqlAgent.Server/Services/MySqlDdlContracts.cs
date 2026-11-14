using HsSqlAgent.SqlCore.Ddl;

namespace HsSqlAgent.Server.Services;

/// <summary>
/// MySQL 8.4 LTS single CREATE TABLE opt-in. Atomic InnoDB DDL is an
/// implicit-commit statement, not rollbackable by user transaction control.
/// </summary>
public sealed class MySqlDdlExecutionOptions
{
    public bool Enabled { get; init; }
    public bool AllowCreateTable { get; init; }
    public IReadOnlySet<string> AllowedDatabases { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public string PolicyRevision { get; init; } = "mysql84-ddl-policy-v1";
    public TimeSpan ApprovalLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>All raw server identifiers and principals are hashed for MCP output.</summary>
public sealed record MySqlDdlPhysicalSnapshot(
    string ServerIdentity,
    string DatabaseName,
    string TableName,
    string PrincipalIdentity,
    bool TableExists,
    string? StorageEngine,
    IReadOnlyList<DdlPhysicalColumn> Columns,
    string Fingerprint);

public sealed record MySqlDdlRuntimePreview(
    DdlSchemaPreview StaticPreview,
    MySqlDdlPhysicalSnapshot Before,
    IReadOnlyList<DdlPhysicalColumn> ExpectedColumns,
    string ApprovalFingerprint,
    DateTimeOffset ExpiresAt,
    string RequestId,
    string HumanReadableDiff);

public sealed record MySqlDdlRuntimeResult(
    DdlRuntimeStatus Status,
    MySqlDdlRuntimePreview? Preview,
    string Message,
    string? ErrorCode = null)
{
    public bool CommitConfirmed => Status == DdlRuntimeStatus.Committed;
}
