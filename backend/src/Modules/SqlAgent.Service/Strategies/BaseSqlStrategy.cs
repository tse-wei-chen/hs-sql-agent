using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;
using SqlKata.Extensions;

namespace SqlAgent.Service.Strategies;

public abstract class BaseSqlStrategy : ISqlStrategy
{
	private readonly IQueryValueParserService _valueParser;

	protected BaseSqlStrategy(IQueryValueParserService valueParser)
	{
		_valueParser = valueParser;
	}

	public abstract SqlAgentToolType DbType { get; }

	protected abstract DbConnection CreateConnection(string? connectionString);
	protected abstract Compiler CreateCompiler();

	#region Security Guards
	#endregion
	#region Shared Query Builders

	private Query ApplySelectColumns(Query query, IList<SelectCondition> cols)
	{
		if (cols == null || cols.Count == 0) return query.Select("*");

		foreach (var col in cols)
		{
			var hasAlias = !string.IsNullOrWhiteSpace(col.Alias);
			var hasAgg = !string.IsNullOrWhiteSpace(col.Aggregation);

			AbstractColumn columnExpr;

			if (col.Arithmetic != null)
			{
				columnExpr = MapArithmetic(col.Arithmetic);
			}
			else if (!string.IsNullOrWhiteSpace(col.Field))
			{
				columnExpr = new Column { Name = col.Field.Trim() };
			}
			else
			{
				continue;
			}

			if (hasAgg)
			{
				columnExpr = new AggregatedColumn
				{
					Aggregate = col.Aggregation,
					Column = columnExpr
				};
			}

			if (hasAlias)
			{
				if (columnExpr is ArithmeticColumn ac) ac.Alias = col.Alias;
				else if (columnExpr is Column c) c.Name = $"{c.Name} AS {col.Alias}";
				else if (columnExpr is AggregatedColumn ag)
				{
					if (ag.Column is Column inner) inner.Name = $"{inner.Name} AS {col.Alias}";
				}
			}

			query = query.Select(columnExpr);
		}

		return query;
	}

	private AbstractColumn MapArithmetic(SelectArithmeticCondition arithmetic)
	{
		if (arithmetic.Left != null && arithmetic.Operator != null && arithmetic.Right != null)
		{
			return MapArithmetic(arithmetic.Left).Arithmetic(arithmetic.Operator, MapArithmetic(arithmetic.Right));
		}

		if (arithmetic.Constant != null)
		{
			return new NumberColumn { Value = arithmetic.Constant };
		}

		return new Column { Name = arithmetic.FieldName ?? string.Empty };
	}

	private Query ApplyJoins(Query query, IList<JoinCondition> joins)
	{
		foreach (var join in joins)
		{
			query = join.Type.ToLowerInvariant() switch
			{
				"left" => query.LeftJoin(join.Table, join.First, join.Second, join.Operator),
				"right" => query.RightJoin(join.Table, join.First, join.Second, join.Operator),
				"cross" => query.CrossJoin(join.Table),
				_ => query.Join(join.Table, join.First, join.Second, join.Operator),
			};
		}

		return query;
	}

	private Query ApplyWhereConditions(Query query, IList<WhereCondition> conds)
	{
		return conds.Where(c => !string.IsNullOrWhiteSpace(c.Field))
			.Aggregate(query, (q, c) =>
			{
				var op = c.Operator?.ToLowerInvariant().Trim();
				var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;

				return op switch
				{
					"is" or "isnull" => q.Where(c.Field, val),
					"isnot" or "isnotnull" => q.WhereNot(c.Field, val),

					"in" or "notin" when _valueParser.TryGetInValues(c.Value, out var ins)
						=> op == "in" ? q.WhereIn(c.Field, ins) : q.WhereNotIn(c.Field, ins),

					"between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high)
						=> op == "between" ? q.WhereBetween(c.Field, low, high) : q.WhereNotBetween(c.Field, low, high),

					"like" => q.WhereLike(c.Field, val),
					"notlike" => q.WhereNotLike(c.Field, val),
					"starts" => q.WhereStarts(c.Field, val),
					"ends" => q.WhereEnds(c.Field, val),
					"contains" => q.WhereContains(c.Field, val),

					_ => q.Where(c.Field, c.Operator, val)
				};
			});
	}

	private Query ApplyDateWhereConditions(Query query, IList<DateWhereCondition> conds)
	{
		return conds.Where(c => !string.IsNullOrWhiteSpace(c.Field))
			.Aggregate(query, (q, c) =>
			{
				var op = c.Operator?.ToLowerInvariant().Trim();
				var val = _valueParser.TryToDateTime(c.Value, out var dt) ? dt : (object?)null;

				return op switch
				{
					"is" or "isnull" => q.WhereDate(c.Field, val),
					"isnot" or "isnotnull" => q.WhereNotDate(c.Field, val),

					_ => q.WhereDate(c.Field, c.Operator, val)
				};
			});
	}

	private Query ApplyGroupByConditions(Query query, IList<GroupByCondition> conds)
	{
		foreach (var gf in conds)
		{
			var field = gf.Field.Trim();
			query = query.GroupBy(field);
		}

		return query;
	}

	private Query ApplyHavingConditions(Query query, IList<HavingCondition> conds)
	{
		if (conds == null || !conds.Any()) return query;

		return conds.Where(c => !string.IsNullOrWhiteSpace(c.Field))
			.Aggregate(query, (q, c) =>
			{
				var op = c.Operator.ToLowerInvariant().Trim();
				var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;

				var agg = c.Aggregation;
				var isAgg = agg != null;
				var field = c.Field.Trim();
				return op switch
				{
					"between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high) =>
						isAgg
							? (op == "between" ? q.HavingBetweenAggregate(agg, field, low, high) : q.HavingNotBetweenAggregate(agg, field, low, high))
							: (op == "between" ? q.HavingBetween(field, low, high) : q.HavingNotBetween(field, low, high)),

					"is" or "isnull" => isAgg ? q.HavingAggregate(agg, field, "=", null) : q.HavingNull(field),
					"isnot" or "isnotnull" => isAgg ? q.Not().HavingAggregate(agg, field, "=", null) : q.HavingNotNull(field),

					"in" when _valueParser.TryGetInValues(val, out var ins)
						=> isAgg ? q.HavingInAggregate(agg, field, ins) : q.HavingIn(field, ins),
					"notin" when _valueParser.TryGetInValues(val, out var nins)
						=> isAgg ? q.HavingNotInAggregate(agg, field, nins) : q.HavingNotIn(field, nins),

					"like" => isAgg ? q.HavingLikeAggregate(agg, field, val) : q.HavingLike(field, val),
					"starts" => isAgg ? q.HavingStartsAggregate(agg, field, val) : q.HavingStarts(field, val),
					"ends" => isAgg ? q.HavingEndsAggregate(agg, field, val) : q.HavingEnds(field, val),
					"contains" => isAgg ? q.HavingContainsAggregate(agg, field, val) : q.HavingContains(field, val),


					_ => isAgg
						? q.HavingAggregate(agg, field, op, val)
						: q.Having(field, op, val)
				};
			});
	}

	private Query ApplyDateHavingConditions(Query query, IList<DateHavingCondition> conds)
	{
		if (conds == null || !conds.Any()) return query;

		return conds.Where(c => !string.IsNullOrWhiteSpace(c.Field))
			.Aggregate(query, (q, c) =>
			{
				var op = c.Operator.ToLowerInvariant().Trim();
				var val = _valueParser.TryToDateTime(c.Value, out var dt) ? dt : (object?)null;

				var agg = c.Aggregation;
				var isAgg = agg != null;
				var field = c.Field.Trim();
				return op switch
				{
					"is" or "isnull" => isAgg
						? q.HavingDateAggregate(agg, field, "=", null)
						: q.HavingNull(field),

					"isnot" or "isnotnull" => isAgg
						? q.Not().HavingDateAggregate(agg, field, "=", null)
						: q.HavingNotNull(field),

					"between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high) => isAgg
						? (op == "between"
							? q.HavingBetweenAggregate(agg, field, low, high)
							: q.HavingNotBetweenAggregate(agg, field, low, high))
						: (op == "between"
							? q.HavingBetween(field, low, high)
							: q.HavingNotBetween(field, low, high)),

					_ => isAgg
						? q.HavingDateAggregate(agg, field, op, val)
						: q.HavingDate(field, op, val)
				};
			});
	}

	private Query ApplyOrderByColumns(Query query, IList<OrderByCondition> cols)
	{
		if (cols == null || !cols.Any()) return query;

		return cols.Where(c => !string.IsNullOrWhiteSpace(c.Field))
			.Aggregate(query, (q, c) =>
			{
				var field = c.Field.Trim();
				var dir = c.Direction?.ToLowerInvariant().Trim() ?? "asc";

				return dir switch
				{
					"random" => q.OrderByRandom(field),
					"desc" => q.OrderByDesc(field),
					"asc" => q.OrderBy(field),
					_ => q.OrderBy(field)
				};
			});
	}
	#endregion
	#region ExecuteQueryAsync

	public async Task<string> ExecuteQueryAsync(
		string? connectionString = null,
		string? tableName = null,
		List<SelectCondition>? selectColumns = null,
		List<WhereCondition>? whereConditions = null,
		List<DateWhereCondition>? dateWhereConditions = null,
		List<OrderByCondition>? orderByColumns = null,
		List<GroupByCondition>? groupByConditions = null,
		List<HavingCondition>? havingConditions = null,
		List<DateHavingCondition>? dateHavingConditions = null,
		List<CombineCondition>? combineConditions = null,
		List<CteCondition>? cteConditions = null,
		int? limit = null,
		List<JoinCondition>? joins = null,
		CancellationToken cancellationToken = default)
	{
		using var connection = CreateConnection(connectionString);
		await connection.OpenAsync(cancellationToken);

		var compiler = CreateCompiler();
		var db = new QueryFactory(connection, compiler);
		var query = new Query(tableName);

		query = ApplySelectColumns(query, selectColumns ?? []);

		if (joins?.Count > 0)
			query = ApplyJoins(query, joins);

		if (whereConditions?.Count > 0)
			query = ApplyWhereConditions(query, whereConditions);

		if (dateWhereConditions?.Count > 0)
			query = ApplyDateWhereConditions(query, dateWhereConditions);

		if (groupByConditions?.Count > 0)
			query = ApplyGroupByConditions(query, groupByConditions);

		if (havingConditions?.Count > 0)
			query = ApplyHavingConditions(query, havingConditions);

		if (dateHavingConditions?.Count > 0)
			query = ApplyDateHavingConditions(query, dateHavingConditions);

		if (cteConditions?.Count > 0)
		{
			foreach (var cte in cteConditions)
			{
				if (string.IsNullOrWhiteSpace(cte.Name) || string.IsNullOrWhiteSpace(cte.Query?.TableName))
					continue;
				query = query.With(cte.Name, BuildQueryFromDefinition(cte.Query));
			}
		}

		if (combineConditions?.Count > 0)
		{
			foreach (var combine in combineConditions)
			{
				if (string.IsNullOrWhiteSpace(combine.Query?.TableName)) continue;

				var sub = BuildQueryFromDefinition(combine.Query);
				var combineType = combine.Type?.ToLowerInvariant().Replace("_", "").Trim() ?? "union";
				query = combineType switch
				{
					"unionall" => query.Union(sub, all: true),
					"intersect" => query.Intersect(sub),
					"except" => query.Except(sub),
					_ => query.Union(sub),
				};
			}
		}

		var hasCombineConditions = combineConditions?.Count > 0;
		if (hasCombineConditions)
		{
			var wrapper = new Query().From(query.As("combined_result"));
			if (orderByColumns?.Count > 0)
				wrapper = ApplyOrderByColumns(wrapper, orderByColumns);
			if (limit > 0)
				wrapper = wrapper.Limit(limit.Value);
			query = wrapper;
		}
		else
		{
			if (orderByColumns?.Count > 0)
				query = ApplyOrderByColumns(query, orderByColumns);
			if (limit > 0)
				query = query.Limit(limit.Value);
		}

		try
		{
			var result = await db.GetAsync(query, cancellationToken: cancellationToken);
			return SerializeQueryResult(result);
		}
		catch (Exception ex)
		{
			return BuildExecutionErrorMessage(ex);
		}
	}
	#endregion
	#region BuildQueryFromDefinition

	private Query BuildQueryFromDefinition(QueryDefinition definition)
	{
		var query = new Query(definition.TableName);

		query = ApplySelectColumns(query, definition.SelectColumns ?? []);

		if (definition.Joins?.Count > 0)
			query = ApplyJoins(query, definition.Joins);

		if (definition.WhereColumnsAndValues?.Count > 0)
			query = ApplyWhereConditions(query, definition.WhereColumnsAndValues);

		if (definition.DateWhereConditions?.Count > 0)
			query = ApplyDateWhereConditions(query, definition.DateWhereConditions);

		if (definition.GroupByConditions?.Count > 0)
			query = ApplyGroupByConditions(query, definition.GroupByConditions);

		if (definition.HavingConditions?.Count > 0)
			query = ApplyHavingConditions(query, definition.HavingConditions);

		if (definition.OrderByColumns?.Count > 0)
			query = ApplyOrderByColumns(query, definition.OrderByColumns);

		if (definition.Limit > 0)
			query = query.Limit(definition.Limit!.Value);

		return query;
	}
	#endregion

	#region Error Helpers

	protected virtual string SerializeQueryResult(IEnumerable<dynamic> result)
	{
		var resultList = result.Select(r => (IDictionary<string, object>)r).ToList();
		return JsonSerializer.Serialize(resultList);
	}

	protected virtual string BuildExecutionErrorMessage(Exception ex)
	{
		var code = TryExtractSqlStateCode(ex.Message);
		var hint = BuildHint(code, ex.Message);
		var action = BuildNextAction(code, ex.Message);

		return $"Error executing query | code={code ?? "unknown"} | hint={hint} | nextAction={action}";
	}

	protected static string? TryExtractSqlStateCode(string message)
	{
		var match = Regex.Match(message ?? string.Empty, @"\b(?<code>[0-9A-Z]{5})\b");
		if (!match.Success) return null;

		var code = match.Groups["code"].Value;
		return code.Any(char.IsDigit) ? code : null;
	}

	protected virtual string BuildHint(string? code, string message)
	{
		return "Use the SQL and bindings to adjust fields/operators/types, then retry.";
	}

	protected virtual string BuildNextAction(string? code, string message)
	{
		return "Retry after adjusting query fields according to the SQL error. Prefer specialized params: dateWhereConditions and inWhereConditions.";
	}
	#endregion

	#region Abstract Members

	public abstract Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
	public abstract Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);

	#endregion
}