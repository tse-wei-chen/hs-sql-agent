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
public abstract class BaseSqlStrategy(
    IQueryValueParserService valueParser,
    IConfiguration configuration) : ISqlStrategy
{
    static BaseSqlStrategy()
    {
        DapperTemporalTypeHandlerRegistry.EnsureRegistered();
    }

    // Kept in the constructor contract until provider registrations stop constructing strategies
    // directly. Translation/execution no longer consume either dependency from this base class.
    private readonly IQueryValueParserService _valueParser = valueParser;
    protected readonly IConfiguration _configuration = configuration;

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

    // Removed from the public strategy contract and retained only until provider-specific formatter
    // implementations are deleted after the adapter-owned IProviderErrorMapper is proven in CI.
    protected abstract string BuildExecutionErrorMessage(Exception ex, string type);
}
