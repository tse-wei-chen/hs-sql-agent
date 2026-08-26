namespace HsSqlAgent.SqlCore.SqlTranslation.Functions.Translators;

public sealed class TemporalFunctionTranslator : ISpecializedFunctionTranslator
{
    public bool CanTranslate(string functionName) =>
        functionName.Trim().Equals("DATEADD", StringComparison.OrdinalIgnoreCase)
        || functionName.Trim().Equals("DATEDIFF", StringComparison.OrdinalIgnoreCase);

    public SelectCondition Normalize(FunctionSelectCondition function, TranslationContext context)
    {
        var name = function.FunctionName.Trim().ToUpperInvariant();
        var arguments = function.Arguments ?? [];

        if (name == "DATEADD" && arguments.Count != 3)
            throw new InvalidOperationException("DATEADD requires exactly 3 arguments.");
        if (name == "DATEDIFF" && arguments.Count is not (2 or 3))
            throw new InvalidOperationException("DATEDIFF requires 2 or 3 arguments.");

        return name switch
        {
            "DATEADD" when arguments.Count == 3 => new DateAddExpression
            {
                Alias = function.Alias,
                Unit = ParseUnit(arguments[0]),
                Amount = arguments[1],
                Value = arguments[2]
            },
            "DATEDIFF" when arguments.Count == 2 => new DateDiffExpression
            {
                Alias = function.Alias,
                Unit = SqlDatePart.Day,
                Start = arguments[1],
                End = arguments[0]
            },
            "DATEDIFF" when arguments.Count == 3 => new DateDiffExpression
            {
                Alias = function.Alias,
                Unit = ParseUnit(arguments[0]),
                Start = arguments[1],
                End = arguments[2]
            },
            _ => throw new InvalidOperationException($"Unsupported temporal function '{function.FunctionName}'.")
        };
    }

    public static SqlDatePart ParseUnit(SelectCondition argument)
    {
        var unit = argument switch
        {
            FieldSelectCondition field => field.FieldName.Trim(),
            TemplateSqlTokenSelectCondition token => token.Token.Trim(),
            _ => throw new InvalidOperationException(
                "DATEADD/DATEDIFF date-part unit must be an unquoted SQL keyword.")
        };

        return unit.ToUpperInvariant() switch
        {
            "DAY" or "DD" or "D" => SqlDatePart.Day,
            "WEEK" or "WK" or "WW" => SqlDatePart.Week,
            "MONTH" or "MM" or "M" => SqlDatePart.Month,
            "QUARTER" or "QQ" or "Q" => SqlDatePart.Quarter,
            "YEAR" or "YY" or "YYYY" => SqlDatePart.Year,
            "HOUR" or "HH" => SqlDatePart.Hour,
            "MINUTE" or "MI" or "N" => SqlDatePart.Minute,
            "SECOND" or "SS" or "S" => SqlDatePart.Second,
            _ => throw new InvalidOperationException($"Unsupported DATEADD/DATEDIFF date-part unit '{unit}'.")
        };
    }
}
