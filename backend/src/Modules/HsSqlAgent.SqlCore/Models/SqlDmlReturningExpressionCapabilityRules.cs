namespace HsSqlAgent.SqlCore.Models;

/// <summary>
/// Provider contract for richer DML result expressions. This is intentionally narrower than the
/// portable column/wildcard RETURNING contract: the first proven slice is PostgreSQL only, and the
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
            default:
                throw new SqlCompilationException(
                    $"SQL capability '{CapabilityId}' currently accepts only unqualified target columns, literals, arithmetic/concatenation, unary +/-, CAST, and registered direct-portable scalar functions. Expression node {expression.GetType().Name} remains fail-closed.");
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

    private static void ValidateTargetColumn(SqlIdentifier identifier)
    {
        if (identifier.Parts.Length != 1)
        {
            throw new SqlCompilationException(
                $"SQL capability '{CapabilityId}' accepts unqualified target-row columns only; qualified/source-table references remain fail-closed.");
        }
    }
}
