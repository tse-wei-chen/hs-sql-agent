namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single target-provider contract for modeled window-function lowering. Pure AST/frame-bound
/// validity stays in the plan validator; this type owns provider-dependent function, ORDER BY,
/// frame acceptance, and SQL Server RANGE-offset restrictions.
/// </summary>
internal static class SqlWindowCapabilityRules
{
    internal static string? FunctionValidationError(
        string functionName,
        SqlAgentToolType provider) =>
        provider == SqlAgentToolType.MsSqlServer
            ? CapabilityError(
                provider,
                $"function.{functionName.Trim().ToLowerInvariant()}")
            : null;

    internal static string? LiteralOffsetValidationError(
        string functionName,
        long offset,
        SqlAgentToolType provider) =>
        offset < 0
        && provider is SqlAgentToolType.MsSqlServer or SqlAgentToolType.MySQL
            ? CapabilityError(
                provider,
                $"function.{functionName.Trim().ToLowerInvariant()}.negative_offset")
            : null;

    internal static string? WindowValidationError(
        WindowedExpr windowed,
        SqlAgentToolType provider)
    {
        ArgumentNullException.ThrowIfNull(windowed);

        var function = DirectWindowFunction(windowed.Expression);
        if (function is null)
            return null;

        var name = IdentifierText(function.Name).ToUpperInvariant();
        var contract = SqlCanonicalFunctionRegistry.Find(name);

        if (contract?.TargetCapabilityFamily == SqlCanonicalTargetCapabilityFamily.WindowFunction)
        {
            var functionError = FunctionValidationError(name, provider);
            if (functionError is not null)
                return functionError;
        }

        if (windowed.Window.Frame is not null
            && contract?.IsWindowFrameInsensitive == true
            && provider is SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle)
        {
            return CapabilityError(
                provider,
                $"window.frame.{name.ToLowerInvariant()}");
        }

        if (provider == SqlAgentToolType.MsSqlServer
            && contract?.Kind == SqlCanonicalFunctionKind.Window
            && windowed.Window.OrderBy.IsDefaultOrEmpty)
        {
            return CapabilityError(provider, "window.order_by");
        }

        if (provider == SqlAgentToolType.MsSqlServer
            && windowed.Window.Frame is { Unit: WindowFrameUnitKind.Range } frame
            && (HasOffsetBound(frame.Start)
                || frame.End is not null && HasOffsetBound(frame.End)))
        {
            return CapabilityError(provider, "window.range_offset");
        }

        return null;
    }

    internal static SqlCapability BasicMatrixCapability(
        SqlAgentToolType provider)
    {
        _ = provider;
        return new(
            "window.basic",
            "window",
            SqlCapabilityStatus.Translated,
            "OVER with PARTITION BY and ORDER BY is represented structurally; provider-specific function/order requirements are validated before lowering.");
    }

    internal static SqlCapability FrameMatrixCapability(
        SqlAgentToolType provider)
    {
        _ = provider;
        return new(
            "window.frame",
            "window",
            SqlCapabilityStatus.Translated,
            "ROWS/RANGE frames are represented structurally; provider/function combinations that do not accept a frame and SQL Server RANGE offsets fail closed before lowering.");
    }

    private static FunctionCallExpr? DirectWindowFunction(SqlExpr expression) => expression switch
    {
        FunctionCallExpr function => function,
        FilterExpr { Expression: FunctionCallExpr function } => function,
        _ => null
    };

    private static bool HasOffsetBound(WindowFrameBoundCore bound) =>
        bound.Kind is WindowFrameBoundKindCore.Preceding
            or WindowFrameBoundKindCore.Following;

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private static string CapabilityError(
        SqlAgentToolType provider,
        string capability) =>
        $"SQL capability '{capability}' is not supported by provider {provider} for this Core plan.";
}
