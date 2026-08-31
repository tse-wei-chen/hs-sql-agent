namespace SqlAgent.Test.Services;

internal sealed record GrammarVariant<T>(
    string Name,
    T Value);

internal static class SyntaxGrammarMatrix
{
    public static IEnumerable<(
        GrammarVariant<T1> First,
        GrammarVariant<T2> Second,
        GrammarVariant<T3> Third,
        GrammarVariant<T4> Fourth)>
        Product<T1, T2, T3, T4>(
            IReadOnlyList<GrammarVariant<T1>> first,
            IReadOnlyList<GrammarVariant<T2>> second,
            IReadOnlyList<GrammarVariant<T3>> third,
            IReadOnlyList<GrammarVariant<T4>> fourth)
    {
        foreach (var firstVariant in first)
        foreach (var secondVariant in second)
        foreach (var thirdVariant in third)
        foreach (var fourthVariant in fourth)
            yield return (firstVariant, secondVariant, thirdVariant, fourthVariant);
    }
}
