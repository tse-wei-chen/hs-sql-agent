namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Provider-specific capability checks for already-normalized Core expressions. Keeping these
/// checks ahead of lowering prevents a supported Core shape from degenerating into SQL that the
/// selected backend cannot execute.
/// </summary>
internal static class CoreProviderCapabilityRules
{
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
        var contract = SqlCanonicalFunctionRegistry.Find(name);
        if (contract is null)
            return;

        ValidateTargetCapability(contract, function, provider);
        ValidateLiteralArgumentRules(contract, function, provider);
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

    private static void ValidateTargetCapability(
        SqlCanonicalFunctionContract contract,
        FunctionCallExpr function,
        SqlAgentToolType provider)
    {
        switch (contract.TargetCapabilityFamily)
        {
            case SqlCanonicalTargetCapabilityFamily.None:
                return;
            case SqlCanonicalTargetCapabilityFamily.WindowFunction:
                ValidateWindowFunctionCapability(contract.Name, provider);
                return;
            case SqlCanonicalTargetCapabilityFamily.TemporalFormat:
                ValidateTemporalFormatCapability(contract.Name, provider);
                return;
            case SqlCanonicalTargetCapabilityFamily.Json:
                ValidateJsonCapability(contract.Name, provider);
                ValidateJsonPath(function, provider, contract.Name);
                return;
            case SqlCanonicalTargetCapabilityFamily.Regex:
                ValidateRegexCapability(provider);
                return;
            case SqlCanonicalTargetCapabilityFamily.DatePart:
                ValidateDatePart(function, provider);
                return;
            case SqlCanonicalTargetCapabilityFamily.DateMath:
                ValidateDateMathUnit(function, provider, contract.Name);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported canonical target capability family '{contract.TargetCapabilityFamily}' for function '{contract.Name}'.");
        }
    }

    private static void ValidateLiteralArgumentRules(
        SqlCanonicalFunctionContract contract,
        FunctionCallExpr function,
        SqlAgentToolType provider)
    {
        foreach (var rule in contract.LiteralArgumentRules)
        {
            if (rule.ArgumentIndex < 0)
            {
                throw new SqlCompilationException(
                    $"Canonical function '{contract.Name}' declares an invalid literal argument index {rule.ArgumentIndex}.");
            }

            if (function.Arguments.Length <= rule.ArgumentIndex
                || !TryIntegerLiteral(function.Arguments[rule.ArgumentIndex], out var value))
            {
                continue;
            }

            switch (rule.Kind)
            {
                case SqlCanonicalLiteralArgumentValidationKind.PositiveInteger:
                    if (value <= 0)
                    {
                        throw new SqlCompilationException(
                            rule.ValidationMessage
                            ?? $"Canonical function '{contract.Name}' requires a positive integer argument.");
                    }
                    break;

                case SqlCanonicalLiteralArgumentValidationKind.WindowOffset:
                    var error = SqlWindowCapabilityRules.LiteralOffsetValidationError(
                        contract.Name,
                        value,
                        provider);
                    if (error is not null)
                        throw new SqlCompilationException(error);
                    break;

                default:
                    throw new SqlCompilationException(
                        $"Unsupported canonical literal argument rule '{rule.Kind}' for function '{contract.Name}'.");
            }
        }
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
        var error = SqlJsonCapabilityRules.PathValidationError(
            function,
            functionName,
            provider);
        if (error is not null)
            throw new SqlCompilationException(error);
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
