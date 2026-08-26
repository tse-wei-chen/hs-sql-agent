namespace HsSqlAgent.SqlCore.SqlTranslation.Functions;

public sealed record FunctionTranslationResult(
    SelectCondition Expression,
    IReadOnlyList<TranslationDiagnostic> Diagnostics);

public interface IFunctionTranslator
{
    FunctionTranslationResult Translate(
        TranslationContext context,
        FunctionSelectCondition function,
        FunctionDefinition? targetDefinition);
}

public sealed class IdentityFunctionTranslator : IFunctionTranslator
{
    public FunctionTranslationResult Translate(
        TranslationContext context,
        FunctionSelectCondition function,
        FunctionDefinition? targetDefinition) =>
        new(Clone(function), []);

    internal static FunctionSelectCondition Clone(FunctionSelectCondition function, string? name = null) => new()
    {
        Alias = function.Alias,
        FunctionName = name ?? function.FunctionName,
        Arguments = function.Arguments is null ? null : [.. function.Arguments],
        IsDistinct = function.IsDistinct,
        FilterWhereConditions = function.FilterWhereConditions is null ? null : [.. function.FilterWhereConditions],
        Window = function.Window
    };
}

public sealed class RenameFunctionTranslator : IFunctionTranslator
{
    public FunctionTranslationResult Translate(
        TranslationContext context,
        FunctionSelectCondition function,
        FunctionDefinition? targetDefinition)
    {
        ArgumentNullException.ThrowIfNull(targetDefinition);
        return new(IdentityFunctionTranslator.Clone(function, targetDefinition.Name), []);
    }
}

public sealed class TemplateFunctionTranslator : IFunctionTranslator
{
    public FunctionTranslationResult Translate(
        TranslationContext context,
        FunctionSelectCondition function,
        FunctionDefinition? targetDefinition)
    {
        if (string.IsNullOrWhiteSpace(targetDefinition?.Template))
            throw new InvalidOperationException("A template definition must provide a template.");

        var translated = new FunctionTemplateEngine(targetDefinition.Template).Translate(function.Arguments, context)
            ?? throw new InvalidOperationException("Function template produced no expression.");
        return new(translated, []);
    }
}

public sealed class PassthroughFunctionTranslator : IFunctionTranslator
{
    public FunctionTranslationResult Translate(
        TranslationContext context,
        FunctionSelectCondition function,
        FunctionDefinition? targetDefinition)
    {
        if (context.UnknownFunctionPolicy == UnknownFunctionPolicy.Throw)
            throw new InvalidOperationException(
                $"Unknown function '{function.FunctionName}' while translating {context.SourceDialect} to {context.TargetDialect}.");

        var diagnostics = context.UnknownFunctionPolicy == UnknownFunctionPolicy.WarnAndPassthrough
            ? new TranslationDiagnostic[]
            {
                new(
                    "SQLFUNC001",
                    DiagnosticSeverity.Warning,
                    $"Unknown function '{function.FunctionName}'. The function was preserved unchanged while translating {context.SourceDialect} to {context.TargetDialect}.",
                    FunctionPortability.Unknown)
            }
            : [];

        return new(IdentityFunctionTranslator.Clone(function), diagnostics);
    }
}
