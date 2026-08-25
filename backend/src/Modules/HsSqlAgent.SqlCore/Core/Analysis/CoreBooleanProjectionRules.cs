using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Analysis;

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

    public static void Validate(SqlExpr expression, SqlAgentToolType provider)
    {
        if (provider is not (SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer)
            || !IsDefinitelyBoolean(expression, provider))
        {
            return;
        }

        throw new SqlCompilationException(
            $"SQL capability 'expression.boolean_select' is not supported by provider {provider} for this Core plan.");
    }

    public static void ValidateAssignment(SqlExpr expression, SqlAgentToolType provider)
    {
        if (provider is not (SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer)
            || !IsDefinitelyBoolean(expression, provider))
        {
            return;
        }

        throw new SqlCompilationException(
            $"SQL capability 'dml.update.boolean_assignment' is not supported by provider {provider} for this Core plan.");
    }

    public static void ValidateInsertValue(SqlExpr expression, SqlAgentToolType provider)
    {
        if (provider is not (SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer)
            || !IsDefinitelyBoolean(expression, provider))
        {
            return;
        }

        throw new SqlCompilationException(
            $"SQL capability 'dml.insert.boolean_value' is not supported by provider {provider} for this Core plan.");
    }

    private static bool IsDefinitelyBoolean(SqlExpr expression, SqlAgentToolType provider) => expression switch
    {
        LiteralExpr { Value: bool } => true,
        IsNullExpr or InExpr or BetweenExpr or ExistsExpr => true,
        UnaryExpr unary when unary.Operator.Equals("NOT", StringComparison.OrdinalIgnoreCase) => true,
        BinaryExpr binary when BooleanBinaryOperators.Contains(binary.Operator) => true,
        FunctionCallExpr function when IdentifierText(function.Name).Equals(
            "CORE_REGEX_MATCH",
            StringComparison.OrdinalIgnoreCase) =>
            provider is SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Oracle,
        CaseExpr @case => IsBooleanCase(@case, provider),
        _ => false
    };

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

    private static bool IsNullLiteral(SqlExpr expression) =>
        expression is LiteralExpr { Value: null };

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
