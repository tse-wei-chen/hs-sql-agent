using System.Text.RegularExpressions;

namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single contract for portable JSON extraction/mutation target support and the shared constant
/// property-chain path subset used by semantic validation, native lowering, and capability-matrix
/// projection.
/// </summary>
internal static class SqlJsonCapabilityRules
{
    private static readonly Regex PortableJsonPropertyPath = new(
        @"^\$\.[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
        RegexOptions.CultureInvariant);

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

    internal static string? PathValidationError(
        FunctionCallExpr function,
        string canonicalFunctionName,
        SqlAgentToolType provider)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (function.Arguments.Length < 2
            || function.Arguments[1] is not LiteralExpr { Value: string path })
        {
            return CapabilityError(
                provider,
                "json.path.constant",
                $"{canonicalFunctionName} requires a constant JSON path in the portable Core model.");
        }

        if (!PortableJsonPropertyPath.IsMatch(path))
        {
            return CapabilityError(
                provider,
                "json.path.property_chain",
                $"JSON path '{path}' is outside the portable Core property-chain subset. " +
                "Only paths such as '$.user.name' are supported; root-only paths, array indexes, " +
                "wildcards, filters, quoted property names, and recursive descent fail closed.");
        }

        return null;
    }

    internal static IReadOnlyList<string> PropertyPathSegments(
        FunctionCallExpr function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (function.Arguments.Length < 2
            || function.Arguments[1] is not LiteralExpr { Value: string path }
            || !PortableJsonPropertyPath.IsMatch(path))
        {
            throw new SqlCompilationException(
                "Canonical JSON lowering requires a validated constant property-chain path.");
        }

        return path[2..].Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
    }

    internal static SqlCapability PathMatrixCapability() =>
        new(
            "json.path.simple",
            "json",
            SqlCapabilityStatus.Translated,
            "Portable JSON paths are limited to constant property chains beginning at $, for example $.user.name; root-only, array-index, wildcard, filter, quoted property names, recursive descent, and dynamic paths are rejected before lowering.");

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

    private static string CapabilityError(
        SqlAgentToolType provider,
        string capability,
        string detail) =>
        $"{detail.Trim()} SQL capability '{capability}' is not supported by provider {provider} for this Core plan.";

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
