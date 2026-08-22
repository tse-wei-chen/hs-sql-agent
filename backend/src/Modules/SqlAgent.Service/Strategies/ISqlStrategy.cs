using System.Data.Common;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

/// <summary>
/// Transitional provider contract. Query compilation/execution belongs to Core/typed runtimes;
/// DML execution belongs to the typed approval pipeline. Strategies retain only provider identity,
/// connection construction and metadata access until native ISqlProvider implementations replace them.
/// </summary>
public interface ISqlStrategy
{
    SqlAgentToolType DbType { get; }
    string BuildConnectionString(BuildDbConnectionModelBase model);
    DbConnection CreateConnection(string connectionString);
    Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
    Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);
    Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default);
}
