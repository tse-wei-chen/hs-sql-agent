using System.Data;
using System.Data.Common;
using SqlAgent.Service.Enums;

namespace HsSqlAgent.Provider.Abstractions;

public interface IDmlPreviewTransactionFactory
{
    Task<DbTransaction> BeginAsync(
        DbConnection connection,
        SqlAgentToolType provider,
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default);
}

public interface IProviderDmlPreviewTransactionSource
{
    IDmlPreviewTransactionFactory PreviewTransactions { get; }
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
            SqlAgentToolType.MySQL => await BeginMySqlAsync(connection, isolationLevel, cancellationToken),
            SqlAgentToolType.Postgres => await BeginThenMarkReadOnlyAsync(connection, isolationLevel, cancellationToken),
            SqlAgentToolType.Oracle => await BeginOracleReadOnlyAsync(connection, cancellationToken),
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite => await connection.BeginTransactionAsync(isolationLevel, cancellationToken),
            SqlAgentToolType.Firebird => throw new InvalidOperationException(
                "Firebird DML preview requires the provider-native preview transaction factory so a read-only TPB is enforced."),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported DML preview provider.")
        };
    }

    internal static DmlPreviewReadOnlyMode ReadOnlyMode(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.MySQL => DmlPreviewReadOnlyMode.BeforeTransactionSql,
        SqlAgentToolType.Postgres or SqlAgentToolType.Oracle => DmlPreviewReadOnlyMode.InTransactionSql,
        SqlAgentToolType.Firebird => DmlPreviewReadOnlyMode.NativeTransactionOptions,
        SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite => DmlPreviewReadOnlyMode.NotAvailable,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported DML preview provider.")
    };

    private static async Task<DbTransaction> BeginMySqlAsync(DbConnection connection, IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        await ExecuteSetupSqlAsync(connection, null, ReadOnlyTransactionSql, cancellationToken);
        return await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
    }

    private static async Task<DbTransaction> BeginOracleReadOnlyAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await BeginThenMarkReadOnlyAsync(connection, IsolationLevel.ReadCommitted, cancellationToken);

    private static async Task<DbTransaction> BeginThenMarkReadOnlyAsync(DbConnection connection, IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
        try
        {
            await ExecuteSetupSqlAsync(connection, transaction, ReadOnlyTransactionSql, cancellationToken);
            return transaction;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteSetupSqlAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
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
