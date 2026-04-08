namespace SqlAgent.Service.Interfaces;

public interface IValidator
{
	bool IsSupportedAggregation(string? aggregation);
	bool IsAllowedJoinType(string? joinType);
	string RequireSafeIdentifier(string? identifier, string context);
	string RequireSafeAggregation(string aggregation);
	string GetSafeOperator(string? op, string fallback = "=");
}
