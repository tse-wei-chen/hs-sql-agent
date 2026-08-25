using System.Data;
using System.Data.Common;
using FirebirdSql.Data.FirebirdClient;
using SqlAgent.Service.Core.Execution;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Providers;

/// <summary>
/// Firebird preview transactions must use a native read-only TPB; the portable DbTransaction API
/// cannot express the required Read/NoWait/isolation behavior without losing the database-enforced
/// safety guarantee.
/// </summary>
public sealed class FirebirdDmlPreviewTransactionFactory : IDmlPreviewTransactionFactory
{
    public async Task<DbTransaction> BeginAsync(
        DbConnection connection,
        SqlAgentToolType provider,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (provider != SqlAgentToolType.Firebird)
        {
            throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Firebird preview transactions can only be used with the Firebird provider.");
        }

        if (connection is not FbConnection firebird)
        {
            throw new InvalidOperationException(
                "Firebird DML preview requires an FbConnection so a native read-only TPB can be used.");
        }

        var options = new FbTransactionOptions
        {
            TransactionBehavior = ResolveBehavior(isolationLevel),
            WaitTimeout = null
        };
        return await firebird.BeginTransactionAsync(options, cancellationToken);
    }

    internal static FbTransactionBehavior ResolveBehavior(IsolationLevel isolationLevel) =>
        isolationLevel switch
        {
            IsolationLevel.Serializable =>
                FbTransactionBehavior.Read |
                FbTransactionBehavior.NoWait |
                FbTransactionBehavior.Consistency,
            IsolationLevel.RepeatableRead or IsolationLevel.Snapshot =>
                FbTransactionBehavior.Read |
                FbTransactionBehavior.NoWait |
                FbTransactionBehavior.Concurrency,
            IsolationLevel.ReadCommitted or IsolationLevel.ReadUncommitted or IsolationLevel.Unspecified =>
                FbTransactionBehavior.Read |
                FbTransactionBehavior.NoWait |
                FbTransactionBehavior.ReadCommitted |
                FbTransactionBehavior.RecVersion,
            _ => throw new ArgumentOutOfRangeException(
                nameof(isolationLevel),
                isolationLevel,
                "Unsupported Firebird DML preview isolation level.")
        };
}
