using System.Collections.Immutable;

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
            ["COUNT"] = WithPlanShapeRules(
                Aggregate("COUNT", 1),
                DistinctWildcardForbidden(
                    0,
                    "COUNT(DISTINCT *) is not a valid Core aggregate shape.")),
            ["MAX"] = Aggregate("MAX", 1),
            ["MIN"] = Aggregate("MIN", 1),
            ["SUM"] = Aggregate("SUM", 1),

            ["ROW_NUMBER"] = Window("ROW_NUMBER", 0, frameInsensitive: true),
            ["RANK"] = Window("RANK", 0, frameInsensitive: true),
            ["DENSE_RANK"] = Window("DENSE_RANK", 0, frameInsensitive: true),
            ["PERCENT_RANK"] = Window("PERCENT_RANK", 0, frameInsensitive: true),
            ["CUME_DIST"] = Window("CUME_DIST", 0, frameInsensitive: true),
            ["LAG"] = WithTargetMetadata(
                Window("LAG", 1, 3, frameInsensitive: true),
                SqlCanonicalTargetCapabilityFamily.None,
                WindowOffset(1)),
            ["LEAD"] = WithTargetMetadata(
                Window("LEAD", 1, 3, frameInsensitive: true),
                SqlCanonicalTargetCapabilityFamily.None,
                WindowOffset(1)),
            ["FIRST_VALUE"] = Window("FIRST_VALUE", 1),
            ["LAST_VALUE"] = Window("LAST_VALUE", 1),
            ["NTH_VALUE"] = WithTargetMetadata(
                Window("NTH_VALUE", 2),
                SqlCanonicalTargetCapabilityFamily.WindowFunction,
                PositiveInteger(1, "NTH_VALUE index must be a positive integer.")),
            ["NTILE"] = WithTargetMetadata(
                Window("NTILE", 1, frameInsensitive: true),
                SqlCanonicalTargetCapabilityFamily.None,
                PositiveInteger(0, "NTILE bucket count must be a positive integer.")),

            ["CORE_DATE_ADD"] = WithNativeLowering(
                WithTargetMetadata(
                    Scalar("CORE_DATE_ADD", 3, directPortable: false),
                    SqlCanonicalTargetCapabilityFamily.DateMath),
                SqlCanonicalNativeLoweringKind.DateAdd),
            ["CORE_DATE_DIFF"] = WithNativeLowering(
                WithTargetMetadata(
                    Scalar("CORE_DATE_DIFF", 3, directPortable: false),
                    SqlCanonicalTargetCapabilityFamily.DateMath),
                SqlCanonicalNativeLoweringKind.DateDiff),
            ["CORE_DATE_PART"] = WithNativeLowering(
                WithTargetMetadata(
                    Scalar("CORE_DATE_PART", 2, directPortable: false),
                    SqlCanonicalTargetCapabilityFamily.DatePart),
                SqlCanonicalNativeLoweringKind.DatePart),
            ["CORE_DATE_FORMAT"] = WithNativeLowering(
                WithTargetMetadata(
                    Scalar("CORE_DATE_FORMAT", 2, directPortable: false),
                    SqlCanonicalTargetCapabilityFamily.TemporalFormat),
                SqlCanonicalNativeLoweringKind.DateFormat),
            ["CORE_DATE_PARSE"] = WithNativeLowering(
                WithTargetMetadata(
                    Scalar("CORE_DATE_PARSE", 2, directPortable: false),
                    SqlCanonicalTargetCapabilityFamily.TemporalFormat),
                SqlCanonicalNativeLoweringKind.DateParse),
            ["CORE_POSITION"] = WithNativeLowering(
                Scalar("CORE_POSITION", 2, directPortable: false),
                SqlCanonicalNativeLoweringKind.Position),
            ["CORE_JSON_EXTRACT"] = WithNativeLowering(
                WithTargetMetadata(
                    Scalar("CORE_JSON_EXTRACT", 2, directPortable: false),
                    SqlCanonicalTargetCapabilityFamily.Json),
                SqlCanonicalNativeLoweringKind.JsonExtract),
            ["CORE_JSON_SET"] = WithNativeLowering(
                WithTargetMetadata(
                    Scalar("CORE_JSON_SET", 3, directPortable: false),
                    SqlCanonicalTargetCapabilityFamily.Json),
                SqlCanonicalNativeLoweringKind.JsonSet),
            ["CORE_REGEX_MATCH"] = WithNativeLowering(
                WithTargetMetadata(
                    Scalar("CORE_REGEX_MATCH", 2, directPortable: false),
                    SqlCanonicalTargetCapabilityFamily.Regex),
                SqlCanonicalNativeLoweringKind.RegexMatch),
            ["CORE_CURRENT_DATE"] = WithNativeLowering(
                WithCurrentTemporalTarget(
                    Scalar("CORE_CURRENT_DATE", 0, directPortable: false),
                    SqlCurrentTemporalKind.Date),
                SqlCanonicalNativeLoweringKind.CurrentDate),
            ["CORE_CURRENT_TIME"] = WithNativeLowering(
                WithCurrentTemporalTarget(
                    Scalar("CORE_CURRENT_TIME", 0, directPortable: false),
                    SqlCurrentTemporalKind.Time),
                SqlCanonicalNativeLoweringKind.CurrentTime),
            ["CORE_CURRENT_TIMESTAMP"] = WithNativeLowering(
                WithCurrentTemporalTarget(
                    Scalar("CORE_CURRENT_TIMESTAMP", 0, directPortable: false),
                    SqlCurrentTemporalKind.Timestamp),
                SqlCanonicalNativeLoweringKind.CurrentTimestamp),
            ["CORE_STRING_AGG"] = WithPlanShapeRules(
                WithNativeLowering(
                    new(
                        "CORE_STRING_AGG",
                        2,
                        2,
                        SqlCanonicalFunctionKind.Aggregate,
                        AllowDistinct: false,
                        AllowFilter: true,
                        AllowWindow: false,
                        RequireWindow: false,
                        IsDirectPortable: false),
                    SqlCanonicalNativeLoweringKind.StringAggregate),
                LiteralStringRequired(
                    1,
                    "aggregate.string.dynamic_separator"))
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
        int arguments,
        bool frameInsensitive = false) =>
        Window(name, arguments, arguments, frameInsensitive);

    private static SqlCanonicalFunctionContract Window(
        string name,
        int minArguments,
        int maxArguments,
        bool frameInsensitive = false) =>
        new(
            name,
            minArguments,
            maxArguments,
            SqlCanonicalFunctionKind.Window,
            AllowDistinct: false,
            AllowFilter: false,
            AllowWindow: true,
            RequireWindow: true,
            IsDirectPortable: true)
        {
            IsWindowFrameInsensitive = frameInsensitive
        };

    private static SqlCanonicalFunctionContract WithTargetMetadata(
        SqlCanonicalFunctionContract contract,
        SqlCanonicalTargetCapabilityFamily targetCapabilityFamily,
        params SqlCanonicalLiteralArgumentRule[] literalArgumentRules) =>
        contract with
        {
            TargetCapabilityFamily = targetCapabilityFamily,
            LiteralArgumentRules = literalArgumentRules.ToImmutableArray()
        };

    private static SqlCanonicalFunctionContract WithNativeLowering(
        SqlCanonicalFunctionContract contract,
        SqlCanonicalNativeLoweringKind nativeLoweringKind) =>
        contract with { NativeLoweringKind = nativeLoweringKind };

    private static SqlCanonicalFunctionContract WithCurrentTemporalTarget(
        SqlCanonicalFunctionContract contract,
        SqlCurrentTemporalKind currentTemporalKind) =>
        contract with
        {
            TargetCapabilityFamily = SqlCanonicalTargetCapabilityFamily.CurrentTemporal,
            CurrentTemporalKind = currentTemporalKind
        };

    private static SqlCanonicalFunctionContract WithPlanShapeRules(
        SqlCanonicalFunctionContract contract,
        params SqlCanonicalPlanShapeRule[] planShapeRules) =>
        contract with { PlanShapeRules = planShapeRules.ToImmutableArray() };

    private static SqlCanonicalPlanShapeRule DistinctWildcardForbidden(
        int argumentIndex,
        string validationMessage) =>
        new(
            SqlCanonicalPlanShapeValidationKind.DistinctWildcardForbidden,
            argumentIndex,
            validationMessage,
            CapabilityId: null);

    private static SqlCanonicalPlanShapeRule LiteralStringRequired(
        int argumentIndex,
        string capabilityId) =>
        new(
            SqlCanonicalPlanShapeValidationKind.LiteralStringRequired,
            argumentIndex,
            ValidationMessage: null,
            capabilityId);

    private static SqlCanonicalLiteralArgumentRule PositiveInteger(
        int argumentIndex,
        string validationMessage) =>
        new(
            argumentIndex,
            SqlCanonicalLiteralArgumentValidationKind.PositiveInteger,
            validationMessage);

    private static SqlCanonicalLiteralArgumentRule WindowOffset(int argumentIndex) =>
        new(
            argumentIndex,
            SqlCanonicalLiteralArgumentValidationKind.WindowOffset,
            ValidationMessage: null);
}

internal enum SqlCanonicalFunctionKind
{
    Scalar,
    Aggregate,
    Window
}

internal enum SqlCanonicalTargetCapabilityFamily
{
    None,
    WindowFunction,
    TemporalFormat,
    Json,
    Regex,
    DatePart,
    DateMath,
    CurrentTemporal
}

internal enum SqlCanonicalNativeLoweringKind
{
    Ordinary,
    DateAdd,
    DateDiff,
    DatePart,
    DateFormat,
    DateParse,
    Position,
    JsonExtract,
    JsonSet,
    RegexMatch,
    CurrentDate,
    CurrentTime,
    CurrentTimestamp,
    StringAggregate
}

internal enum SqlCanonicalPlanShapeValidationKind
{
    DistinctWildcardForbidden,
    LiteralStringRequired
}

internal sealed record SqlCanonicalPlanShapeRule(
    SqlCanonicalPlanShapeValidationKind Kind,
    int ArgumentIndex,
    string? ValidationMessage,
    string? CapabilityId);

internal enum SqlCanonicalLiteralArgumentValidationKind
{
    PositiveInteger,
    WindowOffset
}

internal sealed record SqlCanonicalLiteralArgumentRule(
    int ArgumentIndex,
    SqlCanonicalLiteralArgumentValidationKind Kind,
    string? ValidationMessage);

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
    internal SqlCanonicalTargetCapabilityFamily TargetCapabilityFamily { get; init; } =
        SqlCanonicalTargetCapabilityFamily.None;

    internal ImmutableArray<SqlCanonicalLiteralArgumentRule> LiteralArgumentRules { get; init; } =
        ImmutableArray<SqlCanonicalLiteralArgumentRule>.Empty;

    internal bool IsWindowFrameInsensitive { get; init; }

    internal SqlCanonicalNativeLoweringKind NativeLoweringKind { get; init; } =
        SqlCanonicalNativeLoweringKind.Ordinary;

    internal SqlCurrentTemporalKind? CurrentTemporalKind { get; init; }

    internal ImmutableArray<SqlCanonicalPlanShapeRule> PlanShapeRules { get; init; } =
        ImmutableArray<SqlCanonicalPlanShapeRule>.Empty;

    internal bool AcceptsArgumentCount(int argumentCount) =>
        argumentCount >= MinArguments && argumentCount <= MaxArguments;
}
