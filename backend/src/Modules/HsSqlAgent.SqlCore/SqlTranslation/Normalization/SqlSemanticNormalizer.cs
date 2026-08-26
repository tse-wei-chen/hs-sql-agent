using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlTranslation.Context;
using HsSqlAgent.SqlCore.SqlTranslation.Functions;

namespace HsSqlAgent.SqlCore.SqlTranslation.Normalization;

public sealed class SqlSemanticNormalizer(IFunctionRegistry registry)
{
    private readonly IFunctionRegistry _registry = registry;
    private readonly IFunctionTranslator _identity = new IdentityFunctionTranslator();
    private readonly IFunctionTranslator _rename = new RenameFunctionTranslator();
    private readonly IFunctionTranslator _template = new TemplateFunctionTranslator();
    private readonly IFunctionTranslator _passthrough = new PassthroughFunctionTranslator();

    public FunctionTranslationResult Normalize(
        FunctionSelectCondition function,
        TranslationContext context)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(context);

        var argumentCount = function.Arguments?.Count ?? 0;
        var source = _registry.Find(context.SourceDialect, function.FunctionName, argumentCount);
        if (source is null)
            return _passthrough.Translate(context, function, null);

        var target = source.Semantic is { } semantic
            ? _registry.Find(context.TargetDialect, semantic, argumentCount)
            : _registry.Find(context.TargetDialect, function.FunctionName, argumentCount);

        if (target is null)
            return _passthrough.Translate(context, function, null);

        return target.TranslationKind switch
        {
            FunctionTranslationKind.Identity => _identity.Translate(context, function, target),
            FunctionTranslationKind.Rename or FunctionTranslationKind.Semantic =>
                _rename.Translate(context, function, target),
            FunctionTranslationKind.Template => _template.Translate(context, function, target),
            FunctionTranslationKind.Specialized => throw new NotSupportedException(
                $"Specialized translator '{target.Translator}' is not registered."),
            _ => throw new ArgumentOutOfRangeException(nameof(target.TranslationKind))
        };
    }
}
