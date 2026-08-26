using System.Text.RegularExpressions;

namespace HsSqlAgent.SqlCore.SqlTranslation.Functions.Translators;

public sealed partial class JsonFunctionTranslator : ISpecializedFunctionTranslator
{
    public bool CanTranslate(string functionName) => functionName.Trim().ToUpperInvariant() is "JSON_EXTRACT" or "JSON_SET";

    public SelectCondition Normalize(FunctionSelectCondition function, TranslationContext context)
    {
        var name = function.FunctionName.Trim().ToUpperInvariant();
        var arguments = function.Arguments ?? [];
        if (name == "JSON_EXTRACT" && arguments.Count != 2)
            throw new InvalidOperationException("JSON_EXTRACT requires exactly 2 arguments.");
        if (name == "JSON_SET" && arguments.Count != 3)
            throw new InvalidOperationException("JSON_SET requires exactly 3 arguments.");
        var path = ParsePath(arguments[1]);
        return name == "JSON_EXTRACT"
            ? new JsonExtractExpression { Value = arguments[0], Path = path }
            : new JsonSetExpression { Value = arguments[0], Path = path, NewValue = arguments[2] };
    }

    private static JsonPath ParsePath(SelectCondition expression)
    {
        if (expression is not ConstantSelectCondition { Constant: string value })
            throw new InvalidOperationException("Portable JSON paths must be string constants.");
        var match = JsonPathRegex().Match(value);
        if (!match.Success)
            throw new InvalidOperationException($"Unsupported portable JSON path '{value}'.");
        // Captures from separate groups lose ordering, so scan the validated path once to retain it.
        var segments = JsonPathSegmentRegex().Matches(value[1..])
            .Select(segment => segment.Groups["property"].Success
                ? (JsonPathSegment)new JsonPropertySegment(segment.Groups["property"].Value)
                : new JsonArrayIndexSegment(int.Parse(
                    segment.Groups["index"].Value,
                    System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        return new JsonPath(segments);
    }

    [GeneratedRegex(@"^\$(?:(?:\.(?<property>[A-Za-z_][A-Za-z0-9_]*))|(?:\[(?<index>\d+)\]))*$")]
    private static partial Regex JsonPathRegex();

    [GeneratedRegex(@"(?:\.(?<property>[A-Za-z_][A-Za-z0-9_]*))|(?:\[(?<index>\d+)\])")]
    private static partial Regex JsonPathSegmentRegex();
}
