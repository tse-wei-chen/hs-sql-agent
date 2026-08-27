namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single canonical unit and target-provider contract for DATEADD/DATEDIFF lowering. Source-dialect
/// DATEDIFF boundary semantics stay in CoreDateDiffNormalizer because those are semantic
/// translation rules, not target syntax capabilities.
/// </summary>
internal static class SqlDateMathCapabilityRules
{
    internal static string NormalizeUnit(
        string rawUnit,
        string surfaceName)
    {
        var unit = rawUnit.Trim().ToUpperInvariant();
        return unit switch
        {
            "DAY" or "DD" or "D" => "DAY",
            "WEEK" or "WK" or "WW" => "WEEK",
            "MONTH" or "MM" or "M" => "MONTH",
            "QUARTER" or "QQ" or "Q" => "QUARTER",
            "YEAR" or "YY" or "YYYY" => "YEAR",
            "HOUR" or "HH" => "HOUR",
            "MINUTE" or "MI" or "N" => "MINUTE",
            "SECOND" or "SS" or "S" => "SECOND",
            _ => throw new SqlCompilationException(
                $"Unsupported {surfaceName} date-part unit '{rawUnit}'.")
        };
    }

    internal static bool SupportsTarget(
        string rawUnit,
        SqlAgentToolType provider)
    {
        var unit = NormalizeUnit(rawUnit, "DATEADD/DATEDIFF");
        return provider switch
        {
            SqlAgentToolType.Postgres
                or SqlAgentToolType.Oracle
                or SqlAgentToolType.Sqlite => unit == "DAY",
            SqlAgentToolType.Firebird => unit != "QUARTER",
            SqlAgentToolType.MySQL
                or SqlAgentToolType.MsSqlServer => true,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported SQL provider.")
        };
    }

    internal static string? TargetValidationError(
        string rawUnit,
        SqlAgentToolType provider,
        string functionName)
    {
        var surfaceName = functionName switch
        {
            "CORE_DATE_ADD" => "DATEADD",
            "CORE_DATE_DIFF" => "DATEDIFF",
            _ => throw new ArgumentOutOfRangeException(
                nameof(functionName),
                functionName,
                "Unsupported canonical date-math function.")
        };
        var unit = NormalizeUnit(rawUnit, surfaceName);
        if (SupportsTarget(unit, provider))
            return null;

        return
            $"{surfaceName} unit {unit} is not supported by {provider}. " +
            $"SQL capability '{functionName.ToLowerInvariant()}.unit.{unit.ToLowerInvariant()}' " +
            $"is not supported by provider {provider} for this Core plan.";
    }

    internal static SqlCapability MatrixCapability(SqlAgentToolType provider)
    {
        _ = provider;
        return new(
            "temporal.date_arithmetic",
            "temporal",
            SqlCapabilityStatus.Translated,
            "Raw SQL DATEADD/DATEDIFF input is accepted only in declared source-dialect forms, while structured Core input can use the portable date-arithmetic shapes independently of source-native syntax. Cross-dialect semantics and target-specific unit restrictions are validated before lowering.");
    }
}
