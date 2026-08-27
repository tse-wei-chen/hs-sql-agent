namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single source/target provider-runtime contract for deterministic INSERT conflict handling.
/// PostgreSQL ON CONFLICT is directly declared and SQLite requires ServerVersion 3.24+. MySQL and
/// Firebird expose distinct native source forms and conditional statement-assured target lowering,
/// while SQL Server and Oracle remain MERGE-gated.
/// </summary>
internal static class SqlDmlUpsertCapabilityRules
{
    private static readonly Version SqliteUpsertVersion = new(3, 24);
    private static readonly Version MySqlProposedRowAliasVersion = new(8, 0, 19);

    internal static bool SupportsOnConflictSource(
        SqlAgentToolType sourceDialect,
        Version? sourceServerVersion) => sourceDialect switch
    {
        SqlAgentToolType.Postgres => true,
        SqlAgentToolType.Sqlite =>
            IsAtLeast(sourceServerVersion, SqliteUpsertVersion),
        SqlAgentToolType.MySQL
            or SqlAgentToolType.Firebird
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(sourceDialect),
            sourceDialect,
            "Unsupported SQL source dialect.")
    };

    internal static string? OnConflictSourceValidationError(
        SqlAgentToolType sourceDialect,
        Version? sourceServerVersion)
    {
        if (SupportsOnConflictSource(sourceDialect, sourceServerVersion))
            return null;

        return sourceDialect switch
        {
            SqlAgentToolType.Sqlite =>
                "Raw SQLite UPSERT requires a source capability profile with ServerVersion 3.24 or newer.",
            SqlAgentToolType.MySQL =>
                "MySQL ON DUPLICATE KEY UPDATE has no explicit conflict target and is not represented by the deterministic portable upsert contract.",
            SqlAgentToolType.Firebird =>
                "Firebird source upsert uses UPDATE OR INSERT ... MATCHING rather than ON CONFLICT; use the native explicit MATCHING form so Core can preserve source semantics.",
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle =>
                $"Source dialect {sourceDialect} uses MERGE-style upsert semantics, which require a separate source-row cardinality contract and remain fail-closed.",
            _ =>
                $"Portable INSERT conflict handling is not represented for source dialect {sourceDialect}."
        };
    }

    internal static string? FirebirdUpdateOrInsertSourceValidationError(
        SqlAgentToolType sourceDialect) =>
        sourceDialect == SqlAgentToolType.Firebird
            ? null
            : $"UPDATE OR INSERT is Firebird source syntax and is not valid for source dialect {sourceDialect}.";

    internal static bool SupportsDirectTarget(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile) => provider switch
    {
        SqlAgentToolType.Postgres => true,
        SqlAgentToolType.Sqlite =>
            IsAtLeast(
                targetProfile,
                SqlAgentToolType.Sqlite,
                SqliteUpsertVersion),
        SqlAgentToolType.MySQL
            or SqlAgentToolType.Firebird
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "Unsupported SQL provider.")
    };

    internal static string? DirectTargetValidationError(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (SupportsDirectTarget(provider, targetProfile))
            return null;

        return provider switch
        {
            SqlAgentToolType.Sqlite =>
                "SQLite UPSERT requires an explicit target capability profile with ServerVersion 3.24 or newer.",
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.Oracle =>
                $"Target provider {provider} requires MERGE-style source/match semantics; portable MERGE remains fail-closed until Core models source-row cardinality and match guarantees.",
            SqlAgentToolType.MySQL or SqlAgentToolType.Firebird =>
                $"Portable INSERT conflict handling is not represented as an unconditional target capability for provider {provider}.",
            _ =>
                $"Portable INSERT conflict handling is not represented for target provider {provider}."
        };
    }

    internal static string? MySqlConditionalTargetValidationError(
        SqlProviderCapabilityProfile? targetProfile) =>
        IsAtLeast(
            targetProfile,
            SqlAgentToolType.MySQL,
            MySqlProposedRowAliasVersion)
            ? null
            : "MySQL conflict lowering requires an explicit target capability profile with ServerVersion 8.0.19 or newer so Core can use the proposed-row alias form instead of deprecated VALUES(column) semantics.";

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        var direct = SupportsDirectTarget(provider, targetProfile);
        return new(
            "dml.upsert_merge",
            "dml",
            direct
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            direct
                ? provider == SqlAgentToolType.Postgres
                    ? "PostgreSQL supports the deterministic Core INSERT VALUES conflict contract with an explicit conflict-column target. DO NOTHING permits multiple proposed rows; DO UPDATE is limited to exactly one proposed row and closed assignments of the form target = EXCLUDED.source. Arbitrary expressions, predicates, named constraints, partial-index predicates, INSERT ... SELECT upsert, and typed approval execution remain fail-closed."
                    : "SQLite ServerVersion 3.24+ target profiles support the deterministic Core INSERT VALUES conflict contract with an explicit conflict-column target. DO NOTHING permits multiple proposed rows; DO UPDATE is limited to exactly one proposed row and target = EXCLUDED.source assignments. The target version must be explicit; richer SQLite UPSERT grammar and typed approval execution remain fail-closed."
                : provider switch
                {
                    SqlAgentToolType.Sqlite =>
                        "SQLite UPSERT remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.24 or newer.",
                    SqlAgentToolType.MySQL =>
                        "MySQL ON DUPLICATE KEY UPDATE can fire on any UNIQUE or PRIMARY KEY and has no explicit conflict target. Core inventories provider-native enforced unique keys, including richer partial/expression/prefix shapes. The compiler has a conditional single-row DO UPDATE path only when an explicit ServerVersion 8.0.19+ target profile and statement-level assurance prove the matched explicit conflict target is the sole enforced native conflict source; it uses a proposed-row alias rather than deprecated VALUES(column). Because this capability matrix has no per-statement assurance input, the default capability remains Rejected and fail-closed; DO NOTHING, multiple native conflict sources, richer unsupported enforced unique sources, and typed approval execution remain rejected.",
                    SqlAgentToolType.Firebird =>
                        "Firebird raw UPDATE OR INSERT ... MATCHING is canonicalized only with an explicit MATCHING column list. Firebird target lowering is available only when DmlConflictTargetAssurance proves that the canonical conflict target equals the complete resolved primary key and the conflict update mirrors every supplied INSERT column as the same proposed-row column. Because this capability matrix has no per-statement primary-key assurance input, the default Firebird capability remains Rejected and fail-closed; DO NOTHING, partial updates, general UNIQUE-key matching, and general MERGE remain rejected.",
                    _ =>
                        "This provider requires MERGE-style source and match semantics. Core has not yet modeled the source-row cardinality and match guarantees needed for a portable MERGE contract, so upsert remains fail-closed."
                });
    }

    private static bool IsAtLeast(
        Version? actual,
        Version required) =>
        actual is not null && actual.CompareTo(required) >= 0;

    private static bool IsAtLeast(
        SqlProviderCapabilityProfile? profile,
        SqlAgentToolType provider,
        Version required) =>
        profile is { Provider: var profileProvider, ServerVersion: { } actual }
        && profileProvider == provider
        && actual.CompareTo(required) >= 0;
}
