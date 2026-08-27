namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Capability gate for aggregate-local ORDER BY. The AST structure is generic, but raw source
/// syntax and target lowering are enabled only for explicitly declared provider/function shapes.
/// Shared traversal keeps nested query/DML coverage fail-closed without parallel graph walkers.
/// </summary>
internal static class CoreAggregateLocalOrderingGuard
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

        foreach (var expression in CoreSqlAstTraversal.EnumerateExpressions(statement))
        {
            if (expression is not FunctionCallExpr function || function.AggregateOrderBy.IsDefaultOrEmpty)
                continue;

            var functionName = IdentifierText(function.Name).ToUpperInvariant();
            var error = SqlAggregateLocalOrderingCapabilityRules.ValidationError(
                enforceSourceDialectSyntax,
                sourceDialect,
                sourceProfile,
                targetProvider,
                targetProfile,
                functionName,
                function.AggregateOrderSyntax,
                function.IsDistinct);
            if (error is not null)
                throw new SqlCompilationException(error);
        }
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
