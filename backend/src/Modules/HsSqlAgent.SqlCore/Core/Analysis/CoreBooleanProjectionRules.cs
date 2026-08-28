namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Classifies expression shapes that are definitely boolean-valued without attempting full SQL
/// type inference. This is intentionally conservative: only shapes whose result type is explicit
/// in the Core AST are classified, so scalar numeric/string CASE expressions are not rejected.
/// Provider-unsupported functions are deliberately left to the function capability validator so
/// the more specific unsupported-function diagnostic wins over the projection/assignment diagnostic.
/// </summary>
internal static class CoreBooleanProjectionRules
{
    private static readonly HashSet<string> BooleanBinaryOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "=", "<>", "!=", ">", "<", ">=", "<=", "LIKE", "ILIKE", "AND", "OR", "IN", "NOT IN"
    };

    public static void Validate(SqlExpr expression, SqlAgentToolType provider) =>
        ValidateScalarBooleanCapability(
            expression,
            provider,
            "expression.boolean_select");

    public static void ValidateAssignment(SqlExpr expression, SqlAgentToolType provider) =>
        ValidateScalarBooleanCapability(
            expression,
            provider,
            "dml.update.boolean_assignment");

    public static void ValidateInsertValue(SqlExpr expression, SqlAgentToolType provider) =>
        ValidateScalarBooleanCapability(
            expression,
            provider,
            "dml.insert.boolean_value");

    private static void ValidateScalarBooleanCapability(
        SqlExpr expression,
        SqlAgentToolType provider,
        string capability)
    {
        if (!IsDefinitelyBoolean(expression, provider))
            return;

        var error = SqlScalarBooleanCapabilityRules.TargetValidationError(
            provider,
            capability);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    internal static bool IsDefinitelyBoolean(SqlExpr expression, SqlAgentToolType provider) => expression switch
    {
        LiteralExpr { Value: bool } => true,
        IsNullExpr or InExpr or BetweenExpr or ExistsExpr => true,
        UnaryExpr unary when unary.Operator.Equals("NOT", StringComparison.OrdinalIgnoreCase) => true,
        BinaryExpr binary when BooleanBinaryOperators.Contains(binary.Operator) => true,
        FunctionCallExpr function => IsCanonicalBooleanResult(function, provider),
        CaseExpr @case => IsBooleanCase(@case, provider),
        _ => false
    };

    private static bool IsCanonicalBooleanResult(
        FunctionCallExpr function,
        SqlAgentToolType provider)
    {
        var name = string.Join('.', function.Name.Parts.Select(part => part.Value));
        return SqlCanonicalFunctionRegistry.Find(name)?.ResultKind switch
        {
            SqlCanonicalResultKind.RegexPredicate =>
                SqlRegexCapabilityRules.SupportsTarget(
                    provider,
                    targetProfile: null),
            _ => false
        };
    }

    internal static bool HasOnlyLiteralBooleanCaseResults(CaseExpr @case)
    {
        ArgumentNullException.ThrowIfNull(@case);

        foreach (var branch in @case.Branches)
        {
            if (!IsBooleanOrNullLiteral(branch.Value))
                return false;
        }

        return @case.ElseExpression is null
            || IsBooleanOrNullLiteral(@case.ElseExpression);
    }

    private static bool IsBooleanCase(CaseExpr @case, SqlAgentToolType provider)
    {
        var sawBooleanResult = false;
        foreach (var branch in @case.Branches)
        {
            if (IsDefinitelyBoolean(branch.Value, provider))
            {
                sawBooleanResult = true;
                continue;
            }

            if (!IsNullLiteral(branch.Value))
                return false;
        }

        if (@case.ElseExpression is not null)
        {
            if (IsDefinitelyBoolean(@case.ElseExpression, provider))
                sawBooleanResult = true;
            else if (!IsNullLiteral(@case.ElseExpression))
                return false;
        }

        return sawBooleanResult;
    }

    private static bool IsBooleanOrNullLiteral(SqlExpr expression) =>
        expression is LiteralExpr { Value: bool or null };

    private static bool IsNullLiteral(SqlExpr expression) =>
        expression is LiteralExpr { Value: null };

}
