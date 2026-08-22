using SqlAgent.Service.Models;
using SqlAgent.Service.SqlTranslation.Context;
using SqlAgent.Service.SqlTranslation.Templates.Ast;
using SqlAgent.Service.SqlTranslation.Templates.Modifiers;

namespace SqlAgent.Service.SqlTranslation.Templates.Resolution;

internal sealed class TemplateArgumentResolver(ITemplateModifierRegistry modifierRegistry)
{
    private readonly ITemplateModifierRegistry _modifierRegistry = modifierRegistry;

    internal SelectCondition Resolve(TemplateExpression expression, IList<SelectCondition>? arguments, TranslationContext? context) => expression switch
    {
        TemplateArgumentReferenceExpression reference => ResolveReference(reference, arguments, context),
        TemplateSqlTokenExpression token => new TemplateSqlTokenSelectCondition { Token = token.Token },
        TemplateConstantExpression constant => new ConstantSelectCondition { Constant = constant.Value },
        TemplateIntervalExpression interval => new IntervalSelectCondition { Literal = interval.Literal },
        TemplateOperationExpression operation => new OperationSelectCondition
        {
            Left = Resolve(operation.Left, arguments, context), Operator = operation.Operator,
            Right = Resolve(operation.Right, arguments, context)
        },
        TemplateFunctionExpression function => new FunctionSelectCondition
        {
            FunctionName = function.Name,
            Arguments = function.Arguments.Select(argument => Resolve(argument, arguments, context)).ToList()
        },
        TemplateCastExpression cast => new CastSelectCondition
        {
            Expression = Resolve(cast.Expression, arguments, context), TypeName = cast.TypeName
        },
        TemplateExtractExpression extract => new TemplateExtractSelectCondition
        {
            Unit = Resolve(extract.Unit, arguments, context),
            Expression = Resolve(extract.Expression, arguments, context)
        },
        TemplateCaseExpression caseExpression => ResolveCase(caseExpression, arguments, context),
        _ => throw new InvalidOperationException($"Unsupported template expression: {expression.GetType().Name}")
    };

    private SelectCondition ResolveReference(TemplateArgumentReferenceExpression reference, IList<SelectCondition>? arguments, TranslationContext? context)
    {
        if (arguments is null || reference.Index < 0 || reference.Index >= arguments.Count)
            throw new FormatException($"Template argument ${reference.Index + 1} is not available.");
        var resolved = arguments[reference.Index];
        if (string.IsNullOrEmpty(reference.Modifier)) return resolved;
        if (context is null)
            throw new FormatException($"Function-template modifier '{reference.Modifier}' requires a TranslationContext.");
        var modifierArguments = reference.ModifierArguments
            .Select(argument => Resolve(argument, arguments, context)).ToArray();
        return _modifierRegistry.Get(reference.Modifier).Apply(resolved, modifierArguments, context);
    }

    private TemplateCaseSelectCondition ResolveCase(TemplateCaseExpression expression, IList<SelectCondition>? arguments, TranslationContext? context) => new()
    {
        Cases = expression.Cases.Select(branch => new SqlAgent.Service.Models.TemplateCaseBranch
        {
            Condition = Resolve(branch.Condition, arguments, context),
            Value = Resolve(branch.Value, arguments, context)
        }).ToList(),
        ElseExpression = expression.ElseExpression is null ? null : Resolve(expression.ElseExpression, arguments, context)
    };
}
