namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Target lowering contract for canonical UPDATE ... FROM. The first proven slice intentionally
/// declares PostgreSQL only. Providers with superficially similar syntax remain fail-closed until
/// their target-row, join, duplicate-match, alias, and version semantics are modeled explicitly.
/// </summary>
internal static class SqlDmlUpdateFromCapabilityRules
{
    internal static bool SupportsTarget(SqlAgentToolType provider) => provider switch
    {
        SqlAgentToolType.Postgres => true,
        SqlAgentToolType.MySQL
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.Firebird => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "Unsupported SQL provider.")
    };

    internal static string? TargetValidationError(SqlAgentToolType provider)
    {
        if (SupportsTarget(provider))
            return null;

        return $"SQL capability 'dml.update.from' remains fail-closed for provider {provider}; " +
               "equivalent mutation, duplicate-match, alias, and runtime-version semantics are not yet proven.";
    }

    internal static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        new(
            "dml.update.from",
            "dml",
            SupportsTarget(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            SupportsTarget(provider)
                ? "Canonical UPDATE ... FROM lowers natively for PostgreSQL. FROM sources participate in binding and authorization facts; mutation policy still requires an approved WHERE unless explicitly overridden."
                : $"UPDATE ... FROM remains fail-closed for {provider} until provider-specific mutation semantics are proven.");
}
