namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Source/target contract for JOIN families whose availability depends on provider/runtime.
/// SQLite added RIGHT and FULL OUTER JOIN in 3.39.0; MySQL has no FULL OUTER JOIN.
/// </summary>
internal static class SqlJoinCapabilityRules
{
    internal static readonly Version SqliteRightFullMinimumVersion = new(3, 39);

    internal static bool SupportsRightJoin(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        TargetValidationError("RIGHT", provider, targetProfile) is null;

    internal static bool SupportsFullJoin(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile = null) =>
        TargetValidationError("FULL", provider, targetProfile) is null;

    internal static string? ProviderValidationError(
        string joinKind,
        SqlAgentToolType provider)
    {
        var kind = NormalizeJoinKind(joinKind);
        if (kind == "FULL" && provider == SqlAgentToolType.MySQL)
        {
            return
                "SQL capability 'join.full' is not supported by provider MySQL for this Core plan.";
        }

        return null;
    }

    internal static string? SourceValidationError(
        string joinKind,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile)
    {
        var kind = NormalizeJoinKind(joinKind);

        if (kind == "FULL" && sourceDialect == SqlAgentToolType.MySQL)
        {
            return
                "Raw MySQL FULL OUTER JOIN is not valid source syntax. " +
                "SQL capability 'join.full' is not supported by source provider MySQL.";
        }

        if (sourceDialect == SqlAgentToolType.Sqlite
            && kind is "RIGHT" or "FULL")
        {
            return SqliteVersionValidationError(
                kind,
                sourceProfile,
                side: "source");
        }

        return null;
    }

    internal static string? TargetValidationError(
        string joinKind,
        SqlAgentToolType provider) =>
        ProviderValidationError(joinKind, provider);

    internal static string? TargetValidationError(
        string joinKind,
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        var providerError = ProviderValidationError(joinKind, provider);
        if (providerError is not null)
            return providerError;

        var kind = NormalizeJoinKind(joinKind);
        if (provider == SqlAgentToolType.Sqlite
            && kind is "RIGHT" or "FULL")
        {
            return SqliteVersionValidationError(
                kind,
                targetProfile,
                side: "target");
        }

        return null;
    }

    internal static SqlCapability RightJoinMatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        var supported = SupportsRightJoin(provider, targetProfile);
        return new(
            "join.right",
            "query",
            supported
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            provider == SqlAgentToolType.Sqlite
                ? supported
                    ? $"SQLite {targetProfile!.ServerVersion} satisfies the 3.39+ RIGHT JOIN runtime contract; Core emits native RIGHT JOIN syntax."
                    : "SQLite RIGHT JOIN remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.39 or newer."
                : "RIGHT JOIN is represented structurally and emitted with the provider's native RIGHT JOIN syntax.");
    }

    internal static SqlCapability FullJoinMatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile = null)
    {
        var supported = SupportsFullJoin(provider, targetProfile);
        return new(
            "join.full",
            "query",
            supported
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            provider switch
            {
                SqlAgentToolType.MySQL =>
                    "MySQL has no native FULL OUTER JOIN and Core has no declared semantics-preserving emulation, so FULL JOIN remains fail-closed.",
                SqlAgentToolType.Sqlite when supported =>
                    $"SQLite {targetProfile!.ServerVersion} satisfies the 3.39+ FULL OUTER JOIN runtime contract; Core emits native FULL OUTER JOIN syntax.",
                SqlAgentToolType.Sqlite =>
                    "SQLite FULL OUTER JOIN remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.39 or newer.",
                _ =>
                    "FULL OUTER JOIN is represented structurally and emitted with the provider's native FULL OUTER JOIN syntax."
            });
    }

    private static string? SqliteVersionValidationError(
        string joinKind,
        SqlProviderCapabilityProfile? profile,
        string side)
    {
        var capability = joinKind == "RIGHT"
            ? "join.right"
            : "join.full";

        if (profile is null || profile.ServerVersion is null)
        {
            return
                $"SQL capability '{capability}' requires a declared SQLite {side} capability profile " +
                $"with ServerVersion {SqliteRightFullMinimumVersion}+.";
        }

        return profile.ServerVersion.CompareTo(SqliteRightFullMinimumVersion) < 0
            ? $"SQL capability '{capability}' requires SQLite {side} ServerVersion " +
              $"{SqliteRightFullMinimumVersion}+; declared version is {profile.ServerVersion}."
            : null;
    }

    private static string NormalizeJoinKind(string joinKind) =>
        joinKind.Trim().ToUpperInvariant();
}
