namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Target lowering contract for canonical DELETE ... USING. PostgreSQL is the first proven target;
/// providers with joined-delete syntax remain fail-closed until target-row and duplicate-match
/// semantics are modeled explicitly.
/// </summary>
internal static class SqlDmlDeleteUsingCapabilityRules
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

        return $"SQL capability 'dml.delete.using' remains fail-closed for provider {provider}; " +
               "equivalent joined-delete, target-row, alias, and duplicate-match semantics are not yet proven.";
    }

    internal static SqlCapability MatrixCapability(SqlAgentToolType provider) =>
        new(
            "dml.delete.using",
            "dml",
            SupportsTarget(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            SupportsTarget(provider)
                ? "Canonical DELETE ... USING lowers natively for PostgreSQL. USING sources participate in binding and authorization facts; mutation policy still requires an approved WHERE unless explicitly overridden."
                : $"DELETE ... USING remains fail-closed for {provider} until provider-specific joined-delete semantics are proven.");
}
