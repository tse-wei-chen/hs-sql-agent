using System.Data.Common;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public interface ISqlStrategy
{
    SqlAgentToolType DbType { get; }
    string BuildConnectionString(BuildDbConnectionModelBase model);
    DbConnection CreateConnection(string connectionString);
    Task<string> ExecuteQueryAsync(
        QueryDefinition definition,
        string? connectionString,
        CancellationToken cancellationToken = default
    );
    Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
    Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);
    Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default);
    Task<string> ExecuteDmlAsync(
        string? connectionString = null,
        DmlDefinition? dml = null,
        CancellationToken cancellationToken = default
    );
}
