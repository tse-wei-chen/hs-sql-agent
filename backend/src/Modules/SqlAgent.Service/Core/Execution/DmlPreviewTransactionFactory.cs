using System.Data;
using System.Data.Common;
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

/// <summary>
/// Optional provider capability for databases whose preview transaction semantics require a
/// provider-native transaction API. Existing ISqlProvider implementations do not have to implement
/// this side-interface; callers fall back to the driver-neutral policy and unsupported native-only
/// modes remain fail-closed.
/// </summary>
public interface IProviderDmlPreviewTransactionSource
{
    IDmlPreviewTransactionFactory PreviewTransactions { get; }
}

/// <summary>
/// Driver-neutral preview transaction policy for providers whose read-only semantics can be expressed
/// through portable DbConnection APIs and fixed SQL. Providers that need native transaction options
/// supply their own implementation through IProviderDmlPreviewTransactionSource.
/// </summary>
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
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite =>
                await connection.BeginTransactionAsync(isolationLevel, cancellationToken),
            SqlAgentToolType.Firebird => throw new InvalidOperationException(
                "Firebird DML preview requires the provider-native preview transaction factory so a read-only TPB is enforced."),
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

    private static async Task<DbTransaction> BeginMySqlAsync(
        DbConnection connection,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
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
