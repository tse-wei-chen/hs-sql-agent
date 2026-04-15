using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public interface ISqlStrategy
{
    SqlAgentToolType DbType { get; }
    Task<string> ExecuteQueryAsync(
        string? connectionString = null,
        string? tableName = null,
        List<SelectCondition>? selectColumns = null,
        List<WhereCondition>? whereConditions = null,
        List<OrderByCondition>? orderByColumns = null,
        List<GroupByCondition>? groupByConditions = null,
        List<HavingCondition>? havingConditions = null,
        List<CombineCondition>? combineConditions = null,
        List<CteCondition>? cteConditions = null,
        int? limit = null,
        int? offset = null,
        List<JoinCondition>? joins = null,
        QueryDefinition? fromQuery = null,
        string? alias = null,
        bool distinct = false,
        CancellationToken cancellationToken = default
    );
    Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
    Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);
    Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
    Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
    Task<string> ExecuteDmlAsync(
        string? connectionString = null,
        DmlDefinition? dml = null,
        CancellationToken cancellationToken = default
    );
}
