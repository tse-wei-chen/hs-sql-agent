namespace HsSqlAgent.SqlCore.SqlTranslation.Ast.Semantic;

public sealed class DateFormatExpression : SelectCondition
{
    public required SelectCondition Value { get; init; }
    public required IReadOnlyList<DateFormatPart> Format { get; init; }
}

public sealed class FormattedDateParseExpression : SelectCondition
{
    public required SelectCondition Value { get; init; }
    public required IReadOnlyList<DateFormatPart> Format { get; init; }
}

public sealed class PositionExpression : SelectCondition
{
    public required SelectCondition Haystack { get; init; }
    public required SelectCondition Needle { get; init; }
}

public sealed class DatePartExpression : SelectCondition
{
    public required SqlDatePart Part { get; init; }
    public required SelectCondition Value { get; init; }
}
