using System.Text.RegularExpressions;
using SqlAgent.Service.Interfaces;

namespace SqlAgent.Service.Services;

public class SqlQueryValidator : IValidator
{
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

	public bool IsSupportedAggregation(string? aggregation) =>
		!string.IsNullOrWhiteSpace(aggregation) && AllowedAggregations.Contains(aggregation);

	public bool IsAllowedJoinType(string? joinType) =>
		!string.IsNullOrWhiteSpace(joinType) && AllowedJoinTypes.Contains(joinType.Trim());

	public string RequireSafeIdentifier(string? identifier, string context)
	{
		if (!IsSafeIdentifier(identifier))
			throw new InvalidOperationException($"Unsafe {context} identifier: '{identifier}'");
		return identifier!;
	}

	public string RequireSafeAggregation(string aggregation)
	{
		if (!IsSupportedAggregation(aggregation))
			throw new InvalidOperationException($"Unsupported aggregation: '{aggregation}'");
		return aggregation.ToUpperInvariant();
	}

	public string GetSafeOperator(string? op, string fallback = "=")
	{
		var trimmed = (op ?? fallback).Trim();
		if (!AllowedOperators.Contains(trimmed))
			throw new InvalidOperationException($"Unsupported operator: '{op}'");
		return trimmed;
	}

	private static bool IsSafeIdentifier(string? id)
	{
		if (string.IsNullOrWhiteSpace(id)) return false;
		if (id == "*") return true;
		return SafeIdentifierRegex.IsMatch(id);
	}
}
