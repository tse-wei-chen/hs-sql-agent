using System.Data;
using HsSqlAgent.SqlCore.Enums;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Defines the isolation guarantees used by typed DML approval.
/// Preview must observe a stable row set for fingerprinting, while commit must prevent a
/// revalidation-to-mutation race after the approved row set has been checked.
/// </summary>
public interface IDmlTransactionIsolationPolicy
{
    IsolationLevel PreviewIsolation(SqlAgentToolType provider);
    IsolationLevel CommitIsolation(SqlAgentToolType provider);
}

/// <summary>
/// Conservative cross-provider policy. PostgreSQL and MySQL expose repeatable-read snapshot
/// semantics directly for preview. Providers whose portable ADO.NET surface cannot request a
/// snapshot transaction without deployment-specific configuration use Serializable instead.
/// Commit is always Serializable so row-set revalidation and the mutation execute inside one
/// transaction with the strongest portable isolation available from DbConnection.
/// </summary>
public sealed class StrictDmlTransactionIsolationPolicy : IDmlTransactionIsolationPolicy
{
    public IsolationLevel PreviewIsolation(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.Postgres or SqlAgentToolType.MySQL => IsolationLevel.RepeatableRead,
        SqlAgentToolType.Sqlite
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle
            or SqlAgentToolType.Firebird => IsolationLevel.Serializable,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported DML provider.")
    };

    public IsolationLevel CommitIsolation(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.Sqlite
            or SqlAgentToolType.Postgres
            or SqlAgentToolType.MySQL
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle
            or SqlAgentToolType.Firebird => IsolationLevel.Serializable,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported DML provider.")
    };
}
