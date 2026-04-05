using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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
		if (selectColumns != null && selectColumns.Any())
		{
			foreach (var col in selectColumns)
			{
				if (!string.IsNullOrWhiteSpace(col.Aggregation) && IsSupportedAggregation(col.Aggregation))
				{
					var alias = string.IsNullOrWhiteSpace(col.Alias) ? "" : $" AS {col.Alias}";
					query = query.SelectRaw($"{col.Aggregation.ToUpperInvariant()}({col.Field}){alias}");
				}
				else if (IsRawExpression(col.Field))
				{
					var alias = string.IsNullOrWhiteSpace(col.Alias) ? "" : $" AS {col.Alias}";
					query = query.SelectRaw($"{col.Field}{alias}");
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
				if (string.IsNullOrWhiteSpace(cond.Field))
					continue;

				var op = (cond.Operator ?? "=").Trim().ToLowerInvariant();
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

					query = query.Where(cond.Field, cond.Operator, value);
					continue;
				}

				query = query.Where(cond.Field, cond.Operator, value);
			}
		}
		if (dateWhereConditions != null)
		{
			foreach (var cond in dateWhereConditions)
			{
				if (string.IsNullOrWhiteSpace(cond.Field) || string.IsNullOrWhiteSpace(cond.Value))
					continue;

				var op = string.IsNullOrWhiteSpace(cond.Operator) ? "=" : cond.Operator;
				if (!TryToDateTime(cond.Value, out var dt))
					continue;

				query = query.WhereDate(cond.Field, op, dt);
			}
		}
		if (inWhereConditions != null)
		{
			foreach (var cond in inWhereConditions)
			{
				if (string.IsNullOrWhiteSpace(cond.Field) || cond.Values == null || cond.Values.Count == 0)
					continue;

				var values = cond.Values
					.Select(v => v is JsonElement je ? UnwrapJsonElement(je) : v)
					.Where(v => v is not null)
					.ToArray();

				if (values.Length == 0)
					continue;

				query = cond.NotIn
					? query.WhereNotIn(cond.Field, values)
					: query.WhereIn(cond.Field, values);
			}
		}
		if (stringWhereConditions != null)
		{
			foreach (var cond in stringWhereConditions)
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
		}
		if (groupByConditions != null && groupByConditions.Any())
		{
			var groupFields = groupByConditions
				.Where(g => !string.IsNullOrWhiteSpace(g.Field))
				.Select(condition => condition.Field)
				.ToArray();

			if (groupFields.Length > 0)
			{
				foreach (var gf in groupFields)
				{
					query = gf.Contains('(')
						? query.GroupByRaw(gf)
						: query.GroupBy(gf);
				}
			}
		}
		if (havingConditions != null)
		{
			foreach (var cond in havingConditions)
			{
				if (string.IsNullOrWhiteSpace(cond.Field))
					continue;

				var havingValue = cond.Value is JsonElement je ? UnwrapJsonElement(je) : cond.Value;
				var havingOp = string.IsNullOrWhiteSpace(cond.Operator) ? "=" : cond.Operator;

				if (!string.IsNullOrWhiteSpace(cond.Aggregation) && IsSupportedAggregation(cond.Aggregation))
				{
					query = query.HavingRaw($"{cond.Aggregation.ToUpperInvariant()}({cond.Field}) {havingOp} ?", havingValue);
				}
				else if (IsRawExpression(cond.Field))
				{
					query = query.HavingRaw($"{cond.Field} {havingOp} ?", havingValue);
				}
				else
				{
					query = query.Having(cond.Field, havingOp, havingValue);
				}
			}
		}

		if (cteConditions != null)
		{
			foreach (var cte in cteConditions)
			{
				if (string.IsNullOrWhiteSpace(cte.Name) || string.IsNullOrWhiteSpace(cte.Query?.TableName))
					continue;

				query = query.With(cte.Name, BuildQueryFromDefinition(cte.Query));
			}
		}

		if (combineConditions != null)
		{
			foreach (var combine in combineConditions)
			{
				if (string.IsNullOrWhiteSpace(combine.Query?.TableName))
					continue;

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
				else if (IsRawExpression(field))
				{
					query = query.OrderByRaw($"{field} {direction}");
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

		try
		{
			var result = await db.GetAsync(query, cancellationToken: cancellationToken);

			var resultList = result.Select(r => (IDictionary<string, object>)r).ToList();
			return JsonSerializer.Serialize(resultList);
		}
		catch (Exception ex)
		{
			var compiled = compiler.Compile(query);
			var code = TryExtractSqlStateCode(ex.Message);
			var bindings = compiled.Bindings?.Select(FormatBinding).ToArray() ?? Array.Empty<string>();
			var hint = BuildHint(code, ex.Message);
			var action = BuildNextAction(code, ex.Message);

			return $"Error executing query | code={code ?? "unknown"} | message={ex.Message} | sql={compiled.Sql} | bindings=[{string.Join(", ", bindings)}] | hint={hint} | nextAction={action}";
		}
	}

	private static string? TryExtractSqlStateCode(string message)
	{
		var match = Regex.Match(message ?? string.Empty, @"\b(?<code>[0-9A-Z]{5})\b");
		return match.Success ? match.Groups["code"].Value : null;
	}

	private static string FormatBinding(object? value)
	{
		if (value is null)
			return "null";

		return value switch
		{
			DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
			string s => $"\"{s}\"",
			_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty
		};
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

	private Query BuildQueryFromDefinition(QueryDefinition definition)
	{
		var query = new Query(definition.TableName);

		if (definition.SelectColumns != null && definition.SelectColumns.Any())
		{
			foreach (var col in definition.SelectColumns)
			{
				if (!string.IsNullOrWhiteSpace(col.Aggregation) && IsSupportedAggregation(col.Aggregation))
				{
					var alias = string.IsNullOrWhiteSpace(col.Alias) ? "" : $" AS {col.Alias}";
					query = query.SelectRaw($"{col.Aggregation.ToUpperInvariant()}({col.Field}){alias}");
				}
				else if (IsRawExpression(col.Field))
				{
					var alias = string.IsNullOrWhiteSpace(col.Alias) ? "" : $" AS {col.Alias}";
					query = query.SelectRaw($"{col.Field}{alias}");
				}
				else
					query = query.Select(col.Field);
			}
		}
		else
			query = query.Select("*");

		if (definition.Joins != null)
		{
			foreach (var join in definition.Joins)
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

		if (definition.WhereColumnsAndValues != null)
		{
			foreach (var cond in definition.WhereColumnsAndValues)
			{
				if (string.IsNullOrWhiteSpace(cond.Field))
					continue;

				var op = (cond.Operator ?? "=").Trim().ToLowerInvariant();
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

					query = query.Where(cond.Field, cond.Operator, value);
					continue;
				}

				query = query.Where(cond.Field, cond.Operator, value);
			}
		}

		if (definition.DateWhereConditions != null)
		{
			foreach (var cond in definition.DateWhereConditions)
			{
				if (string.IsNullOrWhiteSpace(cond.Field) || string.IsNullOrWhiteSpace(cond.Value))
					continue;

				var op = string.IsNullOrWhiteSpace(cond.Operator) ? "=" : cond.Operator;
				if (!TryToDateTime(cond.Value, out var dt))
					continue;

				query = query.WhereDate(cond.Field, op, dt);
			}
		}

		if (definition.InWhereConditions != null)
		{
			foreach (var cond in definition.InWhereConditions)
			{
				if (string.IsNullOrWhiteSpace(cond.Field) || cond.Values == null || cond.Values.Count == 0)
					continue;

				var values = cond.Values
					.Select(v => v is JsonElement je ? UnwrapJsonElement(je) : v)
					.Where(v => v is not null)
					.ToArray();

				if (values.Length == 0)
					continue;

				query = cond.NotIn
					? query.WhereNotIn(cond.Field, values)
					: query.WhereIn(cond.Field, values);
			}
		}

		if (definition.StringWhereConditions != null)
		{
			foreach (var cond in definition.StringWhereConditions)
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
		}

		if (definition.GroupByConditions != null && definition.GroupByConditions.Any())
		{
			var groupFields = definition.GroupByConditions
				.Where(g => !string.IsNullOrWhiteSpace(g.Field))
				.Select(condition => condition.Field)
				.ToArray();

			if (groupFields.Length > 0)
			{
				foreach (var gf in groupFields)
				{
					query = gf.Contains('(')
						? query.GroupByRaw(gf)
						: query.GroupBy(gf);
				}
			}
		}

		if (definition.HavingConditions != null)
		{
			foreach (var cond in definition.HavingConditions)
			{
				if (string.IsNullOrWhiteSpace(cond.Field))
					continue;

				var havingValue = cond.Value is JsonElement je ? UnwrapJsonElement(je) : cond.Value;
				var havingOp = string.IsNullOrWhiteSpace(cond.Operator) ? "=" : cond.Operator;

				if (!string.IsNullOrWhiteSpace(cond.Aggregation) && IsSupportedAggregation(cond.Aggregation))
				{
					query = query.HavingRaw($"{cond.Aggregation.ToUpperInvariant()}({cond.Field}) {havingOp} ?", havingValue);
				}
				else if (IsRawExpression(cond.Field))
				{
					query = query.HavingRaw($"{cond.Field} {havingOp} ?", havingValue);
				}
				else
				{
					query = query.Having(cond.Field, havingOp, havingValue);
				}
			}
		}

		if (definition.OrderByColumns != null && definition.OrderByColumns.Any())
		{
			foreach (var col in definition.OrderByColumns)
			{
				if (string.IsNullOrWhiteSpace(col.Field))
					continue;

				var field = col.Field;
				var direction = string.Equals(col.Direction, "DESC", StringComparison.OrdinalIgnoreCase)
					? "DESC"
					: "ASC";

				if (!string.IsNullOrWhiteSpace(col.Aggregation) && IsSupportedAggregation(col.Aggregation))
				{
					query = query.OrderByRaw($"{col.Aggregation.ToUpperInvariant()}({field}) {direction}");
				}
				else if (IsRawExpression(field))
				{
					query = query.OrderByRaw($"{field} {direction}");
				}
				else
				{
					query = direction == "DESC"
						? query.OrderByDesc(field)
						: query.OrderBy(field);
				}
			}
		}

		if (definition.Limit != null && definition.Limit > 0)
			query = query.Limit(definition.Limit.Value);

		return query;
	}

	private static bool IsSupportedAggregation(string aggregation)
	{
		return aggregation.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";
	}

	private static bool IsRawExpression(string field)
	{
		if (string.IsNullOrWhiteSpace(field))
			return false;

		return field.IndexOfAny(['(', ')', ' ', '+', '-', '*', '/', ',']) >= 0;
	}

	private static object UnwrapJsonElement(JsonElement je)
	{
		var raw = je.ValueKind switch
		{
			JsonValueKind.String => (object)je.GetString()!,
			JsonValueKind.Number when je.TryGetInt64(out var l) => l,
			JsonValueKind.Number => je.GetDouble(),
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => (object)je.ToString(),
		};

		return raw;
	}

	private static bool TryToDateTime(object? value, out DateTime dateTime)
	{
		dateTime = default;
		if (value is null)
			return false;

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
		values = Array.Empty<object>();

		if (value is null)
			return false;

		if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
		{
			values = je.EnumerateArray().Select(x => UnwrapJsonElement(x)).Cast<object>().ToArray();
			return values.Any();
		}

		if (value is JsonElement jeText && jeText.ValueKind == JsonValueKind.String)
		{
			return TryGetInValues(jeText.GetString(), out values);
		}

		if (value is IEnumerable<object> objEnum)
		{
			var arr = objEnum.Where(v => v is not null).ToArray();
			if (arr.Length == 0)
				return false;

			values = arr;
			return true;
		}

		if (value is string str)
		{
			var trimmed = str.Trim();
			if (trimmed.StartsWith("(") && trimmed.EndsWith(")"))
				trimmed = trimmed[1..^1];
			if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
				trimmed = trimmed[1..^1];

			var parts = trimmed
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(p => p.Trim('\'', '"'))
				.Where(p => !string.IsNullOrWhiteSpace(p))
				.Cast<object>()
				.ToArray();

			if (parts.Length == 0)
				return false;

			values = parts;
			return true;
		}

		return false;
	}

	public abstract Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetTablesAsync(string connectionString, CancellationToken cancellationToken = default);
	public abstract Task<List<string>> GetColumnsAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
	public abstract Task<string> GetTableReferenceAsync(string connectionString, string tableName, CancellationToken cancellationToken = default);
}