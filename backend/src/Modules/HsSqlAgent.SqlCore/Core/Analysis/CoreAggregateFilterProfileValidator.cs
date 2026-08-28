namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Enforces runtime-version and predicate-shape boundaries for aggregate FILTER after binding has
/// exposed the complete query graph. Source and target profiles remain independent: a source
/// profile never authorizes a target capability, even when both sides name the same provider.
/// Structural predicate features are discovered here while bound outer-reference provenance is
/// available; provider-specific restrictions remain owned by SqlAggregateFilterCapabilityRules.
/// </summary>
internal static class CoreAggregateFilterProfileValidator
{
    public static void Validate(
        SqlStatement statement,
        bool enforceSourceDialectSyntax,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (!CoreSqlAstTraversal.EnumerateExpressions(statement).Any(static expression => expression is FilterExpr))
            return;

        if (enforceSourceDialectSyntax)
        {
            ValidateRuntime("source", sourceDialect, sourceProfile);
            ValidateFilterPredicates(statement, sourceDialect, "source");
        }

        ValidateRuntime("target", targetProvider, targetProfile);
        ValidateFilterPredicates(statement, targetProvider, "target");
    }

    private static void ValidateRuntime(
        string side,
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile)
    {
        var error = SqlAggregateFilterCapabilityRules.ValidationError(provider, profile, side);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateFilterPredicates(
        SqlStatement statement,
        SqlAgentToolType provider,
        string side)
    {
        foreach (var expression in CoreSqlAstTraversal.EnumerateExpressions(statement))
        {
            if (expression is FilterExpr filter)
                ValidatePredicate(filter.Predicate, provider, side);
        }
    }

    private static void ValidatePredicate(
        SqlExpr expression,
        SqlAgentToolType provider,
        string side)
    {
        foreach (var node in CoreSqlAstTraversal.EnumerateExpressions(expression))
        {
            var feature = node switch
            {
                BoundColumnExpr { IsOuterReference: true } =>
                    SqlAggregateFilterPredicateFeature.OuterReference,
                SubqueryExpr or ExistsExpr =>
                    SqlAggregateFilterPredicateFeature.Subquery,
                WindowedExpr =>
                    SqlAggregateFilterPredicateFeature.WindowFunction,
                _ => (SqlAggregateFilterPredicateFeature?)null
            };

            if (feature is null)
                continue;

            var error = SqlAggregateFilterCapabilityRules.PredicateValidationError(
                provider,
                side,
                feature.Value);
            if (error is not null)
                throw new SqlCompilationException(error);
        }
    }
}
