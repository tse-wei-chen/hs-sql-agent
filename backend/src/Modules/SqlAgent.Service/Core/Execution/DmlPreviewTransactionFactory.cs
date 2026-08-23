using System.Data;
using System.Data.Common;
using FirebirdSql.Data.FirebirdClient;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Starts the transaction used to inspect the row set behind a DML approval. Providers that expose
/// a transaction-scoped read-only mode use it as a database-enforced second line of defence;
/// providers without an equivalent portable guarantee still use the configured isolation level
/// and the coordinator's SELECT-only preview plus rollback semantics.
/// </summary>
public interface IDmlPreviewTransactionFactory
{
    Task<DbTransaction> BeginAsync(
        DbConnection connection,
        SqlAgentToolType provider,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default);
}

public sealed class ProviderDmlPreviewTransactionFactory : IDmlPreviewTransactionFactory
{
    internal const string ReadOnlyTransactionSql = "SET TRANSACTION READ ONLY";

    public async Task<DbTransaction> BeginAsync(
        DbConnection connection,
        SqlAgentToolType provider,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return provider switch
        {
            SqlAgentToolType.MySQL => await BeginMySqlAsync(
                connection,
                isolationLevel,
                cancellationToken),
            SqlAgentToolType.Postgres => await BeginThenMarkReadOnlyAsync(
                connection,
                isolationLevel,
                cancellationToken),
            SqlAgentToolType.Oracle => await BeginOracleReadOnlyAsync(
                connection,
                cancellationToken),
            SqlAgentToolType.Firebird => await BeginFirebirdAsync(
                connection,
                isolationLevel,
                cancellationToken),
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite =>
                await connection.BeginTransactionAsync(isolationLevel, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported DML preview provider.")
        };
    }

    internal static DmlPreviewReadOnlyMode ReadOnlyMode(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.MySQL => DmlPreviewReadOnlyMode.BeforeTransactionSql,
        SqlAgentToolType.Postgres or SqlAgentToolType.Oracle =>
            DmlPreviewReadOnlyMode.InTransactionSql,
        SqlAgentToolType.Firebird => DmlPreviewReadOnlyMode.NativeTransactionOptions,
        SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite => DmlPreviewReadOnlyMode.NotAvailable,
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "Unsupported DML preview provider.")
    };

    internal static FbTransactionBehavior FirebirdBehavior(IsolationLevel isolationLevel) =>
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

    private static async Task<DbTransaction> BeginMySqlAsync(
        DbConnection connection,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        // MySQL applies transaction access mode to the next transaction, so READ ONLY must be
        // configured before BeginTransactionAsync starts the transaction.
        await ExecuteSetupSqlAsync(
            connection,
            transaction: null,
            ReadOnlyTransactionSql,
            cancellationToken);
        return await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
    }

    private static async Task<DbTransaction> BeginOracleReadOnlyAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        // Oracle READ ONLY is itself a transaction mode with a transaction-start consistent
        // snapshot. Do not combine it with BeginTransaction(Serializable): that would establish a
        // conflicting transaction mode before SET TRANSACTION READ ONLY. Begin a normal local
        // transaction only to obtain the DbTransaction handle; the first SQL statement establishes
        // the read-only snapshot mode.
        return await BeginThenMarkReadOnlyAsync(
            connection,
            IsolationLevel.ReadCommitted,
            cancellationToken);
    }

    private static async Task<DbTransaction> BeginThenMarkReadOnlyAsync(
        DbConnection connection,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
        try
        {
            // No application SQL is executed before this transaction characteristic is applied.
            await ExecuteSetupSqlAsync(
                connection,
                transaction,
                ReadOnlyTransactionSql,
                cancellationToken);
            return transaction;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the setup failure. Disposal below still releases provider resources.
            }

            await transaction.DisposeAsync();
            throw;
        }
    }

    private static async Task<DbTransaction> BeginFirebirdAsync(
        DbConnection connection,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        if (connection is not FbConnection firebird)
        {
            throw new InvalidOperationException(
                "Firebird DML preview requires an FbConnection so a native read-only TPB can be used.");
        }

        var options = new FbTransactionOptions
        {
            TransactionBehavior = FirebirdBehavior(isolationLevel),
            WaitTimeout = null
        };
        return await firebird.BeginTransactionAsync(options, cancellationToken);
    }

    private static async Task ExecuteSetupSqlAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

internal enum DmlPreviewReadOnlyMode
{
    NotAvailable = 0,
    BeforeTransactionSql = 1,
    InTransactionSql = 2,
    NativeTransactionOptions = 3
}
