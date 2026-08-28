namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Provider contract for richer DML result expressions. This is intentionally narrower than the
/// portable column/wildcard RETURNING contract: the proven slice is PostgreSQL only, and the
/// expression itself must stay inside the deterministic target-row subset validated below.
/// </summary>
internal static class SqlDmlReturningExpressionCapabilityRules
{
    internal const string CapabilityId = "dml.returning.expression";

    internal static bool SupportsSource(SqlAgentToolType sourceDialect) =>
        sourceDialect == SqlAgentToolType.Postgres;

    internal static bool SupportsTarget(SqlAgentToolType provider) =>
        provider == SqlAgentToolType.Postgres;

    internal static string? SourceValidationError(SqlAgentToolType sourceDialect) =>
        SupportsSource(sourceDialect)
            ? null
            : $"SQL capability '{CapabilityId}' is currently declared only for the PostgreSQL source dialect; source dialect {sourceDialect} remains fail-closed.";

    internal static string? TargetValidationError(SqlAgentToolType provider) =>
        SupportsTarget(provider)
            ? null
            : $"SQL capability '{CapabilityId}' is currently lowered only for PostgreSQL targets; target provider {provider} remains fail-closed.";

    internal static bool HasExpressionItems(SqlStatement statement) => statement switch
    {
        InsertStatement insert => insert.Returning.Any(static item => item is DmlReturningExpressionItem),
        UpdateStatement update => update.Returning.Any(static item => item is DmlReturningExpressionItem),
        DeleteStatement delete => delete.Returning.Any(static item => item is DmlReturningExpressionItem),
        _ => false
    };

    internal static void ValidateSource(SqlStatement statement, SqlAgentToolType sourceDialect)
    {
        if (!HasExpressionItems(statement))
            return;

        var error = SourceValidationError(sourceDialect);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    internal static void ValidateExpression(DmlReturningExpressionItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ValidateNode(item.Expression);
    }

    private static void ValidateNode(SqlExpr expression)
    {
        switch (expression)
        {
            case ColumnExpr column:
                ValidateTargetColumn(column.Name);
                return;
            case BoundColumnExpr column:
                ValidateTargetColumn(column.Name);
                return;
            case LiteralExpr:
                return;
            case UnaryExpr { Operator: "+" or "-" } unary:
                ValidateNode(unary.Operand);
                return;
            case BinaryExpr { Operator: "+" or "-" or "*" or "/" or "%" or "||", LikeEscape: null } binary:
                ValidateNode(binary.Left);
                ValidateNode(binary.Right);
                return;
            case CastExpr cast:
                ValidateNode(cast.Expression);
                return;
            case FunctionCallExpr function:
                ValidateScalarFunction(function);
                return;
            case SimpleCaseExpr simpleCase:
                ValidateSimpleCase(simpleCase);
                return;
            default:
                throw new SqlCompilationException(
                    $"SQL capability '{CapabilityId}' currently accepts only unqualified target columns, literals, arithmetic/concatenation, unary +/-, CAST, registered direct-portable scalar functions, and simple CASE. Expression node {expression.GetType().Name} remains fail-closed.");
        }
    }

    private static void ValidateScalarFunction(FunctionCallExpr function)
    {
        if (function.Name.Parts.Length != 1)
        {
            throw new SqlCompilationException(
                $"SQL capability '{CapabilityId}' accepts canonical unqualified function names only; qualified function references remain fail-closed.");
        }

        var name = function.Name.Parts[0].Value;
        var contract = SqlCanonicalFunctionRegistry.Find(name);
        if (contract is null
            || contract.Kind != SqlCanonicalFunctionKind.Scalar
            || !contract.IsDirectPortable
            || function.IsDistinct
            || !contract.AcceptsArgumentCount(function.Arguments.Length))
        {
            throw new SqlCompilationException(
                $"SQL capability '{CapabilityId}' accepts only registered direct-portable scalar functions with canonical arity and no DISTINCT; function '{name}' remains fail-closed.");
        }

        foreach (var argument in function.Arguments)
            ValidateNode(argument);
    }

    private static void ValidateSimpleCase(SimpleCaseExpr simpleCase)
    {
        if (simpleCase.Branches.IsDefaultOrEmpty)
        {
            throw new SqlCompilationException(
                $"SQL capability '{CapabilityId}' requires simple CASE to contain at least one WHEN branch.");
        }

        foreach (var branch in simpleCase.Branches)
        {
            if (branch.Condition is not BinaryExpr
                {
                    Operator: "=",
                    LikeEscape: null
                } equality)
            {
                throw new SqlCompilationException(
                    $"SQL capability '{CapabilityId}' accepts only canonical simple CASE equality branches; searched CASE predicates remain fail-closed.");
            }

            ValidateNode(equality.Left);
            ValidateNode(equality.Right);
            ValidateNode(branch.Value);
        }

        if (simpleCase.ElseExpression is not null)
            ValidateNode(simpleCase.ElseExpression);
    }

    private static void ValidateTargetColumn(SqlIdentifier identifier)
    {
        if (identifier.Parts.Length != 1)
        {
            throw new SqlCompilationException(
                $"SQL capability '{CapabilityId}' accepts unqualified target-row columns only; qualified/source-table references remain fail-closed.");
        }
    }
}
