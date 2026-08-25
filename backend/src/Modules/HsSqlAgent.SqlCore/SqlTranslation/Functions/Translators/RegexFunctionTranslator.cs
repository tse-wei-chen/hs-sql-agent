using SqlAgent.Service.Models;
using SqlAgent.Service.SqlTranslation.Ast.Semantic;
using SqlAgent.Service.SqlTranslation.Context;

namespace SqlAgent.Service.SqlTranslation.Functions.Translators;

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
