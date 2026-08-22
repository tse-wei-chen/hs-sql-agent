using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;
using SqlAgent.Service.SqlTranslation.Ast.Semantic;
using SqlAgent.Service.SqlTranslation.DateFormats;
using SqlAgent.Service.SqlTranslation.Context;

namespace SqlAgent.Service.SqlTranslation.Functions.Translators;

public sealed class PortableFunctionTranslator : ISpecializedFunctionTranslator
{
    private static readonly DateFormatTranslator DateFormats = new();

    public bool CanTranslate(string functionName) => functionName.Trim().ToUpperInvariant() is
        "DATE_FORMAT" or "FORMAT" or "TO_CHAR" or "STRFTIME" or "TO_DATE" or "STR_TO_DATE"
        or "STRPOS" or "INSTR" or "LOCATE" or "CHARINDEX" or "YEAR" or "MONTH" or "DAY";

    public SelectCondition Normalize(FunctionSelectCondition function, TranslationContext context)
    {
        var name = function.FunctionName.Trim().ToUpperInvariant();
        var arguments = function.Arguments ?? [];
        if (name is "YEAR" or "MONTH" or "DAY")
        {
            RequireArity(name, arguments, 1);
            return new DatePartExpression
            {
                Part = Enum.Parse<SqlDatePart>(name, true),
                Value = arguments[0]
            };
        }
        if (name is "STRPOS" or "INSTR" or "LOCATE" or "CHARINDEX")
        {
            RequireArity(name, arguments, 2);
            var reversed = name is "LOCATE" or "CHARINDEX";
            return new PositionExpression
            {
                Haystack = arguments[reversed ? 1 : 0],
                Needle = arguments[reversed ? 0 : 1]
            };
        }
        RequireArity(name, arguments, 2);
        var formatIndex = name == "STRFTIME" ? 0 : 1;
        var valueIndex = name == "STRFTIME" ? 1 : 0;
        if (arguments[formatIndex] is not ConstantSelectCondition { Constant: string format })
            throw new InvalidOperationException($"{name} format must be a string constant.");
        var canonical = DateFormats.Parse(format, context.SourceDialect);
        return name is "TO_DATE" or "STR_TO_DATE"
            ? new FormattedDateParseExpression { Value = arguments[valueIndex], Format = canonical }
            : new DateFormatExpression { Value = arguments[valueIndex], Format = canonical };
    }

    private static void RequireArity(string name, IReadOnlyList<SelectCondition> arguments, int expected)
    {
        if (arguments.Count != expected)
            throw new InvalidOperationException($"{name} requires exactly {expected} arguments.");
    }

}
