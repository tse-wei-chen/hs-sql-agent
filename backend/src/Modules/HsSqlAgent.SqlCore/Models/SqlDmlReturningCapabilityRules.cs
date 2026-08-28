namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single source/target provider-runtime contract for portable DML RETURNING result rows.
/// PostgreSQL is always declared; SQLite requires ServerVersion 3.35+; Firebird requires
/// ServerVersion 5.0+ for the modeled portable multi-row DSQL contract. SQL Server, Oracle,
/// and MySQL remain fail-closed for distinct provider-specific reasons.
/// </summary>
internal static class SqlDmlReturningCapabilityRules
{
    private static readonly Version SqliteReturningVersion = new(3, 35);
    private static readonly Version FirebirdMultiRowReturningVersion = new(5, 0);

    internal static bool SupportsSource(
        SqlAgentToolType sourceDialect,
        Version? sourceServerVersion) => sourceDialect switch
    {
        SqlAgentToolType.Postgres => true,
        SqlAgentToolType.Sqlite =>
            IsAtLeast(sourceServerVersion, SqliteReturningVersion),
        SqlAgentToolType.Firebird =>
            IsAtLeast(sourceServerVersion, FirebirdMultiRowReturningVersion),
        SqlAgentToolType.MySQL
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle => false,
        _ => throw new ArgumentOutOfRangeException(
            nameof(sourceDialect),
            sourceDialect,
            "Unsupported SQL source dialect.")
    };

    internal static string? SourceValidationError(
        SqlAgentToolType sourceDialect,
        Version? sourceServerVersion)
    {
        if (SupportsSource(sourceDialect, sourceServerVersion))
            return null;

        return sourceDialect switch
        {
            SqlAgentToolType.Sqlite =>
                "Raw SQLite RETURNING requires a source capability profile with ServerVersion 3.35 or newer.",
            SqlAgentToolType.Firebird =>
                "Portable multi-row Firebird DSQL RETURNING requires a source capability profile with ServerVersion 5.0 or newer.",
            SqlAgentToolType.MsSqlServer =>
                "SQL Server uses OUTPUT rather than RETURNING; trigger-sensitive OUTPUT result semantics are not yet represented by the portable Core DML contract.",
            SqlAgentToolType.Oracle =>
                "Oracle RETURNING requires RETURNING INTO host or bind variables, which are not represented by the portable Core DML result-row contract.",
            SqlAgentToolType.MySQL =>
                "MySQL has no declared DML RETURNING result-row syntax in the Core MySQL 8.4 source profile.",
            _ =>
                $"DML RETURNING is not represented for source dialect {sourceDialect}."
        };
    }

    internal static bool SupportsTarget(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile) => provider switch
    {
        SqlAgentToolType.Postgres => true,
        SqlAgentToolType.Sqlite =>
            IsAtLeast(targetProfile, SqlAgentToolType.Sqlite, SqliteReturningVersion),
        SqlAgentToolType.Firebird =>
            IsAtLeast(targetProfile, SqlAgentToolType.Firebird, FirebirdMultiRowReturningVersion),
        SqlAgentToolType.MySQL
            or SqlAgentToolType.MsSqlServer
            or SqlAgentToolType.Oracle => false,
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

        return provider switch
        {
            SqlAgentToolType.Sqlite =>
                "SQLite DML RETURNING requires an explicit target capability profile with ServerVersion 3.35 or newer.",
            SqlAgentToolType.Firebird =>
                "Portable multi-row Firebird DSQL RETURNING requires an explicit target capability profile with ServerVersion 5.0 or newer.",
            SqlAgentToolType.MsSqlServer =>
                "SQL Server OUTPUT without INTO is trigger-sensitive and Core has no target-table trigger capability metadata; DML result rows remain fail-closed for SQL Server.",
            SqlAgentToolType.Oracle =>
                "Oracle DML RETURNING requires RETURNING INTO host or bind variables, which are not represented by the Core result-row execution contract.",
            SqlAgentToolType.MySQL =>
                "MySQL has no declared DML RETURNING result-row equivalent in the Core MySQL 8.4 target profile.",
            _ =>
                $"DML result rows are not represented for target provider {provider}."
        };
    }

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        var supported = SupportsTarget(provider, targetProfile);
        return new(
            "dml.returning_output",
            "dml",
            supported
                ? SqlCapabilityStatus.Translated
                : SqlCapabilityStatus.Rejected,
            supported
                ? provider switch
                {
                    SqlAgentToolType.Postgres =>
                        "INSERT/UPDATE/DELETE may return unqualified target columns or a lone wildcard through native RETURNING. Result-producing mutations are marked structurally, materialized through the DML execution boundary, and the returned-row count must still match the approved affected-row count before commit.",
                    SqlAgentToolType.Sqlite =>
                        "SQLite ServerVersion 3.35+ target profiles may return unqualified target columns or a lone wildcard through native RETURNING. The explicit target version is required; returned-row count remains part of approval revalidation before commit.",
                    SqlAgentToolType.Firebird =>
                        "Firebird ServerVersion 5.0+ target profiles may use the portable multi-row DSQL RETURNING contract for unqualified target columns or a lone wildcard. The explicit target version is required; returned-row count remains part of approval revalidation before commit.",
                    _ => throw new InvalidOperationException(
                        $"Unexpected supported DML RETURNING provider {provider}.")
                }
                : provider switch
                {
                    SqlAgentToolType.Sqlite =>
                        "SQLite DML RETURNING remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.35 or newer.",
                    SqlAgentToolType.Firebird =>
                        "Portable multi-row Firebird DSQL RETURNING remains fail-closed unless the target capability profile explicitly declares ServerVersion 5.0 or newer.",
                    SqlAgentToolType.MsSqlServer =>
                        "SQL Server OUTPUT without INTO is trigger-sensitive. Core does not yet carry target-table trigger capability metadata, so result rows remain fail-closed instead of assuming OUTPUT can be returned directly to the client.",
                    SqlAgentToolType.Oracle =>
                        "Oracle DML RETURNING requires RETURNING INTO host or bind variables, which are outside the Core result-row execution contract.",
                    SqlAgentToolType.MySQL =>
                        "MySQL has no declared INSERT/UPDATE/DELETE RETURNING result-row equivalent in the Core MySQL 8.4 target profile.",
                    _ =>
                        $"DML result rows are not represented for target provider {provider}."
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
