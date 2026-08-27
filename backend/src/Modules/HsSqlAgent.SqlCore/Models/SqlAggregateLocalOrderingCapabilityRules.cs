namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Single source/target contract for aggregate-local ordering. The AST can represent ordering for
/// any function, but Core enables a shape only after both raw source grammar and target lowering
/// have a proven semantic contract. Runtime-version and compatibility-level gates live here so
/// compiler behavior and the public capability matrix cannot drift.
/// </summary>
internal static class SqlAggregateLocalOrderingCapabilityRules
{
    internal static readonly Version SqliteMinimumVersion = new(3, 44);
    internal static readonly Version SqlServerMinimumVersion = new(14, 0);
    internal static readonly Version OracleMinimumVersion = new(11, 2);
    internal const int SqlServerMinimumCompatibilityLevel = 110;

    internal static string? ValidationError(
        bool enforceSourceDialectSyntax,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile,
        string functionName,
        AggregateOrderSyntaxKind sourceSyntax,
        bool isDistinct)
    {
        var targetError = TargetValidationError(targetProvider, targetProfile);
        if (targetError is not null) return targetError;

        if (enforceSourceDialectSyntax)
        {
            var sourceError = RawSourceValidationError(
                sourceDialect,
                sourceProfile,
                functionName,
                sourceSyntax);
            if (sourceError is not null) return sourceError;
        }
        else if (!IsStructuredStringAggregate(functionName))
        {
            return "Structured aggregate-local ordering is enabled only for the canonical string-aggregate family.";
        }

        if (isDistinct)
        {
            return "String aggregation DISTINCT with aggregate-local ORDER BY remains fail-closed until " +
                   "provider-specific DISTINCT argument/order-expression restrictions are modeled explicitly.";
        }

        return null;
    }

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        if (provider == SqlAgentToolType.Postgres)
        {
            return new(
                "aggregate.string.ordering",
                "aggregate",
                SqlCapabilityStatus.Supported,
                "PostgreSQL target lowering supports structured aggregate-local ORDER BY for STRING_AGG. " +
                "Raw SQL enablement accepts PostgreSQL inline STRING_AGG ordering without DISTINCT.");
        }

        if (provider == SqlAgentToolType.Sqlite)
        {
            var version = targetProfile?.ServerVersion;
            var supported = version is not null && version.CompareTo(SqliteMinimumVersion) >= 0;
            return new(
                "aggregate.string.ordering",
                "aggregate",
                supported ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                supported
                    ? $"SQLite {version} satisfies the 3.44+ aggregate ORDER BY runtime contract; Core lowers ordered string aggregation to GROUP_CONCAT(... ORDER BY ...)."
                    : "SQLite aggregate-local ORDER BY remains fail-closed unless the target capability profile explicitly declares ServerVersion 3.44 or newer.");
        }

        if (provider == SqlAgentToolType.MsSqlServer)
        {
            var version = targetProfile?.ServerVersion;
            var compatibilityLevel = targetProfile?.CompatibilityLevel;
            var supported = version is not null
                && version.CompareTo(SqlServerMinimumVersion) >= 0
                && compatibilityLevel is >= SqlServerMinimumCompatibilityLevel;
            return new(
                "aggregate.string.ordering",
                "aggregate",
                supported ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                supported
                    ? $"SQL Server {version} target compatibility level {compatibilityLevel} satisfies the 14.0+/110+ WITHIN GROUP ordering contract for STRING_AGG."
                    : "SQL Server ordered STRING_AGG remains fail-closed unless the target capability profile explicitly declares ServerVersion 14.0+ and CompatibilityLevel 110+.");
        }

        if (provider == SqlAgentToolType.Oracle)
        {
            var version = targetProfile?.ServerVersion;
            var supported = version is not null && version.CompareTo(OracleMinimumVersion) >= 0;
            return new(
                "aggregate.string.ordering",
                "aggregate",
                supported ? SqlCapabilityStatus.Supported : SqlCapabilityStatus.Rejected,
                supported
                    ? $"Oracle {version} satisfies the 11.2+ LISTAGG WITHIN GROUP ordering contract."
                    : "Oracle ordered LISTAGG remains fail-closed unless the target capability profile explicitly declares ServerVersion 11.2 or newer.");
        }

        if (provider == SqlAgentToolType.MySQL)
        {
            return new(
                "aggregate.string.ordering",
                "aggregate",
                SqlCapabilityStatus.Supported,
                "MySQL GROUP_CONCAT supports inline aggregate ORDER BY and an optional SEPARATOR string literal. " +
                "Core keeps native comma-separated multi-expression GROUP_CONCAT fail-closed rather than reinterpreting expressions as a delimiter.");
        }

        return new(
            "aggregate.string.ordering",
            "aggregate",
            SqlCapabilityStatus.Rejected,
            $"Aggregate-local string ordering has no enabled Core target contract for {provider}; " +
            "provider-specific grammar/runtime semantics remain fail-closed.");
    }

    private static string? TargetValidationError(
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile) => targetProvider switch
    {
        SqlAgentToolType.Postgres => null,
        SqlAgentToolType.Sqlite => VersionValidationError(
            targetProfile?.ServerVersion,
            SqliteMinimumVersion,
            "SQLite",
            "target"),
        SqlAgentToolType.MsSqlServer =>
            SqlServerValidationError(targetProfile, "target"),
        SqlAgentToolType.Oracle => VersionValidationError(
            targetProfile?.ServerVersion,
            OracleMinimumVersion,
            "Oracle",
            "target"),
        SqlAgentToolType.MySQL => null,
        _ => $"SQL capability 'aggregate.string.ordering' is not enabled for target provider {targetProvider}; " +
             "aggregate-local ORDER BY remains fail-closed."
    };

    private static string? RawSourceValidationError(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile,
        string functionName,
        AggregateOrderSyntaxKind sourceSyntax)
    {
        if (sourceDialect == SqlAgentToolType.Postgres
            && functionName.Equals("STRING_AGG", StringComparison.OrdinalIgnoreCase))
        {
            return sourceSyntax == AggregateOrderSyntaxKind.Inline
                ? null
                : "PostgreSQL raw STRING_AGG aggregate ordering must use inline ORDER BY inside the function call.";
        }

        if (sourceDialect == SqlAgentToolType.Sqlite
            && functionName.Equals("GROUP_CONCAT", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceSyntax != AggregateOrderSyntaxKind.Inline)
                return "SQLite raw GROUP_CONCAT aggregate ordering must use inline ORDER BY inside the function call.";

            return VersionValidationError(
                sourceProfile?.ServerVersion,
                SqliteMinimumVersion,
                "SQLite",
                "source");
        }

        if (sourceDialect == SqlAgentToolType.MsSqlServer
            && functionName.Equals("STRING_AGG", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceSyntax != AggregateOrderSyntaxKind.WithinGroup)
                return "SQL Server raw STRING_AGG aggregate ordering must use WITHIN GROUP (ORDER BY ...).";

            return SqlServerValidationError(sourceProfile, "source");
        }

        if (sourceDialect == SqlAgentToolType.Oracle
            && functionName.Equals("LISTAGG", StringComparison.OrdinalIgnoreCase))
        {
            if (sourceSyntax != AggregateOrderSyntaxKind.WithinGroup)
                return "Oracle raw LISTAGG aggregate ordering must use WITHIN GROUP (ORDER BY ...).";

            return VersionValidationError(
                sourceProfile?.ServerVersion,
                OracleMinimumVersion,
                "Oracle",
                "source");
        }

        if (sourceDialect == SqlAgentToolType.MySQL
            && functionName.Equals("GROUP_CONCAT", StringComparison.OrdinalIgnoreCase))
        {
            return sourceSyntax == AggregateOrderSyntaxKind.Inline
                ? null
                : "MySQL raw GROUP_CONCAT aggregate ordering must use inline ORDER BY inside the function call.";
        }

        return "SQL capability 'aggregate.string.ordering' currently accepts PostgreSQL inline " +
               "STRING_AGG(... ORDER BY ...), SQLite 3.44+ inline GROUP_CONCAT(... ORDER BY ...), " +
               "SQL Server 14.0+/compatibility-level-110+ STRING_AGG(...) WITHIN GROUP (ORDER BY ...), " +
               "Oracle 11.2+ LISTAGG(...) WITHIN GROUP (ORDER BY ...), or MySQL GROUP_CONCAT(... ORDER BY ...).";
    }

    private static string? SqlServerValidationError(
        SqlProviderCapabilityProfile? profile,
        string side)
    {
        var versionError = VersionValidationError(
            profile?.ServerVersion,
            SqlServerMinimumVersion,
            "SQL Server",
            side);
        if (versionError is not null) return versionError;

        return CompatibilityValidationError(
            profile?.CompatibilityLevel,
            SqlServerMinimumCompatibilityLevel,
            side);
    }

    private static string? VersionValidationError(
        Version? declaredVersion,
        Version minimumVersion,
        string providerName,
        string side)
    {
        if (declaredVersion is null)
        {
            return $"SQL capability 'aggregate.string.ordering' requires a declared {providerName} {side} capability " +
                   $"profile with ServerVersion {minimumVersion}+.";
        }

        return declaredVersion.CompareTo(minimumVersion) < 0
            ? $"SQL capability 'aggregate.string.ordering' requires {providerName} {side} ServerVersion " +
              $"{minimumVersion}+; declared version is {declaredVersion}."
            : null;
    }

    private static string? CompatibilityValidationError(
        int? declaredLevel,
        int minimumLevel,
        string side)
    {
        if (declaredLevel is null)
        {
            return $"SQL capability 'aggregate.string.ordering' requires a declared SQL Server {side} capability " +
                   $"profile with CompatibilityLevel {minimumLevel}+.";
        }

        return declaredLevel < minimumLevel
            ? $"SQL capability 'aggregate.string.ordering' requires SQL Server {side} CompatibilityLevel " +
              $"{minimumLevel}+; declared level is {declaredLevel}."
            : null;
    }

    private static bool IsStructuredStringAggregate(string functionName) =>
        functionName.Equals("STRING_AGG", StringComparison.OrdinalIgnoreCase)
        || functionName.Equals("CORE_STRING_AGG", StringComparison.OrdinalIgnoreCase);
}
