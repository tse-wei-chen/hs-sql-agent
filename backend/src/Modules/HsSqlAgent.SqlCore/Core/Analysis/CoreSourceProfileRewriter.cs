namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Resolves source-session-dependent syntax into an internal canonical marker before normalization,
/// then restores the canonical Core operator after source-specific normalization has completed.
/// Undeclared session semantics remain fail-closed in the ordinary normalizer.
/// </summary>
internal static class CoreSourceProfileRewriter
{
    private const string MySqlPipesConcatMarker = "__CORE_MYSQL_PIPES_AS_CONCAT__";

    public static void ValidateProfile(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile)
    {
        if (sourceProfile is null) return;
        if (sourceProfile.Provider != sourceDialect)
        {
            throw new SqlCompilationException(
                $"Source capability profile declares provider {sourceProfile.Provider}, " +
                $"but parsed SQL declares source dialect {sourceDialect}.");
        }
        if (sourceProfile.CompatibilityLevel is < 0)
            throw new SqlCompilationException("Provider compatibility level must be non-negative.");
    }

    public static bool SupportsMySqlPipesAsConcat(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile) =>
        SqlConcatCapabilityRules.SupportsMySqlPipesAsConcat(
            sourceDialect,
            sourceProfile);

    public static SqlStatement Prepare(
        SqlStatement statement,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ValidateProfile(sourceDialect, sourceProfile);

        if (!SupportsMySqlPipesAsConcat(sourceDialect, sourceProfile))
            return statement;

        return new BinaryOperatorAstRewriter(
            static op => op == "||" ? MySqlPipesConcatMarker : op)
            .Rewrite(statement);
    }

    public static SqlStatement Restore(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new BinaryOperatorAstRewriter(
            static op => op.Equals(MySqlPipesConcatMarker, StringComparison.OrdinalIgnoreCase)
                ? "||"
                : op)
            .Rewrite(statement);
    }

    private sealed class BinaryOperatorAstRewriter : CoreSqlAstRewriter
    {
        private readonly Func<string, string> _rewriteOperator;

        internal BinaryOperatorAstRewriter(Func<string, string> rewriteOperator)
            : base("source-profile")
        {
            _rewriteOperator = rewriteOperator
                ?? throw new ArgumentNullException(nameof(rewriteOperator));
        }

        protected override SqlExpr RewriteExpressionNode(SqlExpr expression) =>
            expression is BinaryExpr binary
                ? binary with { Operator = _rewriteOperator(binary.Operator) }
                : expression;
    }
}
