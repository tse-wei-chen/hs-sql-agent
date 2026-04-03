using System.Data.Common;
using System.Text.Json;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;
using ToolBox.Enums;
using ToolBox.Models;

namespace ToolBox.Strategies;

public abstract class BaseSqlStrategy : ISqlStrategy
{
	public abstract SqlAgentToolType DbType { get; }

	protected abstract DbConnection CreateConnection(string? connectionString);
	protected abstract Compiler CreateCompiler();

	public async Task<string> ExecuteQueryAsync(
		string? connectionString = null,
		string? tableName = null,
		List<SelectCondition>? selectColumns = null,
		List<WhereCondition>? whereConditions = null,
		List<OrderByCondition>? orderByColumns = null,
		List<GroupByCondition>? groupByConditions = null,
		int? limit = null,
		List<JoinCondition>? joins = null,
		CancellationToken cancellationToken = default)
	{
		using var connection = CreateConnection(connectionString);
		await connection.OpenAsync(cancellationToken);

		var compiler = CreateCompiler();
		var db = new QueryFactory(connection, compiler);
		var query = new Query(tableName);
		if (selectColumns != null && selectColumns.Any())
		{
			foreach (var col in selectColumns)
			{
				if (!string.IsNullOrWhiteSpace(col.Aggregation) && IsSupportedAggregation(col.Aggregation))
				{
					query = query.SelectRaw($"{col.Aggregation.ToUpperInvariant()}({col.Field})");
				}
				else
					query = query.Select(col.Field);
			}
		}
		else
			query = query.Select("*");
		if (joins != null)
		{
			foreach (var join in joins)
			{
				if (!string.IsNullOrEmpty(join.Table) && !string.IsNullOrEmpty(join.First)
					&& !string.IsNullOrEmpty(join.Second) && !string.IsNullOrEmpty(join.Type))
				{
					var joinType = join.Type?.ToLowerInvariant() ?? "inner";
					var first = join.First;
					var op = join.Operator ?? "=";
					var second = join.Second;

					query = joinType switch
					{
						"left" => query.LeftJoin(join.Table, first, second, op),
						"right" => query.RightJoin(join.Table, first, second, op),
						"cross" => query.CrossJoin(join.Table),
						_ => query.Join(join.Table, first, second, op),
					};
				}
			}
		}
		if (whereConditions != null)
		{
			foreach (var cond in whereConditions)
			{
				query = query.Where(cond.Field, cond.Operator, cond.Value);
			}
		}
		if (groupByConditions != null && groupByConditions.Any())
		{
			var groupFields = groupByConditions
				.Where(g => !string.IsNullOrWhiteSpace(g.Field))
				.Select(condition => condition.Field)
				.ToArray();

			if (groupFields.Length > 0)
				query = query.GroupBy(groupFields);
		}
		if (orderByColumns != null && orderByColumns.Any())
		{
			foreach (var col in orderByColumns)
			{
				if (string.IsNullOrWhiteSpace(col.Field))
					continue;

				var field = col.Field;
				var direction = string.Equals(col.Direction, "DESC", StringComparison.OrdinalIgnoreCase)
					? "DESC"
					: "ASC";

				if (!string.IsNullOrWhiteSpace(col.Aggregation) &&
					IsSupportedAggregation(col.Aggregation))
				{
					query = query.OrderByRaw($"{col.Aggregation.ToUpperInvariant()}({field}) {direction}");
				}
				else
				{
					query = direction == "DESC"
						? query.OrderByDesc(field)
						: query.OrderBy(field);
				}
			}
		}
		if (limit != null && limit > 0)
			query = query.Limit(limit.Value);

		var result = await db.GetAsync(query, cancellationToken: cancellationToken);

		var resultList = result.Select(r => (IDictionary<string, object>)r).ToList();
		return JsonSerializer.Serialize(resultList);
	}

	private static bool IsSupportedAggregation(string aggregation)
	{
		return aggregation.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";
	}

	public abstract Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetTablesAsync(string connectionString, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
	public abstract Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
}