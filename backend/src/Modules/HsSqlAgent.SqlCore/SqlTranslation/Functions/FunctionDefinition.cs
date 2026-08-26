using HsSqlAgent.SqlCore.Enums;

namespace HsSqlAgent.SqlCore.SqlTranslation.Functions;

public enum SemanticFunction
{
    StringLength,
    Ceiling,
    Random,
    Repeat,
    Lower,
    Upper,
    Substring,
    Coalesce,
    CurrentDate,
    CurrentTimestamp,
    DateAdd,
    DateDiff,
    DatePart,
    DateFormat,
    JsonExtract,
    JsonSet,
    RegexMatch,
    StringAggregate
}

public enum FunctionTranslationKind
{
    Identity,
    Rename,
    Semantic,
    Template,
    Specialized
}

public sealed record FunctionDefinition
{
    public required SqlAgentToolType Dialect { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public SemanticFunction? Semantic { get; init; }
    public int MinArguments { get; init; }
    public int? MaxArguments { get; init; }
    public required FunctionTranslationKind TranslationKind { get; init; }
    public string? Template { get; init; }
    public string? Translator { get; init; }
    public HsSqlAgent.SqlCore.SqlTranslation.Diagnostics.FunctionPortability Portability { get; init; } = HsSqlAgent.SqlCore.SqlTranslation.Diagnostics.FunctionPortability.Native;

    public bool AcceptsArgumentCount(int count) =>
        count >= MinArguments && (MaxArguments is null || count <= MaxArguments.Value);
}
