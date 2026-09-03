using System.Data.Common;

namespace HsSqlAgent.Provider.Abstractions;

public abstract class SqlProviderBase :
    ISqlProvider,
    IDbConnectionFactory,
    IProviderMetadataReader,
    IProviderTableLookup,
    IProviderDmlPreviewTransactionSource
{
    private IProviderErrorMapper? _errors;
    private IDmlPreviewTransactionFactory? _previewTransactions;

    public abstract SqlAgentToolType DbType { get; }
    public SqlAgentToolType Type => DbType;
    public IDbConnectionFactory Connections => this;
    public IProviderMetadataReader Metadata => this;
    public IProviderErrorMapper Errors => _errors ??= new ProviderExecutionErrorMapper(DbType);
    public virtual IDmlPreviewTransactionFactory PreviewTransactions =>
        _previewTransactions ??= new ProviderDmlPreviewTransactionFactory();

    public abstract string BuildConnectionString(BuildDbConnectionModelBase model);
    public abstract DbConnection CreateConnection(string? connectionString);
    public abstract Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
    public abstract Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);

    public virtual async Task<IReadOnlyList<DatabaseTableMetadata>> FindTablesAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var matches = new List<DatabaseTableMetadata>();
        var schemas = await GetSchemasAsync(connectionString, cancellationToken);
        foreach (var schema in schemas)
        {
            var tables = await GetTablesAsync(connectionString, schema, cancellationToken);
            foreach (var table in tables)
            {
                if (string.Equals(table, tableName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(new DatabaseTableMetadata(schema, table));
            }
        }

        return matches;
    }

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
