namespace SqlAgent.Test.Services;

internal sealed record GrammarVariant<T>(
    string Name,
    T Value);

internal sealed record CanonicalPagingExpectation(
    int? Limit,
    int? Offset);

internal static class SyntaxGrammarMatrix
{
    public static string CaseName(params string[] dimensions) =>
        string.Join("__", dimensions);

    public static string ExpectedTables(params IEnumerable<string>[] groups) =>
        string.Join(
            ",",
            groups
                .SelectMany(group => group)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    public static bool IntegerParameterEquals(object? value, long expected) =>
        value switch
        {
            sbyte actual => actual == expected,
            byte actual => actual == expected,
            short actual => actual == expected,
            ushort actual => actual == expected,
            int actual => actual == expected,
            uint actual => actual == expected,
            long actual => actual == expected,
            ulong actual => actual <= long.MaxValue && (long)actual == expected,
            _ => false
        };

    public static SqlDiagnostic RequireTypedDiagnostic(Exception error)
    {
        var diagnostic = error switch
        {
            SqlParseException parse => parse.Diagnostic,
            SqlCompilationException compilation => compilation.Diagnostic,
            _ => error.Data["HsSqlAgent.SqlCore.Diagnostic"] as SqlDiagnostic
        };

        return Xunit.Assert.IsType<SqlDiagnostic>(diagnostic);
    }

    public static (int? Limit, int? Offset) CanonicalPaging(
        HsSqlAgent.SqlCore.Core.Ast.SqlStatement statement,
        string caseName) =>
        statement switch
        {
            HsSqlAgent.SqlCore.Core.Ast.SelectStatement select => (
                select.Limit.HasValue ? select.Limit.Value : null,
                select.Offset.HasValue ? select.Offset.Value : null),
            HsSqlAgent.SqlCore.Core.Ast.QueryStatement query => (
                query.Limit.HasValue ? query.Limit.Value : null,
                query.Offset.HasValue ? query.Offset.Value : null),
            _ => throw new Xunit.Sdk.XunitException(
                $"{caseName}: expected SELECT/query compatibility AST, actual {statement.GetType().FullName}.")
        };

    public static IEnumerable<(
        GrammarVariant<T1> First,
        GrammarVariant<T2> Second)>
        Product<T1, T2>(
            IReadOnlyList<GrammarVariant<T1>> first,
            IReadOnlyList<GrammarVariant<T2>> second)
    {
        foreach (var firstVariant in first)
        foreach (var secondVariant in second)
            yield return (firstVariant, secondVariant);
    }

    public static IEnumerable<(
        GrammarVariant<T1> First,
        GrammarVariant<T2> Second,
        GrammarVariant<T3> Third)>
        Product<T1, T2, T3>(
            IReadOnlyList<GrammarVariant<T1>> first,
            IReadOnlyList<GrammarVariant<T2>> second,
            IReadOnlyList<GrammarVariant<T3>> third)
    {
        foreach (var firstVariant in first)
        foreach (var secondVariant in second)
        foreach (var thirdVariant in third)
            yield return (firstVariant, secondVariant, thirdVariant);
    }

    public static int ProductCount<T1, T2, T3, T4>(
        IReadOnlyList<GrammarVariant<T1>> first,
        IReadOnlyList<GrammarVariant<T2>> second,
        IReadOnlyList<GrammarVariant<T3>> third,
        IReadOnlyList<GrammarVariant<T4>> fourth) =>
        checked(first.Count * second.Count * third.Count * fourth.Count);

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
