namespace HsSqlAgent.SqlCore.SqlTranslation.Functions.Translators;

public interface ISpecializedFunctionTranslator
{
    bool CanTranslate(string functionName);
    SelectCondition Normalize(FunctionSelectCondition function, TranslationContext context);
}

public sealed class SpecializedFunctionTranslatorRegistry(IEnumerable<ISpecializedFunctionTranslator> translators)
{
    private readonly IReadOnlyList<ISpecializedFunctionTranslator> _translators = translators.ToArray();

    public bool CanTranslate(string functionName) =>
        _translators.Any(candidate => candidate.CanTranslate(functionName));

    public SelectCondition? Normalize(FunctionSelectCondition function, TranslationContext context)
    {
        var translator = _translators.FirstOrDefault(candidate => candidate.CanTranslate(function.FunctionName));
        return translator?.Normalize(function, context);
    }
}
