using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlTranslation.Ast.Semantic;
using HsSqlAgent.SqlCore.SqlTranslation.Context;

namespace HsSqlAgent.SqlCore.SqlTranslation.Functions.Translators;

public sealed class RegexFunctionTranslator : ISpecializedFunctionTranslator
{
    public bool CanTranslate(string functionName) =>
        functionName.Trim().Equals("REGEXP_LIKE", StringComparison.OrdinalIgnoreCase);

    public SelectCondition Normalize(FunctionSelectCondition function, TranslationContext context)
    {
        var arguments = function.Arguments ?? [];
        if (arguments.Count != 2)
            throw new InvalidOperationException("REGEXP_LIKE requires exactly 2 arguments.");
        return new RegexMatchExpression { Value = arguments[0], Pattern = arguments[1] };
    }
}
