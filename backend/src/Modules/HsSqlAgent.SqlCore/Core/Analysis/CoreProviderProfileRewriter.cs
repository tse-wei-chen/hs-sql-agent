namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Applies target-runtime capability rewrites after canonical validation and before provider
/// lowering. Profile-dependent capabilities remain fail-closed unless the deployment explicitly
/// declares the required runtime contract.
/// </summary>
internal static class CoreProviderProfileRewriter
{
    public static SqlStatement Rewrite(
        SqlStatement statement,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ValidateProfile(targetProvider, targetProfile);

        if (!RequiresProviderProfilePass(targetProvider))
            return statement;

        return new ProviderProfileAstRewriter(targetProvider, targetProfile)
            .Rewrite(statement);
    }

    public static void ValidateProfile(
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        switch (SqlProviderCapabilityProfileRules.ValidationIssue(
                    targetProfile,
                    targetProvider))
        {
            case SqlProviderCapabilityProfileValidationIssue.None:
                return;
            case SqlProviderCapabilityProfileValidationIssue.ProviderMismatch:
                throw new SqlCompilationException(
                    $"Target capability profile declares provider {targetProfile!.Provider}, " +
                    $"but compilation targets {targetProvider}.");
            case SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel:
                throw new SqlCompilationException(
                    "Provider compatibility level must be non-negative.");
            default:
                throw new SqlCompilationException(
                    "Unsupported target capability profile validation issue.");
        }
    }

    private static bool RequiresProviderProfilePass(
        SqlAgentToolType provider) =>
        SqlFirebirdTimeZoneTypeCapabilityRules.RequiresTargetProfileValidation(provider)
        || SqlFirebirdDecimalCapabilityRules.RequiresTargetProfileValidation(provider)
        || SqlConcatCapabilityRules.RequiresTargetProfileRewrite(provider)
        || SqlRegexCapabilityRules.RequiresTargetProfileRewrite(provider);

    private sealed class ProviderProfileAstRewriter : CoreSqlAstRewriter
    {
        private readonly SqlAgentToolType _targetProvider;
        private readonly SqlProviderCapabilityProfile? _targetProfile;

        internal ProviderProfileAstRewriter(
            SqlAgentToolType targetProvider,
            SqlProviderCapabilityProfile? targetProfile)
            : base("provider-profile")
        {
            _targetProvider = targetProvider;
            _targetProfile = targetProfile;
        }

        protected override SqlExpr RewriteExpressionNode(SqlExpr expression) =>
            expression switch
            {
                LiteralExpr literal => RewriteLiteral(literal),
                CastExpr cast => RewriteCast(cast),
                BinaryExpr binary => RewriteBinary(binary),
                FunctionCallExpr function => RewriteFunction(function),
                _ => expression
            };

        private LiteralExpr RewriteLiteral(LiteralExpr literal)
        {
            if (literal.Value is SqlOffsetDateTimeValue or DateTimeOffset)
            {
                var error = SqlOffsetTimestampCapabilityRules.TargetValidationError(
                    _targetProvider,
                    _targetProfile);
                if (error is not null)
                    throw new SqlCompilationException(error);
            }

            if (literal.Value is decimal decimalValue)
            {
                var error = SqlFirebirdDecimalCapabilityRules.TargetValidationError(
                    _targetProvider,
                    _targetProfile,
                    decimalValue);
                if (error is not null)
                    throw new SqlCompilationException(error);
            }

            return literal;
        }

        private CastExpr RewriteCast(CastExpr cast)
        {
            var error = SqlFirebirdTimeZoneTypeCapabilityRules.CastTargetValidationError(
                _targetProvider,
                _targetProfile,
                cast.TypeName);
            if (error is not null)
                throw new SqlCompilationException(error);

            return cast;
        }

        private BinaryExpr RewriteBinary(BinaryExpr binary)
        {
            if (!binary.Operator.Equals("||", StringComparison.OrdinalIgnoreCase)
                || !SqlConcatCapabilityRules.RequiresTargetProfileRewrite(_targetProvider))
            {
                return binary;
            }

            return SqlConcatCapabilityRules.EvaluateSqlServerTarget(_targetProfile) switch
            {
                SqlServerConcatTargetMode.NativePipes => binary,
                SqlServerConcatTargetMode.PlusOperator => binary with { Operator = "+" },
                SqlServerConcatTargetMode.Rejected => throw new SqlCompilationException(
                    SqlConcatCapabilityRules.SqlServerTargetValidationError(_targetProfile)),
                _ => throw new SqlCompilationException(
                    "Unsupported SQL Server concat target mode.")
            };
        }

        private FunctionCallExpr RewriteFunction(FunctionCallExpr function)
        {
            var name = IdentifierText(function.Name);
            var rewriteKind =
                SqlCanonicalFunctionRegistry.Find(name)?.ProviderProfileRewriteKind
                ?? SqlCanonicalProviderProfileRewriteKind.None;

            return rewriteKind switch
            {
                SqlCanonicalProviderProfileRewriteKind.None => function,
                SqlCanonicalProviderProfileRewriteKind.Regex =>
                    RewriteRegexFunction(function),
                _ => throw new SqlCompilationException(
                    $"Unsupported canonical provider-profile rewrite kind '{rewriteKind}' for function '{name}'.")
            };
        }

        private FunctionCallExpr RewriteRegexFunction(FunctionCallExpr function)
        {
            var capabilityError = SqlRegexCapabilityRules.TargetValidationError(
                _targetProvider,
                _targetProfile);
            if (capabilityError is not null)
                throw new SqlCompilationException(capabilityError);

            return function with
            {
                Name = SqlIdentifier.Unquoted("REGEXP_LIKE", function.Name.Span)
            };
        }

        private static string IdentifierText(SqlIdentifier identifier) =>
            string.Join('.', identifier.Parts.Select(part => part.Value));
    }
}
