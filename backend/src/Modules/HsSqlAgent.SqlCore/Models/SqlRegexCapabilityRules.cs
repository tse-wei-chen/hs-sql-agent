namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single target-provider and runtime-profile contract for portable regex matching. SQL Server
/// reaches the provider-profile rewrite stage because REGEXP_LIKE is available only under the
/// declared SQL Server 17.x / compatibility-level-170 contract; SQLite and Firebird remain
/// provider-wide rejections.
/// </summary>
internal static class SqlRegexCapabilityRules
{
    internal static readonly Version SqlServerMinimumVersion = new(17, 0);
    internal const int SqlServerMinimumCompatibilityLevel = 170;

    internal static bool RequiresTargetProfileRewrite(
        SqlAgentToolType provider) =>
        provider == SqlAgentToolType.MsSqlServer;

    internal static string? ProviderValidationError(
        SqlAgentToolType provider) =>
        provider is SqlAgentToolType.Sqlite or SqlAgentToolType.Firebird
            ? "SQL capability 'function.regex_match' is not supported by provider " +
              provider + " for this Core plan."
            : null;

    internal static bool SupportsTarget(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile) => provider switch
    {
        SqlAgentToolType.Postgres
            or SqlAgentToolType.MySQL
            or SqlAgentToolType.Oracle => true,
        SqlAgentToolType.MsSqlServer =>
            targetProfile is
            {
                Provider: SqlAgentToolType.MsSqlServer,
                ServerVersion: { } version,
                CompatibilityLevel: >= SqlServerMinimumCompatibilityLevel
            }
            && version.CompareTo(SqlServerMinimumVersion) >= 0,
        SqlAgentToolType.Sqlite
            or SqlAgentToolType.Firebird => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "Unsupported SQL provider.")
    };

    internal static string? TargetValidationError(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (SupportsTarget(provider, targetProfile))
            return null;

        return provider == SqlAgentToolType.MsSqlServer
            ? "SQL capability 'function.regex_match' requires a declared SQL Server target capability profile with ServerVersion 17.0 or newer and compatibility level 170 or above."
            : "SQL capability 'function.regex_match' is not supported by provider " +
              provider + " for this Core plan.";
    }

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        var supported = SupportsTarget(provider, targetProfile);
        return new(
            "regex.match",
            "regex",
            supported
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            provider switch
            {
                SqlAgentToolType.Postgres
                    or SqlAgentToolType.MySQL
                    or SqlAgentToolType.Oracle =>
                    "REGEXP_LIKE semantics are rendered using the provider's declared regex syntax.",
                SqlAgentToolType.MsSqlServer when supported =>
                    "SQL Server REGEXP_LIKE is enabled by the declared SQL Server 17.x+ target profile at compatibility level 170 or above and is emitted natively.",
                SqlAgentToolType.MsSqlServer =>
                    "SQL Server REGEXP_LIKE requires a declared target capability profile with ServerVersion 17.0+ and compatibility level 170 or above; absent, older, or lower-compatibility profiles remain fail-closed.",
                _ =>
                    "Regex matching is rejected because no reliable native equivalent is declared."
            });
    }
}
