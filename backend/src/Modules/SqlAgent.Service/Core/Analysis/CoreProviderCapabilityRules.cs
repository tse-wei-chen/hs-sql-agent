using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Analysis;

/// <summary>
/// Provider-specific capability checks for already-normalized Core expressions. Keeping these
/// checks ahead of lowering prevents a supported Core shape from degenerating into SQL that the
/// selected backend cannot execute.
/// </summary>
internal static class CoreProviderCapabilityRules
{
    private static readonly HashSet<string> ModeledWindowFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE", "NTILE"
    };

    private static readonly HashSet<string> FrameInsensitiveWindowFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST",
        "LAG", "LEAD", "NTILE"
    };

    public static void ValidateLiteral(LiteralExpr literal, SqlAgentToolType provider)
    {
        if (provider == SqlAgentToolType.Oracle && literal.Value is SqlTimeValue)
            throw CapabilityError(provider, "literal.time");

        if (provider == SqlAgentToolType.MySQL
            && literal.Value is SqlOffsetDateTimeValue or DateTimeOffset)
        {
            throw CapabilityError(provider, "literal.timestamp_offset");
        }
    }

    public static void ValidateFunction(FunctionCallExpr function, SqlAgentToolType provider)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        switch (name)
        {
            case "NTH_VALUE" when provider == SqlAgentToolType.MsSqlServer:
                throw CapabilityError(provider, "function.nth_value");

            case "CORE_DATE_FORMAT" when provider == SqlAgentToolType.Firebird:
                throw CapabilityError(provider, "function.date_format");

            case "CORE_DATE_PARSE" when provider is not (
                SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle):
                throw CapabilityError(provider, "function.date_parse");

            case "CORE_JSON_EXTRACT" when provider is not (
                SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite):
                throw CapabilityError(provider, "function.json_extract");

            case "CORE_JSON_SET" when provider is not (
                SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite
                    or SqlAgentToolType.MsSqlServer):
                throw CapabilityError(provider, "function.json_set");

            case "CORE_REGEX_MATCH" when provider is not (
                SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle):
                throw CapabilityError(provider, "function.regex_match");

            case "CORE_DATE_ADD":
            case "CORE_DATE_DIFF":
                ValidateDateMathUnit(function, provider, name);
                break;
        }

        ValidateLiteralWindowArgument(name, function, provider);
    }

    public static void ValidateWindow(WindowedExpr windowed, SqlAgentToolType provider)
    {
        var function = DirectWindowFunction(windowed.Expression);
        if (function is null) return;

        var name = IdentifierText(function.Name).ToUpperInvariant();
        if (function.IsDistinct)
        {
            throw new SqlCompilationException(
                $"DISTINCT window aggregate '{name}' is not a portable Core capability and is rejected before lowering.");
        }

        if (windowed.Window.Frame is not null && FrameInsensitiveWindowFunctions.Contains(name))
        {
            throw new SqlCompilationException(
                $"Window function '{name}' does not accept an explicit window frame in the Core pipeline.");
        }

        if (provider == SqlAgentToolType.MsSqlServer && ModeledWindowFunctions.Contains(name)
            && windowed.Window.OrderBy.IsDefaultOrEmpty)
        {
            throw CapabilityError(provider, "window.order_by");
        }

        if (provider == SqlAgentToolType.MsSqlServer
            && windowed.Window.Frame is { Unit: WindowFrameUnitKind.Range } frame
            && (HasOffsetBound(frame.Start) || frame.End is not null && HasOffsetBound(frame.End)))
        {
            throw CapabilityError(provider, "window.range_offset");
        }
    }

    private static void ValidateDateMathUnit(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        string functionName)
    {
        if (function.Arguments.IsDefaultOrEmpty
            || function.Arguments[0] is not LiteralExpr { Value: string rawUnit })
        {
            throw new SqlCompilationException(
                $"Canonical function '{functionName}' requires a literal date-part unit.");
        }

        var unit = rawUnit.Trim().ToUpperInvariant();
        if (provider is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Sqlite)
        {
            if (unit != "DAY")
                throw CapabilityError(provider, $"{functionName.ToLowerInvariant()}.unit.{unit.ToLowerInvariant()}");
            return;
        }

        // Firebird supports YEAR/MONTH/WEEK/DAY/HOUR/MINUTE/SECOND for the canonical units
        // represented by Core, but not QUARTER.
        if (provider == SqlAgentToolType.Firebird && unit == "QUARTER")
            throw CapabilityError(provider, $"{functionName.ToLowerInvariant()}.unit.quarter");
    }

    private static void ValidateLiteralWindowArgument(
        string name,
        FunctionCallExpr function,
        SqlAgentToolType provider)
    {
        if (name == "NTILE"
            && function.Arguments.Length == 1
            && TryIntegerLiteral(function.Arguments[0], out var buckets)
            && buckets <= 0)
        {
            throw new SqlCompilationException("NTILE bucket count must be a positive integer.");
        }

        if (name == "NTH_VALUE"
            && function.Arguments.Length >= 2
            && TryIntegerLiteral(function.Arguments[1], out var nth)
            && nth <= 0)
        {
            throw new SqlCompilationException("NTH_VALUE index must be a positive integer.");
        }

        if (name is "LAG" or "LEAD"
            && provider is SqlAgentToolType.MsSqlServer or SqlAgentToolType.MySQL
            && function.Arguments.Length >= 2
            && TryIntegerLiteral(function.Arguments[1], out var offset)
            && offset < 0)
        {
            throw CapabilityError(provider, $"function.{name.ToLowerInvariant()}.negative_offset");
        }
    }

    private static FunctionCallExpr? DirectWindowFunction(SqlExpr expression) => expression switch
    {
        FunctionCallExpr function => function,
        FilterExpr { Expression: FunctionCallExpr function } => function,
        _ => null
    };

    private static bool HasOffsetBound(WindowFrameBoundCore bound) =>
        bound.Kind is WindowFrameBoundKindCore.Preceding or WindowFrameBoundKindCore.Following;

    private static bool TryIntegerLiteral(SqlExpr expression, out long value)
    {
        switch (expression)
        {
            case LiteralExpr { Value: sbyte v }: value = v; return true;
            case LiteralExpr { Value: byte v }: value = v; return true;
            case LiteralExpr { Value: short v }: value = v; return true;
            case LiteralExpr { Value: ushort v }: value = v; return true;
            case LiteralExpr { Value: int v }: value = v; return true;
            case LiteralExpr { Value: uint v }: value = v; return true;
            case LiteralExpr { Value: long v }: value = v; return true;
            default:
                value = default;
                return false;
        }
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private static SqlCompilationException CapabilityError(
        SqlAgentToolType provider,
        string capability) =>
        new($"SQL capability '{capability}' is not supported by provider {provider} for this Core plan.");
}
