using System.Text.RegularExpressions;

namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Provider-specific capability checks for already-normalized Core expressions. Keeping these
/// checks ahead of lowering prevents a supported Core shape from degenerating into SQL that the
/// selected backend cannot execute.
/// </summary>
internal static class CoreProviderCapabilityRules
{
    private static readonly Regex PortableJsonPropertyPath = new(
        @"^\$\.[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
        RegexOptions.CultureInvariant);

    public static void ValidateLiteral(LiteralExpr literal, SqlAgentToolType provider)
    {
        if (literal.Value is SqlTimeValue)
        {
            var error = SqlStandaloneTimeCapabilityRules.TargetValidationError(provider);
            if (error is not null)
                throw new SqlCompilationException(error);
        }

        if (literal.Value is SqlOffsetDateTimeValue or DateTimeOffset)
        {
            var error = SqlOffsetTimestampCapabilityRules.ProviderValidationError(
                provider);
            if (error is not null)
                throw new SqlCompilationException(error);
        }
    }

    public static void ValidateFunction(FunctionCallExpr function, SqlAgentToolType provider)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        switch (name)
        {
            case "NTH_VALUE":
                ValidateWindowFunctionCapability(name, provider);
                break;

            case "CORE_DATE_FORMAT":
            case "CORE_DATE_PARSE":
                ValidateTemporalFormatCapability(name, provider);
                break;

            case "CORE_JSON_EXTRACT":
            case "CORE_JSON_SET":
                ValidateJsonCapability(name, provider);
                break;

            case "CORE_REGEX_MATCH":
                ValidateRegexCapability(provider);
                break;

            case "CORE_DATE_PART":
                ValidateDatePart(function, provider);
                break;

            case "CORE_DATE_ADD":
            case "CORE_DATE_DIFF":
                ValidateDateMathUnit(function, provider, name);
                break;
        }

        if (name is "CORE_JSON_EXTRACT" or "CORE_JSON_SET")
            ValidateJsonPath(function, provider, name);

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

        var capabilityError = SqlWindowCapabilityRules.WindowValidationError(
            windowed,
            provider);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);
    }

    private static void ValidateWindowFunctionCapability(
        string functionName,
        SqlAgentToolType provider)
    {
        var error = SqlWindowCapabilityRules.FunctionValidationError(
            functionName,
            provider);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateTemporalFormatCapability(
        string functionName,
        SqlAgentToolType provider)
    {
        var error = SqlTemporalFormatCapabilityRules.TargetValidationError(
            functionName,
            provider);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateRegexCapability(
        SqlAgentToolType provider)
    {
        var error = SqlRegexCapabilityRules.ProviderValidationError(provider);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateDatePart(
        FunctionCallExpr function,
        SqlAgentToolType provider)
    {
        if (function.Arguments.IsDefaultOrEmpty
            || function.Arguments[0] is not LiteralExpr { Value: string rawPart })
        {
            throw new SqlCompilationException(
                "Canonical function 'CORE_DATE_PART' requires a literal date-part unit.");
        }

        var error = SqlDatePartCapabilityRules.TargetValidationError(rawPart, provider);
        if (error is not null)
            throw new SqlCompilationException(error);
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

        var error = SqlDateMathCapabilityRules.TargetValidationError(
            rawUnit,
            provider,
            functionName);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateJsonCapability(
        string functionName,
        SqlAgentToolType provider)
    {
        var error = SqlJsonCapabilityRules.TargetValidationError(
            functionName,
            provider);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateJsonPath(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        string functionName)
    {
        if (function.Arguments.Length < 2
            || function.Arguments[1] is not LiteralExpr { Value: string path })
        {
            throw CapabilityError(
                provider,
                "json.path.constant",
                $"{functionName} requires a constant JSON path in the portable Core model.");
        }

        if (!PortableJsonPropertyPath.IsMatch(path))
        {
            throw CapabilityError(
                provider,
                "json.path.property_chain",
                $"JSON path '{path}' is outside the portable Core property-chain subset. " +
                "Only paths such as '$.user.name' are supported; root-only paths, array indexes, wildcards, filters, quoted property names, and recursive descent fail closed.");
        }
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
            && function.Arguments.Length >= 2
            && TryIntegerLiteral(function.Arguments[1], out var offset))
        {
            var error = SqlWindowCapabilityRules.LiteralOffsetValidationError(
                name,
                offset,
                provider);
            if (error is not null)
                throw new SqlCompilationException(error);
        }
    }

    private static FunctionCallExpr? DirectWindowFunction(SqlExpr expression) => expression switch
    {
        FunctionCallExpr function => function,
        FilterExpr { Expression: FunctionCallExpr function } => function,
        _ => null
    };

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
            case LiteralExpr { Value: decimal v }
                when v == decimal.Truncate(v)
                    && v >= long.MinValue
                    && v <= long.MaxValue:
                value = (long)v;
                return true;
            case LiteralExpr { Value: double v }
                when double.IsFinite(v)
                    && v == Math.Truncate(v)
                    && v >= long.MinValue
                    && v <= long.MaxValue:
                value = (long)v;
                return true;
            case LiteralExpr { Value: float v }
                when float.IsFinite(v)
                    && v == MathF.Truncate(v)
                    && v >= long.MinValue
                    && v <= long.MaxValue:
                value = (long)v;
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private static SqlCompilationException CapabilityError(
        SqlAgentToolType provider,
        string capability,
        string? detail = null)
    {
        var prefix = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim() + " ";
        return new SqlCompilationException(
            $"{prefix}SQL capability '{capability}' is not supported by provider {provider} for this Core plan.");
    }
}
