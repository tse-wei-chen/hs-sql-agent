using System.Data.Common;
using Microsoft.Extensions.Configuration;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlAgent.Service.Services;

namespace SqlAgent.Service.Strategies;

/// <summary>
/// Transitional provider strategy base. SQL parsing, compilation, policy rewriting, lowering and
/// execution belong to the Core/typed runtime pipeline. Strategy subclasses retain only provider
/// connection/metadata responsibilities while callers migrate to native ISqlProvider components.
/// </summary>
public abstract class BaseSqlStrategy : ISqlStrategy
{
    static BaseSqlStrategy()
    {
        DapperTemporalTypeHandlerRegistry.EnsureRegistered();
    }

    protected BaseSqlStrategy(
        IQueryValueParserService valueParser,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(valueParser);
        ArgumentNullException.ThrowIfNull(configuration);
    }

    public abstract SqlAgentToolType DbType { get; }
    public abstract string BuildConnectionString(BuildDbConnectionModelBase model);
    public abstract DbConnection CreateConnection(string? connectionString);

    public abstract Task<List<string>> GetSchemasAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    public abstract Task<List<string>> GetTablesAsync(
        string connectionString,
        string schemaName,
        CancellationToken cancellationToken = default);

    public abstract Task<List<ColumnInfo>> GetColumnsAsync(
        string connectionString,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken = default);
}
