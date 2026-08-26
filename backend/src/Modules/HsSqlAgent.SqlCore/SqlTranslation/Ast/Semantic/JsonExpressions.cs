using HsSqlAgent.SqlCore.Models;

namespace HsSqlAgent.SqlCore.SqlTranslation.Ast.Semantic;

public abstract record JsonPathSegment;
public sealed record JsonPropertySegment(string Name) : JsonPathSegment;
public sealed record JsonArrayIndexSegment(int Index) : JsonPathSegment;

public sealed record JsonPath(IReadOnlyList<JsonPathSegment> Segments)
{
    public string RenderDollarPath() => "$" + string.Concat(Segments.Select(segment =>
        segment switch
        {
            JsonPropertySegment property => $".{property.Name}",
            JsonArrayIndexSegment index => $"[{index.Index}]",
            _ => throw new ArgumentOutOfRangeException(nameof(segment))
        }));

    public string RenderPostgresPath() => "{" + string.Join(',', Segments.Select(RenderSegment)) + "}";

    public IEnumerable<string> RenderSegments() => Segments.Select(RenderSegment);

    private static string RenderSegment(JsonPathSegment segment) => segment switch
    {
        JsonPropertySegment property => property.Name,
        JsonArrayIndexSegment index => index.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(segment))
    };
}

public sealed class JsonExtractExpression : SelectCondition
{
    public required SelectCondition Value { get; init; }
    public required JsonPath Path { get; init; }
}

public sealed class JsonSetExpression : SelectCondition
{
    public required SelectCondition Value { get; init; }
    public required JsonPath Path { get; init; }
    public required SelectCondition NewValue { get; init; }
}

public sealed class RegexMatchExpression : SelectCondition
{
    public required SelectCondition Value { get; init; }
    public required SelectCondition Pattern { get; init; }
}
