using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Interfaces;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public abstract class BaseSqlStrategy : ISqlStrategy
{
	private readonly IValidator _validator;
	private readonly IQueryValueParserService _valueParser;

	protected BaseSqlStrategy(IValidator validator, IQueryValueParserService valueParser)
	{
		_validator = validator;
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
		if (cols.Count == 0) return query.Select("*");

		foreach (var col in cols)
		{
			if (!string.IsNullOrWhiteSpace(col.Aggregation))
			{
				var agg = _validator.RequireSafeAggregation(col.Aggregation);
				var field = _validator.RequireSafeIdentifier(col.Field, "select aggregation field");
				if (!string.IsNullOrWhiteSpace(col.Alias))
				{
					var alias = _validator.RequireSafeIdentifier(col.Alias, "select aggregation alias");
					query = query.SelectRaw($"{agg}({field}) AS {alias}");
				}
				else
				{
					query = query.SelectAggregate(agg, field);
				}
			}
			else if (!string.IsNullOrWhiteSpace(col.Alias))
			{
				var field = _validator.RequireSafeIdentifier(col.Field, "select field");
				var alias = _validator.RequireSafeIdentifier(col.Alias, "select alias");
				query = query.Select($"{field} AS {alias}");
			}
			else
			{
				var field = _validator.RequireSafeIdentifier(col.Field, "select field");
				query = query.Select(field);
			}
		}

		return query;
	}

	private Query ApplyJoins(Query query, IList<JoinCondition> joins)
	{
		foreach (var join in joins)
		{
			if (string.IsNullOrEmpty(join.Table) || string.IsNullOrEmpty(join.First)
				|| string.IsNullOrEmpty(join.Second) || string.IsNullOrEmpty(join.Type))
				continue;

			var joinType = join.Type.Trim().ToLowerInvariant();
			if (!_validator.IsAllowedJoinType(joinType))
				throw new InvalidOperationException($"Unsupported join type: '{join.Type}'");

			var first = _validator.RequireSafeIdentifier(join.First, "join first");
			var second = _validator.RequireSafeIdentifier(join.Second, "join second");
			var op = _validator.GetSafeOperator(join.Operator);
			var table = _validator.RequireSafeIdentifier(join.Table, "join table");

			query = joinType switch
			{
				"left" => query.LeftJoin(table, first, second, op),
				"right" => query.RightJoin(table, first, second, op),
				"cross" => query.CrossJoin(table),
				_ => query.Join(table, first, second, op),
			};
		}

		return query;
	}

	private Query ApplyWhereConditions(Query query, IList<WhereCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field)) continue;

			var op = _validator.GetSafeOperator(cond.Operator).ToLowerInvariant();
			var rawValue = cond.Value;
			var value = rawValue is JsonElement je ? _valueParser.UnwrapJsonElement(je) : rawValue;

			if (op is "is" or "is null")
			{
				query = value is null ? query.WhereNull(cond.Field) : query.Where(cond.Field, "=", value);
				continue;
			}

			if (op is "is not" or "is not null")
			{
				query = value is null ? query.WhereNotNull(cond.Field) : query.Where(cond.Field, "!=", value);
				continue;
			}

			if (op is "in" or "not in")
			{
				if (_valueParser.TryGetInValues(rawValue, out var values))
				{
					query = op == "in"
						? query.WhereIn(cond.Field, values)
						: query.WhereNotIn(cond.Field, values);
					continue;
				}
			}

			query = query.Where(cond.Field, _validator.GetSafeOperator(cond.Operator), value);
		}

		return query;
	}

	private Query ApplyDateWhereConditions(Query query, IList<DateWhereCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field) || string.IsNullOrWhiteSpace(cond.Value))
				continue;

			var op = _validator.GetSafeOperator(cond.Operator);
			if (!_valueParser.TryToDateTime(cond.Value, out var dt)) continue;

			query = query.WhereDate(cond.Field, op, dt);
		}

		return query;
	}

	private Query ApplyInWhereConditions(Query query, IList<InWhereCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field) || cond.Values == null || cond.Values.Count == 0)
				continue;

			var values = cond.Values
				.Select(v => v is JsonElement je ? _valueParser.UnwrapJsonElement(je) : v)
				.Where(v => v is not null)
				.ToArray();

			if (values.Length == 0) continue;

			query = cond.NotIn
				? query.WhereNotIn(cond.Field, values)
				: query.WhereIn(cond.Field, values);
		}

		return query;
	}

	private Query ApplyStringWhereConditions(Query query, IList<StringWhereCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field) || string.IsNullOrWhiteSpace(cond.Value))
				continue;

			var mode = (cond.MatchMode ?? "contains").Trim().ToLowerInvariant();
			var pattern = mode switch
			{
				"starts" => $"{cond.Value}%",
				"ends" => $"%{cond.Value}",
				"like" => cond.Value,
				_ => $"%{cond.Value}%"
			};

			if (cond.CaseInsensitive)
			{
				if (DbType == SqlAgentToolType.Postgres)
				{
					query = query.Where(cond.Field, "ilike", pattern);
				}
				else
				{
					var safeField = _validator.RequireSafeIdentifier(cond.Field, "string where field");
					query = query.WhereRaw($"LOWER({safeField}) LIKE ?", pattern.ToLowerInvariant());
				}
			}
			else
			{
				query = query.Where(cond.Field, "like", pattern);
			}
		}

		return query;
	}

	private Query ApplyGroupByConditions(Query query, IList<GroupByCondition> conds)
	{
		foreach (var gf in conds)
		{
			var field = _validator.RequireSafeIdentifier(gf.Field, "group by field");
			query = query.GroupBy(field);
		}

		return query;
	}

	private Query ApplyHavingConditions(Query query, IList<HavingCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field)) continue;
			query = ApplyHavingCondition(query, cond);
		}

		return query;
	}

	private Query ApplyOrderByColumns(Query query, IList<OrderByCondition> cols)
	{
		foreach (var col in cols)
		{
			if (string.IsNullOrWhiteSpace(col.Field)) continue;

			var field = col.Field;
			var direction = string.Equals(col.Direction, "DESC", StringComparison.OrdinalIgnoreCase)
				? "DESC"
				: "ASC";

			if (!string.IsNullOrWhiteSpace(col.Aggregation) && _validator.IsSupportedAggregation(col.Aggregation))
			{
				var agg = _validator.RequireSafeAggregation(col.Aggregation);
				var safeField = _validator.RequireSafeIdentifier(field, "order by aggregation field");
				query = query.OrderByRaw($"{agg}({safeField}) {direction}");
			}
			else
			{
				query = direction == "DESC"
					? query.OrderByDesc(field)
					: query.OrderBy(field);
			}
		}

		return query;
	}
	#endregion
	#region ExecuteQueryAsync

	public async Task<string> ExecuteQueryAsync(
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

		if (inWhereConditions?.Count > 0)
			query = ApplyInWhereConditions(query, inWhereConditions);

		if (stringWhereConditions?.Count > 0)
			query = ApplyStringWhereConditions(query, stringWhereConditions);

		if (groupByConditions?.Count > 0)
			query = ApplyGroupByConditions(query, groupByConditions);

		if (havingConditions?.Count > 0)
			query = ApplyHavingConditions(query, havingConditions);

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
				var combineType = Regex.Replace(
					(combine.Type ?? "union").Trim().ToLowerInvariant(),
					@"\s+",
					" ");
				query = combineType switch
				{
					"union all" or "union_all" or "unionall" => query.Union(sub, all: true),
					"intersect" => query.Intersect(sub),
					"except" => query.Except(sub),
					_ => query.Union(sub),
				};
			}
		}

		if (orderByColumns?.Count > 0)
			query = ApplyOrderByColumns(query, orderByColumns);

		var hasCombineConditions = combineConditions?.Count > 0;
		if (limit > 0)
		{
			query = hasCombineConditions
				? new Query().From(query.As("combined_result")).Limit(limit.Value)
				: query.Limit(limit.Value);
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

		if (definition.InWhereConditions?.Count > 0)
			query = ApplyInWhereConditions(query, definition.InWhereConditions);

		if (definition.StringWhereConditions?.Count > 0)
			query = ApplyStringWhereConditions(query, definition.StringWhereConditions);

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
	#region Having Helpers

	private Query ApplyHavingCondition(Query query, HavingCondition cond)
	{
		var havingOp = _validator.GetSafeOperator(cond.Operator).ToLowerInvariant();

		if (havingOp is "between" or "not between")
		{
			if (!_valueParser.TryGetBetweenValues(cond.Value, out var start, out var end))
				return query;

			var expression = BuildHavingExpression(cond);
			var betweenOperator = havingOp == "not between" ? "NOT BETWEEN" : "BETWEEN";
			return query.HavingRaw($"{expression} {betweenOperator} ? AND ?", start, end);
		}

		var havingValue = cond.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : cond.Value;
		var displayOperator = _validator.GetSafeOperator(cond.Operator);

		if (_validator.IsSupportedAggregation(cond.Aggregation))
		{
			var agg = _validator.RequireSafeAggregation(cond.Aggregation);
			var field = _validator.RequireSafeIdentifier(cond.Field, "having aggregation field");
			return query.HavingRaw($"{agg}({field}) {displayOperator} ?", havingValue);
		}

		return query.Having(cond.Field, displayOperator, havingValue);
	}

	private string BuildHavingExpression(HavingCondition cond)
	{
		if (_validator.IsSupportedAggregation(cond.Aggregation))
		{
			var agg = _validator.RequireSafeAggregation(cond.Aggregation);
			var field = _validator.RequireSafeIdentifier(cond.Field, "having expression field");
			return $"{agg}({field})";
		}

		// BETWEEN on a plain column: validate as identifier (no raw expressions allowed)
		return _validator.RequireSafeIdentifier(cond.Field, "having between field");
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