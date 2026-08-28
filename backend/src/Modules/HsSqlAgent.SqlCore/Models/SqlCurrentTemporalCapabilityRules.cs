namespace HsSqlAgent.SqlCore.Models;

internal enum SqlCurrentTemporalKind
{
    Date,
    Time,
    Timestamp
}

/// <summary>
/// Separates raw source spelling from target capability for CURRENT_DATE, CURRENT_TIME, and
/// CURRENT_TIMESTAMP. SQL Server raw source syntax does not accept CURRENT_DATE/CURRENT_TIME even
/// though Core can lower those canonical values for SQL Server targets. Oracle has no standalone
/// TIME target contract, so CURRENT_TIME is rejected there on both source and target paths.
/// </summary>
internal static class SqlCurrentTemporalCapabilityRules
{
    internal static bool SupportsRawSource(
        SqlCurrentTemporalKind kind,
        SqlAgentToolType sourceDialect) => kind switch
    {
        SqlCurrentTemporalKind.Date =>
            sourceDialect != SqlAgentToolType.MsSqlServer,

        SqlCurrentTemporalKind.Time =>
            sourceDialect is not (SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle),

        SqlCurrentTemporalKind.Timestamp => true,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    internal static bool SupportsTarget(
        SqlCurrentTemporalKind kind,
        SqlAgentToolType provider) => kind switch
    {
        SqlCurrentTemporalKind.Date => true,
        SqlCurrentTemporalKind.Time => provider != SqlAgentToolType.Oracle,
        SqlCurrentTemporalKind.Timestamp => true,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    internal static string? TargetValidationError(
        SqlCurrentTemporalKind kind,
        SqlAgentToolType provider)
    {
        if (SupportsTarget(kind, provider))
            return null;

        var capability = kind switch
        {
            SqlCurrentTemporalKind.Date => "function.current_date",
            SqlCurrentTemporalKind.Time => "function.current_time",
            SqlCurrentTemporalKind.Timestamp => "function.current_timestamp",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        return
            $"SQL capability '{capability}' is not supported by provider {provider} for this Core plan.";
    }

    internal static string? SourceValidationError(
        SqlCurrentTemporalKind kind,
        SqlAgentToolType sourceDialect)
    {
        if (SupportsRawSource(kind, sourceDialect))
            return null;

        var function = kind switch
        {
            SqlCurrentTemporalKind.Date => "CURRENT_DATE",
            SqlCurrentTemporalKind.Time => "CURRENT_TIME",
            SqlCurrentTemporalKind.Timestamp => "CURRENT_TIMESTAMP",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        var detail = kind switch
        {
            SqlCurrentTemporalKind.Date =>
                "CURRENT_DATE is not Transact-SQL source syntax.",
            SqlCurrentTemporalKind.Time =>
                "CURRENT_TIME is not modeled as SQL Server or Oracle source syntax.",
            _ => throw new InvalidOperationException(
                $"No source restriction detail is defined for {kind}.")
        };

        return $"Function '{function}' is not valid for declared source dialect {sourceDialect} in the Core source capability profile. {detail}";
    }

    internal static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        new(
            "temporal.current_keywords",
            "temporal",
            provider == SqlAgentToolType.Oracle
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Supported,
            provider == SqlAgentToolType.Oracle
                ? "CURRENT_DATE and CURRENT_TIMESTAMP are supported; CURRENT_TIME is rejected because Oracle has no standalone TIME type."
                : "CURRENT_DATE, CURRENT_TIME, and CURRENT_TIMESTAMP are emitted with provider-specific translation where needed.");
}
