namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single target-provider contract for canonical date formatting and formatted date parsing.
/// Format-token translation remains owned by DateFormatTranslator; this type owns only whether
/// the target provider has a declared lowering and how that capability projects into the matrix.
/// </summary>
internal static class SqlTemporalFormatCapabilityRules
{
    internal static bool SupportsDateFormat(SqlAgentToolType provider) =>
        provider != SqlAgentToolType.Firebird;

    internal static bool SupportsFormattedParse(SqlAgentToolType provider) =>
        provider is SqlAgentToolType.Postgres
            or SqlAgentToolType.MySQL
            or SqlAgentToolType.Oracle;

    internal static string? TargetValidationError(
        string canonicalFunctionName,
        SqlAgentToolType provider) => canonicalFunctionName switch
    {
        "CORE_DATE_FORMAT" when SupportsDateFormat(provider) => null,
        "CORE_DATE_FORMAT" =>
            "portable date formatting is not supported by Firebird. " +
            "SQL capability 'function.date_format' is not supported by provider " +
            provider + " for this Core plan.",
        "CORE_DATE_PARSE" when SupportsFormattedParse(provider) => null,
        "CORE_DATE_PARSE" =>
            "formatted date parsing is not supported by this provider. " +
            "SQL capability 'function.date_parse' is not supported by provider " +
            provider + " for this Core plan.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(canonicalFunctionName),
            canonicalFunctionName,
            "Unsupported canonical temporal format function.")
    };

    internal static SqlCapability DateFormatMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "temporal.date_format",
            "temporal",
            SupportsDateFormat(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            SupportsDateFormat(provider)
                ? "Declared source date-format functions and tokens are normalized and translated to provider-native syntax."
                : "Portable date formatting is rejected because no complete translation is declared.");

    internal static SqlCapability FormattedParseMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "temporal.formatted_parse",
            "temporal",
            SupportsFormattedParse(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            SupportsFormattedParse(provider)
                ? "TO_DATE input and format tokens are translated to the provider-native function."
                : "Formatted date parsing is rejected because no complete provider translation is declared.");
}
