using System.Data.Common;
using SqlAgent.Service.Core.Providers;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

/// <summary>
/// Transitional provider-management contract layered on the Core provider runtime. Any remaining
/// strategy is therefore an ISqlProvider by type; query compilation/execution and DML execution
/// stay in the Core/typed pipelines while connection-string construction and legacy metadata
/// shapes are retired incrementally.
/// </summary>
public interface ISqlStrategy : ISqlProvider
{
    SqlAgentToolType DbType { get; }
    string BuildConnectionString(BuildDbConnectionModelBase model);
    DbConnection CreateConnection(string connectionString);
    Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
    Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);
    Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default);
}