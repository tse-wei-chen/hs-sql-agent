namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single target-provider contract for the portable JSON extraction and mutation families.
/// JSON path shape validation remains a separate semantic contract in the provider validator and
/// renderer because path expressiveness is independent of whether a provider exposes a lowering.
/// </summary>
internal static class SqlJsonCapabilityRules
{
    internal static bool SupportsExtract(SqlAgentToolType provider) =>
        provider is SqlAgentToolType.Postgres
            or SqlAgentToolType.MySQL
            or SqlAgentToolType.Sqlite;

    internal static bool SupportsSet(SqlAgentToolType provider) =>
        provider is SqlAgentToolType.Postgres
            or SqlAgentToolType.MySQL
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.MsSqlServer;

    internal static string? TargetValidationError(
        string canonicalFunctionName,
        SqlAgentToolType provider) => canonicalFunctionName switch
    {
        "CORE_JSON_EXTRACT" when SupportsExtract(provider) => null,
        "CORE_JSON_EXTRACT" =>
            "SQL capability 'function.json_extract' is not supported by provider " +
            provider + " for this Core plan.",
        "CORE_JSON_SET" when SupportsSet(provider) => null,
        "CORE_JSON_SET" =>
            "SQL capability 'function.json_set' is not supported by provider " +
            provider + " for this Core plan.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(canonicalFunctionName),
            canonicalFunctionName,
            "Unsupported canonical JSON function.")
    };

    internal static SqlCapability ExtractMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "json.extract",
            "json",
            SupportsExtract(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            provider switch
            {
                SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle =>
                    "Ambiguous JSON_EXTRACT is rejected because the scalar/object result type is unknown; use an explicit JSON_VALUE or JSON_QUERY contract.",
                SqlAgentToolType.Firebird =>
                    "Portable JSON extraction has no declared Firebird equivalent.",
                _ =>
                    "Constant JSON property-chain paths such as $.user.name are normalized and translated; root-only, array-index, wildcard, filter, quoted-property, recursive-descent, and dynamic paths fail closed."
            });

    internal static SqlCapability SetMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "json.set",
            "json",
            SupportsSet(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            SupportsSet(provider)
                ? "Portable JSON mutation is rendered with provider-native functions after constant property-chain path validation."
                : "Portable JSON mutation has no declared equivalent for this provider.");
}
