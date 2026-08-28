namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Enforces runtime-version boundaries for aggregate FILTER after binding has exposed the complete
/// query graph. Source and target profiles remain independent: a source profile never authorizes a
/// target capability, even when both sides name the same provider. Oracle 26ai additionally limits
/// FILTER conditions, so those predicates are checked while bound outer-reference provenance is
/// still available.
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
            if (SqlAggregateFilterCapabilityRules.RequiresRestrictedPredicateShape(sourceDialect))
                ValidateOracleFilterPredicates(statement, "source");
        }

        ValidateRuntime("target", targetProvider, targetProfile);
        if (SqlAggregateFilterCapabilityRules.RequiresRestrictedPredicateShape(targetProvider))
            ValidateOracleFilterPredicates(statement, "target");
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

    private static void ValidateOracleFilterPredicates(SqlStatement statement, string side)
    {
        foreach (var expression in CoreSqlAstTraversal.EnumerateExpressions(statement))
        {
            if (expression is FilterExpr filter)
                ValidateOraclePredicate(filter.Predicate, side);
        }
    }

    private static void ValidateOraclePredicate(SqlExpr expression, string side)
    {
        foreach (var node in CoreSqlAstTraversal.EnumerateExpressions(expression))
        {
            switch (node)
            {
                case BoundColumnExpr { IsOuterReference: true }:
                    throw OraclePredicateError(side, "outer references");

                case SubqueryExpr:
                case ExistsExpr:
                    throw OraclePredicateError(side, "subqueries");

                case WindowedExpr:
                    throw OraclePredicateError(side, "window functions");
            }
        }
    }

    private static SqlCompilationException OraclePredicateError(string side, string restriction) =>
        new(
            $"SQL capability 'expression.filter' requires an Oracle 26ai {side} FILTER condition " +
            $"without {restriction}.");
}
