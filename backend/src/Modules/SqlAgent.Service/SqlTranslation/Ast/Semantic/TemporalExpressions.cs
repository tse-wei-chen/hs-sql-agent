using SqlAgent.Service.Models;

namespace SqlAgent.Service.SqlTranslation.Ast.Semantic;

public enum SqlDatePart
{
    Day,
    Week,
    Month,
    Quarter,
    Year,
    Hour,
    Minute,
    Second
}

public sealed class DateAddExpression : SelectCondition
{
    public required SqlDatePart Unit { get; init; }
    public required SelectCondition Amount { get; init; }
    public required SelectCondition Value { get; init; }
}

public sealed class DateDiffExpression : SelectCondition
{
    public required SqlDatePart Unit { get; init; }
    public required SelectCondition Start { get; init; }
    public required SelectCondition End { get; init; }
}
