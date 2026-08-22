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

        // The format string belongs to the syntax family of the function that introduced it,
        // not necessarily to the strategy currently executing the translated query. For example,
        // DATE_FORMAT(..., '%Y-%m') remains MySQL format syntax when translated to Postgres, and
        // FORMAT(..., 'yyyy-MM') remains SQL Server format syntax.
        var formatDialect = ResolveFormatDialect(name, context.SourceDialect);
        var canonical = DateFormats.Parse(format, formatDialect);
        return name is "TO_DATE" or "STR_TO_DATE"
            ? new FormattedDateParseExpression { Value = arguments[valueIndex], Format = canonical }
            : new DateFormatExpression { Value = arguments[valueIndex], Format = canonical };
    }

    private static SqlAgentToolType ResolveFormatDialect(
        string functionName,
        SqlAgentToolType sourceDialect) => functionName switch
    {
        "DATE_FORMAT" or "STR_TO_DATE" => SqlAgentToolType.MySQL,
        "FORMAT" => SqlAgentToolType.MsSqlServer,
        "STRFTIME" => SqlAgentToolType.Sqlite,
        // TO_CHAR/TO_DATE exist in more than one named-format dialect. Their token vocabularies
        // overlap for the portable subset, so retain the explicit source dialect when known.
        "TO_CHAR" or "TO_DATE" when sourceDialect is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle
            => sourceDialect,
        "TO_CHAR" or "TO_DATE" => SqlAgentToolType.Postgres,
        _ => sourceDialect
    };

    private static void RequireArity(string name, IReadOnlyList<SelectCondition> arguments, int expected)
    {
        if (arguments.Count != expected)
            throw new InvalidOperationException($"{name} requires exactly {expected} arguments.");
    }
}
