namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Central target-runtime contract for canonical offset timestamp values. MySQL has no native
/// offset-preserving timestamp type. Firebird exposes TIMESTAMP WITH TIME ZONE only from 4.0, so
/// that target remains fail-closed unless the runtime version is explicitly declared.
/// </summary>
internal static class SqlOffsetTimestampCapabilityRules
{
    private static readonly Version FirebirdTimeZoneVersion = new(4, 0);

    internal static bool SupportsTarget(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile) => provider switch
    {
        SqlAgentToolType.MySQL => false,
        SqlAgentToolType.Firebird =>
            targetProfile is
            {
                Provider: SqlAgentToolType.Firebird,
                ServerVersion: { } version
            }
            && version.CompareTo(FirebirdTimeZoneVersion) >= 0,
        SqlAgentToolType.Postgres
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle => true,
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "Unsupported SQL provider.")
    };

    internal static string? ProviderValidationError(
        SqlAgentToolType provider) =>
        provider == SqlAgentToolType.MySQL
            ? TargetValidationError(provider, targetProfile: null)
            : null;

    internal static string? TargetValidationError(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (SupportsTarget(provider, targetProfile))
            return null;

        return provider switch
        {
            SqlAgentToolType.MySQL =>
                "SQL capability 'temporal.offset_timestamp' is not supported by MySQL because it has no native timestamp type that preserves an input UTC offset.",
            SqlAgentToolType.Firebird =>
                "SQL capability 'temporal.offset_timestamp' requires an explicit Firebird target capability profile with ServerVersion 4.0 or newer because TIMESTAMP WITH TIME ZONE was introduced in Firebird 4.0.",
            _ => "SQL capability 'temporal.offset_timestamp' is not supported by the declared target profile."
        };
    }

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        var supported = SupportsTarget(provider, targetProfile);
        return new SqlCapability(
            "temporal.offset_timestamp",
            "temporal",
            supported ? SqlCapabilityStatus.Translated : SqlCapabilityStatus.Rejected,
            provider switch
            {
                SqlAgentToolType.MySQL =>
                    "MySQL has no native timestamp type that preserves an input UTC offset; offset values are rejected.",
                SqlAgentToolType.Firebird when supported =>
                    "Firebird 4.0+ offset timestamps lower through TIMESTAMP WITH TIME ZONE under an explicit target runtime profile.",
                SqlAgentToolType.Firebird =>
                    "Firebird offset timestamps require an explicit target capability profile with ServerVersion 4.0 or newer; unknown and older runtimes remain fail-closed.",
                _ =>
                    "Offset timestamps use the provider's declared scalar timestamp representation; PostgreSQL normalizes the represented instant to UTC."
            });
    }
}
