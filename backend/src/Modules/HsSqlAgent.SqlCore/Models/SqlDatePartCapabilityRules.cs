namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single canonical date-part contract shared by raw EXTRACT parsing, normalization, provider
/// validation, native rendering, and the public capability matrix. Adding a new represented unit
/// must happen here first so the compiler cannot advertise, accept, and render different subsets.
/// </summary>
internal static class SqlDatePartCapabilityRules
{
    internal static bool IsRepresentedPart(string rawPart) =>
        NormalizePart(rawPart) is "YEAR" or "MONTH" or "DAY" or "QUARTER";

    internal static bool SupportsTarget(
        string rawPart,
        SqlAgentToolType provider)
    {
        var part = NormalizePart(rawPart);
        return part switch
        {
            "YEAR" or "MONTH" or "DAY" => true,
            "QUARTER" => provider == SqlAgentToolType.Postgres,
            _ => false
        };
    }

    internal static string? TargetValidationError(
        string rawPart,
        SqlAgentToolType provider)
    {
        var part = NormalizePart(rawPart);
        if (!IsRepresentedPart(part))
        {
            return
                "Date part " + part + " is outside the declared Core date-part subset. " +
                "SQL capability 'temporal.date_part." + part.ToLowerInvariant() +
                "' is not supported by provider " + provider + " for this Core plan.";
        }

        if (SupportsTarget(part, provider))
            return null;

        return
            "SQL capability 'temporal.date_part." + part.ToLowerInvariant() +
            "' is not supported by provider " + provider + " for this Core plan.";
    }

    internal static SqlCapability QuarterMatrixCapability(SqlAgentToolType provider)
    {
        var supported = SupportsTarget("QUARTER", provider);
        return new(
            "temporal.date_part.quarter",
            "temporal",
            supported
                ? SqlCapabilityStatus.Supported
                : SqlCapabilityStatus.Rejected,
            supported
                ? "PostgreSQL EXTRACT(QUARTER FROM value) is represented by the canonical date-part family and rendered natively."
                : "QUARTER date-part lowering remains fail-closed for this target until an explicit provider semantic contract is declared.");
    }

    private static string NormalizePart(string rawPart) =>
        rawPart.Trim().ToUpperInvariant();
}
