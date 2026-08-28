namespace HsSqlAgent.SqlCore.Models;

internal enum SqlAggregateFilterCapabilityReason
{
    Supported,
    MissingVersion,
    VersionTooLow,
    UnsupportedProvider
}

internal enum SqlAggregateFilterPredicateFeature
{
    OuterReference,
    Subquery,
    WindowFunction
}

internal sealed record SqlAggregateFilterCapabilityDecision(
    SqlAggregateFilterCapabilityReason Reason,
    Version? MinimumVersion = null,
    Version? DeclaredVersion = null)
{
    public bool Supported => Reason == SqlAggregateFilterCapabilityReason.Supported;
}

/// <summary>
/// Single provider/version and predicate-shape contract for aggregate FILTER. Compiler validation
/// and the public capability matrix both consume this rule so runtime behavior cannot drift from
/// advertised capabilities. Structural predicate traversal remains in analysis after binding,
/// where outer-reference provenance is available.
/// </summary>
internal static class SqlAggregateFilterCapabilityRules
{
    internal static readonly Version PostgresMinimumVersion = new(9, 4);
    internal static readonly Version SqliteMinimumVersion = new(3, 30);
    internal static readonly Version FirebirdMinimumVersion = new(4, 0);
    internal static readonly Version OracleMinimumVersion = new(26, 0);

    internal static bool CanEverSupportProvider(SqlAgentToolType provider) =>
        provider is SqlAgentToolType.Postgres
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.Firebird
            or SqlAgentToolType.Oracle;

    internal static string? RawSourceSyntaxError(SqlAgentToolType sourceDialect) =>
        CanEverSupportProvider(sourceDialect)
            ? null
            : $"Aggregate FILTER (WHERE ...) is not valid for declared source dialect {sourceDialect} in the Core source capability profile.";

    internal static SqlAggregateFilterCapabilityDecision Evaluate(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile)
    {
        if (!CanEverSupportProvider(provider))
            return new(SqlAggregateFilterCapabilityReason.UnsupportedProvider);

        return provider switch
        {
        SqlAgentToolType.Postgres => EvaluateDeclaredVersion(profile, PostgresMinimumVersion),
        SqlAgentToolType.Sqlite => EvaluateDeclaredVersion(profile, SqliteMinimumVersion),
        SqlAgentToolType.Firebird => EvaluateDeclaredVersion(profile, FirebirdMinimumVersion),
        SqlAgentToolType.Oracle => EvaluateDeclaredVersion(profile, OracleMinimumVersion),

        _ => throw new ArgumentOutOfRangeException(
            nameof(provider),
            provider,
            "Unsupported SQL provider.")
        };
    }

    internal static string? ValidationError(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile,
        string side)
    {
        var decision = Evaluate(provider, profile);
        if (decision.Supported) return null;

        return decision.Reason switch
        {
            SqlAggregateFilterCapabilityReason.MissingVersion =>
                $"SQL capability 'expression.filter' requires a declared {provider} {side} capability " +
                $"profile with ServerVersion {decision.MinimumVersion}+.",

            SqlAggregateFilterCapabilityReason.VersionTooLow =>
                $"SQL capability 'expression.filter' requires {provider} {side} ServerVersion " +
                $"{decision.MinimumVersion}+; declared version is {decision.DeclaredVersion}.",

            SqlAggregateFilterCapabilityReason.UnsupportedProvider =>
                $"SQL capability 'expression.filter' is not supported by provider {provider} for {side} SQL.",

            _ => throw new InvalidOperationException(
                $"Unsupported aggregate FILTER capability decision '{decision.Reason}'.")
        };
    }

    internal static string? PredicateValidationError(
        SqlAgentToolType provider,
        string side,
        SqlAggregateFilterPredicateFeature feature)
    {
        if (provider != SqlAgentToolType.Oracle)
            return null;

        var restriction = feature switch
        {
            SqlAggregateFilterPredicateFeature.OuterReference => "outer references",
            SqlAggregateFilterPredicateFeature.Subquery => "subqueries",
            SqlAggregateFilterPredicateFeature.WindowFunction => "window functions",
            _ => throw new ArgumentOutOfRangeException(
                nameof(feature),
                feature,
                "Unsupported aggregate FILTER predicate feature.")
        };

        return
            $"SQL capability 'expression.filter' requires an Oracle 26ai {side} FILTER condition " +
            $"without {restriction}.";
    }

    internal static SqlCapability MatrixCapability(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        var decision = Evaluate(provider, targetProfile);
        var status = decision.Supported
            ? SqlCapabilityStatus.Supported
            : SqlCapabilityStatus.Rejected;

        var detail = decision.Reason switch
        {
            SqlAggregateFilterCapabilityReason.Supported when provider == SqlAgentToolType.Postgres =>
                $"Native aggregate FILTER is enabled by the declared PostgreSQL target ServerVersion " +
                $"{targetProfile!.ServerVersion}, satisfying the {PostgresMinimumVersion}+ runtime contract.",

            SqlAggregateFilterCapabilityReason.Supported when provider == SqlAgentToolType.Oracle =>
                "Oracle AI Database 26ai+ target profiles support native aggregate FILTER. Core additionally " +
                "requires each FILTER condition to contain no subqueries, window functions, or outer references " +
                "before Oracle lowering is authorized.",

            SqlAggregateFilterCapabilityReason.Supported =>
                $"Native aggregate FILTER is enabled by the declared {provider} target ServerVersion " +
                $"{targetProfile!.ServerVersion}, satisfying the {decision.MinimumVersion}+ runtime contract.",

            SqlAggregateFilterCapabilityReason.MissingVersion =>
                $"Aggregate FILTER remains fail-closed unless the {provider} target capability profile " +
                $"explicitly declares ServerVersion {decision.MinimumVersion} or newer.",

            SqlAggregateFilterCapabilityReason.VersionTooLow =>
                $"Aggregate FILTER requires {provider} target ServerVersion {decision.MinimumVersion}+; " +
                $"the declared target version {decision.DeclaredVersion} is too old.",

            SqlAggregateFilterCapabilityReason.UnsupportedProvider =>
                $"Aggregate FILTER has no declared portable target contract for {provider}.",

            _ => throw new InvalidOperationException(
                $"Unsupported aggregate FILTER capability decision '{decision.Reason}'.")
        };

        return new SqlCapability("expression.filter", "expression", status, detail);
    }

    private static SqlAggregateFilterCapabilityDecision EvaluateDeclaredVersion(
        SqlProviderCapabilityProfile? profile,
        Version minimumVersion)
    {
        if (profile?.ServerVersion is not { } declaredVersion)
        {
            return new(
                SqlAggregateFilterCapabilityReason.MissingVersion,
                minimumVersion);
        }

        return declaredVersion.CompareTo(minimumVersion) < 0
            ? new(
                SqlAggregateFilterCapabilityReason.VersionTooLow,
                minimumVersion,
                declaredVersion)
            : new(
                SqlAggregateFilterCapabilityReason.Supported,
                minimumVersion,
                declaredVersion);
    }
}
