using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlKata;
using SqlKata.Compilers;
using SqlKata.Execution;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Strategies;

public abstract class BaseSqlStrategy : ISqlStrategy
{
	public abstract SqlAgentToolType DbType { get; }

	protected abstract DbConnection CreateConnection(string? connectionString);
	protected abstract Compiler CreateCompiler();

	#region Security Guards
	private static readonly Regex SafeIdentifierRegex =
		new(@"^[a-zA-Z_][a-zA-Z0-9_]*(\.[a-zA-Z_][a-zA-Z0-9_]*)*$", RegexOptions.Compiled);

	private static readonly HashSet<string> AllowedAggregations =
		new(StringComparer.OrdinalIgnoreCase) { "COUNT", "SUM", "AVG", "MIN", "MAX" };

	private static readonly HashSet<string> AllowedOperators =
		new(StringComparer.OrdinalIgnoreCase)
		{
			"=", "!=", "<>", ">", "<", ">=", "<=",
			"like", "ilike", "not like",
			"is", "is null", "is not", "is not null",
			"in", "not in",
			"between", "not between"
		};

	private static readonly HashSet<string> AllowedJoinTypes =
		new(StringComparer.OrdinalIgnoreCase) { "inner", "left", "right", "cross" };

	private static bool IsSafeIdentifier(string? id)
	{
		if (string.IsNullOrWhiteSpace(id)) return false;
		if (id == "*") return true;
		return SafeIdentifierRegex.IsMatch(id);
	}

	private static string RequireSafeIdentifier(string? id, string context)
	{
		if (!IsSafeIdentifier(id))
			throw new InvalidOperationException($"Unsafe {context} identifier: '{id}'");
		return id!;
	}

	private static bool IsSupportedAggregation(string? agg) =>
		!string.IsNullOrWhiteSpace(agg) && AllowedAggregations.Contains(agg);

	private static string RequireSafeAggregation(string agg)
	{
		if (!IsSupportedAggregation(agg))
			throw new InvalidOperationException($"Unsupported aggregation: '{agg}'");
		return agg.ToUpperInvariant();
	}

	private static string GetSafeOperator(string? op, string fallback = "=")
	{
		var trimmed = (op ?? fallback).Trim();
		if (!AllowedOperators.Contains(trimmed))
			throw new InvalidOperationException($"Unsupported operator: '{op}'");
		return trimmed;
	}
	#endregion
	#region Shared Query Builders

	private static Query ApplySelectColumns(Query query, IList<SelectCondition> cols)
	{
		if (cols.Count == 0) return query.Select("*");

		foreach (var col in cols)
		{
			if (!string.IsNullOrWhiteSpace(col.Aggregation))
			{
				var agg = RequireSafeAggregation(col.Aggregation);
				var field = RequireSafeIdentifier(col.Field, "select aggregation field");
				if (!string.IsNullOrWhiteSpace(col.Alias))
				{
					var alias = RequireSafeIdentifier(col.Alias, "select aggregation alias");
					query = query.SelectRaw($"{agg}({field}) AS {alias}");
				}
				else
				{
					query = query.SelectRaw($"{agg}({field})");
				}
			}
			else if (!string.IsNullOrWhiteSpace(col.Alias))
			{
				query = query.Select($"{col.Field} AS {col.Alias}");
			}
			else
			{
				query = query.Select(col.Field);
			}
		}

		return query;
	}

	private static Query ApplyJoins(Query query, IList<JoinCondition> joins)
	{
		foreach (var join in joins)
		{
			if (string.IsNullOrEmpty(join.Table) || string.IsNullOrEmpty(join.First)
				|| string.IsNullOrEmpty(join.Second) || string.IsNullOrEmpty(join.Type))
				continue;

			var joinType = join.Type.Trim().ToLowerInvariant();
			if (!AllowedJoinTypes.Contains(joinType))
				throw new InvalidOperationException($"Unsupported join type: '{join.Type}'");

			var first = RequireSafeIdentifier(join.First, "join first");
			var second = RequireSafeIdentifier(join.Second, "join second");
			var op = GetSafeOperator(join.Operator);
			var table = RequireSafeIdentifier(join.Table, "join table");

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

	private static Query ApplyWhereConditions(Query query, IList<WhereCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field)) continue;

			var op = GetSafeOperator(cond.Operator).ToLowerInvariant();
			var rawValue = cond.Value;
			var value = rawValue is JsonElement je ? UnwrapJsonElement(je) : rawValue;

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
				if (TryGetInValues(rawValue, out var values))
				{
					query = op == "in"
						? query.WhereIn(cond.Field, values)
						: query.WhereNotIn(cond.Field, values);
					continue;
				}
			}

			query = query.Where(cond.Field, GetSafeOperator(cond.Operator), value);
		}

		return query;
	}

	private static Query ApplyDateWhereConditions(Query query, IList<DateWhereCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field) || string.IsNullOrWhiteSpace(cond.Value))
				continue;

			var op = GetSafeOperator(cond.Operator);
			if (!TryToDateTime(cond.Value, out var dt)) continue;

			query = query.WhereDate(cond.Field, op, dt);
		}

		return query;
	}

	private static Query ApplyInWhereConditions(Query query, IList<InWhereCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field) || cond.Values == null || cond.Values.Count == 0)
				continue;

			var values = cond.Values
				.Select(v => v is JsonElement je ? UnwrapJsonElement(je) : v)
				.Where(v => v is not null)
				.ToArray();

			if (values.Length == 0) continue;

			query = cond.NotIn
				? query.WhereNotIn(cond.Field, values)
				: query.WhereIn(cond.Field, values);
		}

		return query;
	}

	private static Query ApplyStringWhereConditions(Query query, IList<StringWhereCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field) || string.IsNullOrWhiteSpace(cond.Value))
				continue;

			var mode = (cond.MatchMode ?? "contains").Trim().ToLowerInvariant();
			var likeOperator = cond.CaseInsensitive ? "ilike" : "like";
			var pattern = mode switch
			{
				"starts" => $"{cond.Value}%",
				"ends" => $"%{cond.Value}",
				"like" => cond.Value,
				_ => $"%{cond.Value}%"
			};

			query = query.Where(cond.Field, likeOperator, pattern);
		}

		return query;
	}

	private static Query ApplyGroupByConditions(Query query, IList<GroupByCondition> conds)
	{
		foreach (var gf in conds)
		{
			if (!string.IsNullOrWhiteSpace(gf.Aggregation))
			{
				// Both agg and field go through strict whitelist guards before entering GroupByRaw
				var agg = RequireSafeAggregation(gf.Aggregation);
				var field = RequireSafeIdentifier(gf.Field, "group by aggregation field");
				query = query.GroupByRaw($"{agg}({field})");
			}
			else
			{
				RequireSafeIdentifier(gf.Field, "group by field");
				query = query.GroupBy(gf.Field);
			}
		}

		return query;
	}

	private static Query ApplyHavingConditions(Query query, IList<HavingCondition> conds)
	{
		foreach (var cond in conds)
		{
			if (string.IsNullOrWhiteSpace(cond.Field)) continue;
			query = ApplyHavingCondition(query, cond);
		}

		return query;
	}

	private static Query ApplyOrderByColumns(Query query, IList<OrderByCondition> cols)
	{
		foreach (var col in cols)
		{
			if (string.IsNullOrWhiteSpace(col.Field)) continue;

			var field = col.Field;
			var direction = string.Equals(col.Direction, "DESC", StringComparison.OrdinalIgnoreCase)
				? "DESC"
				: "ASC";

			if (!string.IsNullOrWhiteSpace(col.Aggregation) && IsSupportedAggregation(col.Aggregation))
			{
				var agg = RequireSafeAggregation(col.Aggregation);
				var safeField = RequireSafeIdentifier(field, "order by aggregation field");
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
				var combineType = (combine.Type ?? "union").Trim().ToLowerInvariant();
				query = combineType switch
				{
					"union all" => query.UnionAll(sub),
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
			var resultList = result.Select(r => (IDictionary<string, object>)r).ToList();
			return JsonSerializer.Serialize(resultList);
		}
		catch (Exception ex)
		{
			var code = TryExtractSqlStateCode(ex.Message);
			var hint = BuildHint(code, ex.Message);
			var action = BuildNextAction(code, ex.Message);

			return $"Error executing query | code={code ?? "unknown"} | hint={hint} | nextAction={action}";
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

	private static Query ApplyHavingCondition(Query query, HavingCondition cond)
	{
		var havingOp = GetSafeOperator(cond.Operator).ToLowerInvariant();

		if (havingOp is "between" or "not between")
		{
			if (!TryGetBetweenValues(cond.Value, out var start, out var end))
				return query;

			var expression = BuildHavingExpression(cond);
			var betweenOperator = havingOp == "not between" ? "NOT BETWEEN" : "BETWEEN";
			return query.HavingRaw($"{expression} {betweenOperator} ? AND ?", start, end);
		}

		var havingValue = cond.Value is JsonElement je ? UnwrapJsonElement(je) : cond.Value;
		var displayOperator = GetSafeOperator(cond.Operator);

		if (IsSupportedAggregation(cond.Aggregation))
		{
			var agg = RequireSafeAggregation(cond.Aggregation);
			var field = RequireSafeIdentifier(cond.Field, "having aggregation field");
			return query.HavingRaw($"{agg}({field}) {displayOperator} ?", havingValue);
		}

		return query.Having(cond.Field, displayOperator, havingValue);
	}

	private static string BuildHavingExpression(HavingCondition cond)
	{
		if (IsSupportedAggregation(cond.Aggregation))
		{
			var agg = RequireSafeAggregation(cond.Aggregation);
			var field = RequireSafeIdentifier(cond.Field, "having expression field");
			return $"{agg}({field})";
		}

		// BETWEEN on a plain column: validate as identifier (no raw expressions allowed)
		return RequireSafeIdentifier(cond.Field, "having between field");
	}
	#endregion
	#region Error Helpers

	private static string? TryExtractSqlStateCode(string message)
	{
		var match = Regex.Match(message ?? string.Empty, @"\b(?<code>[0-9A-Z]{5})\b");
		return match.Success ? match.Groups["code"].Value : null;
	}

	private static string BuildHint(string? code, string message)
	{
		if (string.Equals(code, "42883", StringComparison.OrdinalIgnoreCase))
		{
			if (message.Contains("date >= text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date <= text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date < text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date > text", StringComparison.OrdinalIgnoreCase))
			{
				return "Date vs text type mismatch. Retry with dateWhereConditions.";
			}

			return "Operator/type mismatch. Retry with a compatible operator and typed values. Migration tip: use inWhereConditions for IN/NOT IN cases.";
		}

		if (string.Equals(code, "42703", StringComparison.OrdinalIgnoreCase))
			return "Column/expression not recognized. If using SQL expressions (e.g. date_part(...)), pass the full expression and avoid quoting it as a plain column name.";

		if (string.Equals(code, "42702", StringComparison.OrdinalIgnoreCase))
			return "Ambiguous column reference. Qualify fields with table prefixes, for example order_details.unit_price instead of unit_price.";

		if (string.Equals(code, "22P02", StringComparison.OrdinalIgnoreCase))
			return "Invalid value format for column type. Retry with correct literal format.";

		return "Use the SQL and bindings to adjust fields/operators/types, then retry.";
	}

	private static string BuildNextAction(string? code, string message)
	{
		if (string.Equals(code, "42883", StringComparison.OrdinalIgnoreCase)
			&& (message.Contains("date >= text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date <= text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date < text", StringComparison.OrdinalIgnoreCase)
				|| message.Contains("date > text", StringComparison.OrdinalIgnoreCase)))
		{
			return "Retry and add dateWhereConditions. Example: { field: 'order_date', operator: '>=', value: '1997-01-01' }.";
		}

		if (string.Equals(code, "42702", StringComparison.OrdinalIgnoreCase))
			return "Retry by qualifying ambiguous columns with table names, for example 'order_details.unit_price'.";

		if (string.Equals(code, "42703", StringComparison.OrdinalIgnoreCase))
			return "Retry with an existing column or pass expression as raw field text, for example date_part('year', order_date).";

		if (string.Equals(code, "22P02", StringComparison.OrdinalIgnoreCase))
			return "Retry with corrected literal format, for example number without quotes, ISO date string, or boolean true/false.";

		return "Retry after adjusting query fields according to the SQL error. Prefer specialized params: dateWhereConditions and inWhereConditions.";
	}
	#endregion
	#region Value Parsers

	private static bool TryGetBetweenValues(object? rawValue, out object? start, out object? end)
	{
		start = null;
		end = null;

		if (rawValue is JsonElement je)
		{
			if (je.ValueKind == JsonValueKind.Array && je.GetArrayLength() >= 2)
			{
				start = UnwrapJsonElement(je[0]);
				end = UnwrapJsonElement(je[1]);
				return true;
			}

			if (je.ValueKind == JsonValueKind.String)
				rawValue = je.GetString();
		}

		if (rawValue is IEnumerable<object?> enumerable)
		{
			var values = enumerable.Where(v => v is not null).Take(2).ToArray();
			if (values.Length == 2)
			{
				start = values[0];
				end = values[1];
				return true;
			}
		}

		if (rawValue is string s)
		{
			var trimmed = s.Trim();
			if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) trimmed = trimmed[1..^1];
			if (trimmed.StartsWith('(') && trimmed.EndsWith(')')) trimmed = trimmed[1..^1];

			var parts = trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2)
			{
				start = ConvertLiteral(parts[0]);
				end = ConvertLiteral(parts[1]);
				return true;
			}
		}

		return false;
	}

	private static object ConvertLiteral(string value)
	{
		if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
			return longVal;

		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleVal))
			return doubleVal;

		if (bool.TryParse(value, out var boolVal))
			return boolVal;

		return value.Trim().Trim('\'', '"');
	}

	private static object UnwrapJsonElement(JsonElement je)
	{
		return je.ValueKind switch
		{
			JsonValueKind.String => (object)je.GetString()!,
			JsonValueKind.Number when je.TryGetInt64(out var l) => l,
			JsonValueKind.Number => je.GetDouble(),
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => (object)je.ToString(),
		};
	}

	private static bool TryToDateTime(object? value, out DateTime dateTime)
	{
		dateTime = default;
		if (value is null) return false;

		if (value is DateTime dt)
		{
			dateTime = dt;
			return true;
		}

		var text = Convert.ToString(value, CultureInfo.InvariantCulture);
		return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dateTime);
	}

	private static bool TryGetInValues(object? value, out IEnumerable<object> values)
	{
		values = [];

		if (value is null) return false;

		if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
		{
			values = je.EnumerateArray().Select(UnwrapJsonElement).Cast<object>().ToArray();
			return values.Any();
		}

		if (value is JsonElement jeText && jeText.ValueKind == JsonValueKind.String)
			return TryGetInValues(jeText.GetString(), out values);

		if (value is IEnumerable<object> objEnum)
		{
			var arr = objEnum.Where(v => v is not null).ToArray();
			if (arr.Length == 0) return false;
			values = arr;
			return true;
		}

		if (value is string str)
		{
			var trimmed = str.Trim();
			if (trimmed.StartsWith("(") && trimmed.EndsWith(")")) trimmed = trimmed[1..^1];
			if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) trimmed = trimmed[1..^1];

			var parts = trimmed
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(p => p.Trim('\'', '"'))
				.Where(p => !string.IsNullOrWhiteSpace(p))
				.Cast<object>()
				.ToArray();

			if (parts.Length == 0) return false;
			values = parts;
			return true;
		}

		return false;
	}

	#endregion

	#region Abstract Members

	public abstract Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetTablesAsync(string connectionString, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
	public abstract Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);

	#endregion
}