using System.Data.Common;
using HsSqlAgent.SqlCore.Core.Lowering;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;

namespace HsSqlAgent.Provider.Abstractions;

public abstract class SqlProviderBase :
    ISqlProvider,
    IDbConnectionFactory,
    IProviderMetadataReader,
    IProviderDmlPreviewTransactionSource
{
    private IProviderLowerer? _lowerer;
    private IProviderErrorMapper? _errors;
    private IDmlPreviewTransactionFactory? _previewTransactions;

    public abstract SqlAgentToolType DbType { get; }
    public SqlAgentToolType Type => DbType;
    public IDbConnectionFactory Connections => this;
    public IProviderLowerer Lowerer => _lowerer ??= new SqlKataProviderLowerer(DbType);
    public IProviderMetadataReader Metadata => this;
    public IProviderErrorMapper Errors => _errors ??= new ProviderExecutionErrorMapper(DbType);
    public virtual IDmlPreviewTransactionFactory PreviewTransactions =>
        _previewTransactions ??= new ProviderDmlPreviewTransactionFactory();

    public abstract string BuildConnectionString(BuildDbConnectionModelBase model);
    public abstract DbConnection CreateConnection(string? connectionString);
    public abstract Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
    public abstract Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);
    public abstract Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default);
    public abstract Task<List<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default);

    DbConnection IDbConnectionFactory.Create(string connectionString) => CreateConnection(connectionString);
    async Task<IReadOnlyList<string>> IProviderMetadataReader.GetSchemasAsync(string connectionString, CancellationToken cancellationToken) => await GetSchemasAsync(connectionString, cancellationToken);
    async Task<IReadOnlyList<string>> IProviderMetadataReader.GetTablesAsync(string connectionString, string schema, CancellationToken cancellationToken) => await GetTablesAsync(connectionString, schema, cancellationToken);

    async Task<IReadOnlyList<DatabaseColumnMetadata>> IProviderMetadataReader.GetColumnsAsync(string connectionString, string schema, string table, CancellationToken cancellationToken)
    {
        var columns = await GetColumnsAsync(connectionString, schema, table, cancellationToken);
        return columns.Select(column => new DatabaseColumnMetadata(schema, table, column.Name, column.Type, column.IsPrimaryKey, column.PrimaryKeyOrdinal)).ToArray();
    }

    async Task<IReadOnlyList<DatabaseUniqueKeyMetadata>> IProviderMetadataReader.GetUniqueKeysAsync(string connectionString, string schema, string table, CancellationToken cancellationToken) =>
        await GetUniqueKeysAsync(connectionString, schema, table, cancellationToken);
}
