namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Canonical function metadata consumed after source binding/normalization. Raw source aliases and
/// provider-specific semantic mappings remain owned by FunctionRegistry and specialized
/// normalizers; this registry describes only function names that are valid in the canonical Core
/// AST so normalization, semantic placement, and plan-shape validation cannot drift independently.
/// </summary>
internal static class SqlCanonicalFunctionRegistry
{
    private static readonly IReadOnlyDictionary<string, SqlCanonicalFunctionContract> Contracts =
        new Dictionary<string, SqlCanonicalFunctionContract>(StringComparer.OrdinalIgnoreCase)
        {
            ["ABS"] = Scalar("ABS", 1),
            ["ROUND"] = Scalar("ROUND", 1, 2),
            ["LOWER"] = Scalar("LOWER", 1),
            ["UPPER"] = Scalar("UPPER", 1),
            ["TRIM"] = Scalar("TRIM", 1),
            ["LTRIM"] = Scalar("LTRIM", 1),
            ["RTRIM"] = Scalar("RTRIM", 1),
            ["NULLIF"] = Scalar("NULLIF", 2),

            ["AVG"] = Aggregate("AVG", 1),
            ["COUNT"] = Aggregate("COUNT", 1),
            ["MAX"] = Aggregate("MAX", 1),
            ["MIN"] = Aggregate("MIN", 1),
            ["SUM"] = Aggregate("SUM", 1),

            ["ROW_NUMBER"] = Window("ROW_NUMBER", 0),
            ["RANK"] = Window("RANK", 0),
            ["DENSE_RANK"] = Window("DENSE_RANK", 0),
            ["PERCENT_RANK"] = Window("PERCENT_RANK", 0),
            ["CUME_DIST"] = Window("CUME_DIST", 0),
            ["LAG"] = Window("LAG", 1, 3),
            ["LEAD"] = Window("LEAD", 1, 3),
            ["FIRST_VALUE"] = Window("FIRST_VALUE", 1),
            ["LAST_VALUE"] = Window("LAST_VALUE", 1),
            ["NTH_VALUE"] = Window("NTH_VALUE", 2),
            ["NTILE"] = Window("NTILE", 1),

            ["CORE_DATE_ADD"] = Scalar("CORE_DATE_ADD", 3, directPortable: false),
            ["CORE_DATE_DIFF"] = Scalar("CORE_DATE_DIFF", 3, directPortable: false),
            ["CORE_DATE_PART"] = Scalar("CORE_DATE_PART", 2, directPortable: false),
            ["CORE_DATE_FORMAT"] = Scalar("CORE_DATE_FORMAT", 2, directPortable: false),
            ["CORE_DATE_PARSE"] = Scalar("CORE_DATE_PARSE", 2, directPortable: false),
            ["CORE_POSITION"] = Scalar("CORE_POSITION", 2, directPortable: false),
            ["CORE_JSON_EXTRACT"] = Scalar("CORE_JSON_EXTRACT", 2, directPortable: false),
            ["CORE_JSON_SET"] = Scalar("CORE_JSON_SET", 3, directPortable: false),
            ["CORE_REGEX_MATCH"] = Scalar("CORE_REGEX_MATCH", 2, directPortable: false),
            ["CORE_CURRENT_DATE"] = Scalar("CORE_CURRENT_DATE", 0, directPortable: false),
            ["CORE_CURRENT_TIME"] = Scalar("CORE_CURRENT_TIME", 0, directPortable: false),
            ["CORE_CURRENT_TIMESTAMP"] = Scalar("CORE_CURRENT_TIMESTAMP", 0, directPortable: false),
            ["CORE_STRING_AGG"] = new(
                "CORE_STRING_AGG",
                2,
                2,
                SqlCanonicalFunctionKind.Aggregate,
                AllowDistinct: false,
                AllowFilter: true,
                AllowWindow: false,
                RequireWindow: false,
                IsDirectPortable: false)
        };

    internal static IEnumerable<SqlCanonicalFunctionContract> All =>
        Contracts.Values;

    internal static SqlCanonicalFunctionContract? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Contracts.TryGetValue(name.Trim(), out var contract)
            ? contract
            : null;
    }

    internal static bool IsDirectPortable(string name) =>
        Find(name)?.IsDirectPortable == true;

    internal static bool IsAggregate(string name) =>
        Find(name)?.Kind == SqlCanonicalFunctionKind.Aggregate;

    internal static bool IsWindow(string name) =>
        Find(name)?.Kind == SqlCanonicalFunctionKind.Window;

    private static SqlCanonicalFunctionContract Scalar(
        string name,
        int arguments,
        bool directPortable = true) =>
        Scalar(name, arguments, arguments, directPortable);

    private static SqlCanonicalFunctionContract Scalar(
        string name,
        int minArguments,
        int maxArguments,
        bool directPortable = true) =>
        new(
            name,
            minArguments,
            maxArguments,
            SqlCanonicalFunctionKind.Scalar,
            AllowDistinct: false,
            AllowFilter: false,
            AllowWindow: false,
            RequireWindow: false,
            IsDirectPortable: directPortable);

    private static SqlCanonicalFunctionContract Aggregate(
        string name,
        int arguments) =>
        new(
            name,
            arguments,
            arguments,
            SqlCanonicalFunctionKind.Aggregate,
            AllowDistinct: true,
            AllowFilter: true,
            AllowWindow: true,
            RequireWindow: false,
            IsDirectPortable: true);

    private static SqlCanonicalFunctionContract Window(
        string name,
        int arguments) =>
        Window(name, arguments, arguments);

    private static SqlCanonicalFunctionContract Window(
        string name,
        int minArguments,
        int maxArguments) =>
        new(
            name,
            minArguments,
            maxArguments,
            SqlCanonicalFunctionKind.Window,
            AllowDistinct: false,
            AllowFilter: false,
            AllowWindow: true,
            RequireWindow: true,
            IsDirectPortable: true);
}

internal enum SqlCanonicalFunctionKind
{
    Scalar,
    Aggregate,
    Window
}

internal sealed record SqlCanonicalFunctionContract(
    string Name,
    int MinArguments,
    int MaxArguments,
    SqlCanonicalFunctionKind Kind,
    bool AllowDistinct,
    bool AllowFilter,
    bool AllowWindow,
    bool RequireWindow,
    bool IsDirectPortable)
{
    internal bool AcceptsArgumentCount(int argumentCount) =>
        argumentCount >= MinArguments && argumentCount <= MaxArguments;
}
