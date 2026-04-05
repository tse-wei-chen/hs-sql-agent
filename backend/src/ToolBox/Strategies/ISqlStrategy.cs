using ToolBox.Enums;
using ToolBox.Models;

namespace ToolBox.Strategies;

public interface ISqlStrategy
{
	SqlAgentToolType DbType { get; }
	Task<string> ExecuteQueryAsync(
		string? connectionString = null,
		string? tableName = null,
		List<SelectCondition>? selectColumns = null,
		List<WhereCondition>? whereConditions = null,
		List<DateWhereCondition>? dateWhereConditions = null,
		List<InWhereCondition>? inWhereConditions = null,
		List<StringWhereCondition>? stringWhereConditions = null,
		List<OrderByCondition>? orderByColumns = null,
		List<GroupByCondition>? groupByConditions = null,
		List<HavingCondition>? havingConditions = null,
		List<CombineCondition>? combineConditions = null,
		List<CteCondition>? cteConditions = null,
		int? limit = null,
		List<JoinCondition>? joins = null,
		CancellationToken cancellationToken = default
	);
	Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
	Task<List<string>> GetTablesAsync(string connectionString, CancellationToken cancellationToken = default);
	Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
	Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
}