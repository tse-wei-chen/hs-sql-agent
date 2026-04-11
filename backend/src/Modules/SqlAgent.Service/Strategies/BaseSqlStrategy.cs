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
			else if (col.CaseWhen?.Count > 0)
			{
				columnExpr = MapCaseWhen(col.CaseWhen, col.ElseValue);
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
				columnExpr.Alias = col.Alias;
			}

			query = query.Select(columnExpr);
		}

		return query;
	}

	private AbstractColumn MapCaseWhen(List<SqlAgent.Service.Models.CaseWhenClause> cases, object? elseValue)
	{
		var caseCol = new CaseColumn();
		foreach (var c in cases)
		{
			var whenQuery = new Query();
			ApplySingleWhere(whenQuery, c.Condition);
			caseCol.Cases.Add(new SqlKata.CaseWhenClause
			{
				ConditionQuery = whenQuery,
				Value = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value
			});
		}
		caseCol.ElseValue = elseValue is JsonElement eje ? _valueParser.UnwrapJsonElement(eje) : elseValue;
		return caseCol;
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
		foreach (var c in conds)
		{
			query = ApplySingleWhere(query, c);
		}
		return query;
	}

	private Query ApplySingleWhere(Query query, WhereCondition c)
	{
		if (c.Groups?.Count > 0)
		{
			return c.IsOr
				? query.OrWhere(q => ApplyWhereConditions(q, c.Groups))
				: query.Where(q => ApplyWhereConditions(q, c.Groups));
		}

		if (string.IsNullOrWhiteSpace(c.Field) && c.SubQuery == null) return query;

		var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
		var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;

		// Subquery handling (EXISTS, IN)
		if (c.SubQuery != null)
		{
			var sub = BuildQueryFromDefinition(c.SubQuery);
			return op switch
			{
				"exists" => c.IsOr 
                    ? (c.IsNot ? query.OrWhereNotExists(sub) : query.OrWhereExists(sub)) 
                    : (c.IsNot ? query.WhereNotExists(sub) : query.WhereExists(sub)),
				"notexists" => c.IsOr 
                    ? (c.IsNot ? query.OrWhereExists(sub) : query.OrWhereNotExists(sub)) 
                    : (c.IsNot ? query.WhereExists(sub) : query.WhereNotExists(sub)),
				"in" => c.IsOr 
                    ? (c.IsNot ? query.OrWhereNotIn(c.Field, sub) : query.OrWhereIn(c.Field, sub)) 
                    : (c.IsNot ? query.WhereNotIn(c.Field, sub) : query.WhereIn(c.Field, sub)),
				"notin" => c.IsOr 
                    ? (c.IsNot ? query.OrWhereIn(c.Field, sub) : query.OrWhereNotIn(c.Field, sub)) 
                    : (c.IsNot ? query.WhereIn(c.Field, sub) : query.WhereNotIn(c.Field, sub)),
				_ => query
			};
		}

		// Date handling
		if (c.IsDate)
		{
			var dtVal = _valueParser.TryToDateTime(val, out var dt) ? dt : val;
			return op switch
			{
				"is" or "isnull" => c.IsOr ? query.OrWhereDate(c.Field, dtVal) : query.WhereDate(c.Field, dtVal),
				"isnot" or "isnotnull" => c.IsOr ? query.OrWhereNotDate(c.Field, dtVal) : query.WhereNotDate(c.Field, dtVal),

				"in" or "notin" when _valueParser.TryGetInValues(val, out var ins)
					=> ApplyDateIn(query, c, op, ins),

				"between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high)
					=> ApplyDateBetween(query, c, op, low, high),

				_ => c.IsOr ? query.OrWhereDate(c.Field, op, dtVal) : query.WhereDate(c.Field, op, dtVal)
			};
		}

		// Standard handling
		Func<Query, Query> apply = q =>
		{
			return op switch
			{
				"is" or "isnull" => q.Where(c.Field, val),
				"isnot" or "isnotnull" => q.WhereNot(c.Field, val),

				"in" or "notin" when _valueParser.TryGetInValues(val, out var ins)
					=> op == "in" ? q.WhereIn(c.Field, ins) : q.WhereNotIn(c.Field, ins),
				
				"in" or "notin" when c.Values != null
					=> op == "in" ? q.WhereIn(c.Field, c.Values) : q.WhereNotIn(c.Field, c.Values),

				"between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high)
					=> op == "between" ? q.WhereBetween(c.Field, low, high) : q.WhereNotBetween(c.Field, low, high),

				"like" => q.WhereLike(c.Field, val),
				"notlike" => q.WhereNotLike(c.Field, val),
				"starts" => q.WhereStarts(c.Field, val),
				"ends" => q.WhereEnds(c.Field, val),
				"contains" => q.WhereContains(c.Field, val),

				_ => q.Where(c.Field, op, val)
			};
		};

		if (c.IsOr) return query.OrWhere(q => apply(q));
		if (c.IsNot) return query.Not().Where(q => apply(q));
		return query.Where(q => apply(q));
	}

	private Query ApplyDateIn(Query query, WhereCondition c, string op, IEnumerable<object> ins)
	{
		var dtIns = ins.Select(i => _valueParser.TryToDateTime(i, out var d) ? (object)d : i).ToList();
		return op == "in"
			? (c.IsOr ? query.OrWhereDateIn(c.Field, dtIns) : query.WhereDateIn(c.Field, dtIns))
			: (c.IsOr ? query.OrWhereDateNotIn(c.Field, dtIns) : query.WhereDateNotIn(c.Field, dtIns));
	}

	private Query ApplyDateBetween(Query query, WhereCondition c, string op, object? low, object? high)
	{
		var lowDt = _valueParser.TryToDateTime(low, out var d1) ? (object)d1 : low;
		var highDt = _valueParser.TryToDateTime(high, out var d2) ? (object)d2 : high;
		return op == "between"
			? (c.IsOr ? query.OrWhereDateBetween(c.Field, lowDt, highDt) : query.WhereDateBetween(c.Field, lowDt, highDt))
			: (c.IsOr ? query.OrWhereDateNotBetween(c.Field, lowDt, highDt) : query.WhereDateNotBetween(c.Field, lowDt, highDt));
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
		foreach (var c in conds)
		{
			query = ApplySingleHaving(query, c);
		}
		return query;
	}

	private Query ApplySingleHaving(Query query, HavingCondition c)
	{
		if (c.Groups?.Count > 0)
		{
			return c.IsOr
				? query.OrHaving(q => ApplyHavingConditions(q, c.Groups))
				: query.Having(q => ApplyHavingConditions(q, c.Groups));
		}

		if (string.IsNullOrWhiteSpace(c.Field)) return query;

		var op = (c.Operator ?? "=").ToLowerInvariant().Replace(" ", "").Trim();
		var val = c.Value is JsonElement je ? _valueParser.UnwrapJsonElement(je) : c.Value;
		var agg = c.Aggregation;
		var isAgg = !string.IsNullOrWhiteSpace(agg);
		var field = c.Field.Trim();

		if (c.IsDate)
		{
			var dtVal = _valueParser.TryToDateTime(val, out var dt) ? dt : val;
			return isAgg
				? op switch
				{
					"in" or "notin" when _valueParser.TryGetInValues(val, out var ins)
						=> ApplyDateInAggregate(query, c, agg, field, op, ins),

					"between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high)
						=> ApplyDateBetweenAggregate(query, c, agg, field, op, low, high),

					_ => (c.IsOr ? query.OrHavingDateAggregate(agg, field, op, dtVal) : query.HavingDateAggregate(agg, field, op, dtVal))
				}
				: op switch
				{
					"in" or "notin" when _valueParser.TryGetInValues(val, out var ins)
						=> ApplyDateInHaving(query, c, field, op, ins),

					"between" or "notbetween" when _valueParser.TryGetRangeValues(val, out var low, out var high)
						=> ApplyDateBetweenHaving(query, c, field, op, low, high),

					_ => (c.IsOr ? query.OrHavingDate(field, op, dtVal) : query.HavingDate(field, op, dtVal))
				};
		}

		Func<Query, Query> apply = q =>
		{
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

				_ => isAgg ? q.HavingAggregate(agg, field, op, val) : q.Having(field, op, val)
			};
		};

		if (c.IsOr) return query.OrHaving(q => apply(q));
		if (c.IsNot) return query.Not().Having(q => apply(q));
		return query.Having(q => apply(q));
	}

	private Query ApplyDateInAggregate(Query query, HavingCondition c, string agg, string field, string op, IEnumerable<object> ins)
	{
		var dtIns = ins.Select(i => _valueParser.TryToDateTime(i, out var d) ? (object)d : i).ToList();
		return op == "in"
			? (c.IsOr ? query.Or().HavingDateInAggregate(agg, field, dtIns) : query.HavingDateInAggregate(agg, field, dtIns))
			: (c.IsOr ? query.Or().Not().HavingDateInAggregate(agg, field, dtIns) : query.Not().HavingDateInAggregate(agg, field, dtIns));
	}

	private Query ApplyDateBetweenAggregate(Query query, HavingCondition c, string agg, string field, string op, object? low, object? high)
	{
		var lowDt = _valueParser.TryToDateTime(low, out var d1) ? (object)d1 : low;
		var highDt = _valueParser.TryToDateTime(high, out var d2) ? (object)d2 : high;
		return op == "between"
			? (c.IsOr ? query.Or().HavingDateBetweenAggregate(agg, field, lowDt, highDt) : query.HavingDateBetweenAggregate(agg, field, lowDt, highDt))
			: (c.IsOr ? query.Or().Not().HavingDateBetweenAggregate(agg, field, lowDt, highDt) : query.Not().HavingDateBetweenAggregate(agg, field, lowDt, highDt));
	}

	private Query ApplyDateInHaving(Query query, HavingCondition c, string field, string op, IEnumerable<object> ins)
	{
		var dtIns = ins.Select(i => _valueParser.TryToDateTime(i, out var d) ? (object)d : i).ToList();
		return op == "in"
			? (c.IsOr ? query.Or().HavingDateIn(field, dtIns) : query.HavingDateIn(field, dtIns))
			: (c.IsOr ? query.Or().Not().HavingDateIn(field, dtIns) : query.Not().HavingDateIn(field, dtIns));
	}

	private Query ApplyDateBetweenHaving(Query query, HavingCondition c, string field, string op, object? low, object? high)
	{
		var lowDt = _valueParser.TryToDateTime(low, out var d1) ? (object)d1 : low;
		var highDt = _valueParser.TryToDateTime(high, out var d2) ? (object)d2 : high;
		return op == "between"
			? (c.IsOr ? query.Or().HavingDateBetween(field, lowDt, highDt) : query.HavingDateBetween(field, lowDt, highDt))
			: (c.IsOr ? query.Or().Not().HavingDateBetween(field, lowDt, highDt) : query.Not().HavingDateBetween(field, lowDt, highDt));
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
		List<OrderByCondition>? orderByColumns = null,
		List<GroupByCondition>? groupByConditions = null,
		List<HavingCondition>? havingConditions = null,
		List<CombineCondition>? combineConditions = null,
		List<CteCondition>? cteConditions = null,
		int? limit = null,
		int? offset = null,
		List<JoinCondition>? joins = null,
		QueryDefinition? fromQuery = null,
		bool distinct = false,
		CancellationToken cancellationToken = default)
	{
		var definition = new QueryDefinition
		{
			TableName = tableName ?? string.Empty,
			FromQuery = fromQuery,
			Distinct = distinct,
			SelectColumns = selectColumns,
			WhereColumnsAndValues = whereConditions,
			OrderByColumns = orderByColumns,
			GroupByConditions = groupByConditions,
			HavingConditions = havingConditions,
			Joins = joins,
			Limit = limit,
			Offset = offset,
			CombineConditions = combineConditions,
			CteConditions = cteConditions
		};

		using var connection = CreateConnection(connectionString);
		await connection.OpenAsync(cancellationToken);

		var compiler = CreateCompiler();
		var db = new QueryFactory(connection, compiler);
		var query = BuildQueryFromDefinition(definition);

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
		// 1. Determine Source (Table or Subquery)
		var query = definition.FromQuery != null 
			? new Query().From(BuildQueryFromDefinition(definition.FromQuery), definition.Alias) 
			: new Query(definition.TableName);

		if (!string.IsNullOrEmpty(definition.Alias) && definition.FromQuery == null)
		{
			query = query.As(definition.Alias);
		}

		// 2. Apply CTEs
		if (definition.CteConditions?.Count > 0)
		{
			foreach (var cte in definition.CteConditions)
			{
				if (string.IsNullOrWhiteSpace(cte.Name)) continue;
				query = query.With(cte.Name, BuildQueryFromDefinition(cte.Query));
			}
		}

		// 3. Apply Base Components
		if (definition.Distinct) query = query.Distinct();
		query = ApplySelectColumns(query, definition.SelectColumns ?? []);
		if (definition.Joins?.Count > 0) query = ApplyJoins(query, definition.Joins);
		if (definition.WhereColumnsAndValues?.Count > 0) query = ApplyWhereConditions(query, definition.WhereColumnsAndValues);
		if (definition.GroupByConditions?.Count > 0) query = ApplyGroupByConditions(query, definition.GroupByConditions);
		if (definition.HavingConditions?.Count > 0) query = ApplyHavingConditions(query, definition.HavingConditions);

		// 4. Handle Combines (UNION, INTERSECT, EXCEPT)
		if (definition.CombineConditions?.Count > 0)
		{
			foreach (var combine in definition.CombineConditions)
			{
				var sub = BuildQueryFromDefinition(combine.Query);
				var type = combine.Type?.ToLowerInvariant().Replace("_", "").Trim() ?? "union";
				query = type switch
				{
					"unionall" => query.Union(sub, all: true),
					"intersect" => query.Intersect(sub),
					"except" => query.Except(sub),
					_ => query.Union(sub)
				};
			}

			// 5. Wrapping for Post-Combine Operations
			// If we have ORDER BY or LIMIT/OFFSET after a combine, we MUST wrap it in a subquery
			// to ensure the operations apply to the combined set, not just the last branch.
			if (definition.OrderByColumns?.Count > 0 || (definition.Limit ?? 0) > 0 || (definition.Offset ?? 0) > 0)
			{
				var wrapper = new Query().From(query.As("combined_set"));
				if (definition.OrderByColumns?.Count > 0) 
					wrapper = ApplyOrderByColumns(wrapper, definition.OrderByColumns);
				if ((definition.Limit ?? 0) > 0) 
					wrapper = wrapper.Limit(definition.Limit!.Value);
				if ((definition.Offset ?? 0) > 0) 
					wrapper = wrapper.Offset(definition.Offset!.Value);
				
				return wrapper;
			}
		}
		else
		{
			// Standard No-Combine Operations
			if (definition.OrderByColumns?.Count > 0) 
				query = ApplyOrderByColumns(query, definition.OrderByColumns);
			if ((definition.Limit ?? 0) > 0) 
				query = query.Limit(definition.Limit!.Value);
			if ((definition.Offset ?? 0) > 0) 
				query = query.Offset(definition.Offset!.Value);
		}

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
		return "Retry after adjusting query fields according to the SQL error.";
	}
	#endregion

	#region Abstract Members

	public abstract Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
	public abstract Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);

	#endregion
}