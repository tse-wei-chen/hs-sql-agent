namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Applies source/target runtime JOIN gates before normalization/lowering. Structural JOIN discovery
/// is delegated to CoreSqlAstTraversal so nested CTE/derived/scalar/EXISTS/INSERT-query shapes share
/// the same fail-closed traversal.
/// </summary>
internal static class CoreJoinProfileValidator
{
    internal static void Validate(
        SqlStatement statement,
        bool enforceSourceDialectSyntax,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(statement);

        foreach (var join in CoreSqlAstTraversal.EnumerateJoins(statement))
        {
            if (enforceSourceDialectSyntax)
            {
                var sourceError = SqlJoinCapabilityRules.SourceValidationError(
                    join.Kind,
                    sourceDialect,
                    sourceProfile);
                if (sourceError is not null)
                    throw new SqlCompilationException(sourceError);
            }

            var targetError = SqlJoinCapabilityRules.TargetValidationError(
                join.Kind,
                targetProvider,
                targetProfile);
            if (targetError is not null)
                throw new SqlCompilationException(targetError);
        }
    }
}
