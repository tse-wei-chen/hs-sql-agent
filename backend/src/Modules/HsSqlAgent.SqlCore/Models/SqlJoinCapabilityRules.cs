namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single target-provider contract for JOIN families whose support is not universal. MySQL has no
/// native FULL OUTER JOIN and Core has no declared semantics-preserving emulation; other declared
/// target providers retain the existing native FULL OUTER JOIN lowering.
/// </summary>
internal static class SqlJoinCapabilityRules
{
    internal static bool SupportsFullJoin(SqlAgentToolType provider) =>
        provider != SqlAgentToolType.MySQL;

    internal static string? TargetValidationError(
        string joinKind,
        SqlAgentToolType provider)
    {
        if (!joinKind.Equals("FULL", StringComparison.OrdinalIgnoreCase)
            || SupportsFullJoin(provider))
        {
            return null;
        }

        return
            "SQL capability 'join.full' is not supported by provider MySQL for this Core plan.";
    }

    internal static SqlCapability FullJoinMatrixCapability(
        SqlAgentToolType provider) =>
        new(
            "join.full",
            "query",
            SupportsFullJoin(provider)
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            SupportsFullJoin(provider)
                ? "FULL OUTER JOIN is represented structurally and emitted with the provider's native FULL OUTER JOIN syntax."
                : "MySQL has no native FULL OUTER JOIN and Core has no declared semantics-preserving emulation, so FULL JOIN remains fail-closed.");
}
