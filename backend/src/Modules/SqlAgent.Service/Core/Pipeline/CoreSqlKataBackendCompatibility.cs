using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;

namespace SqlAgent.Service.Core.Pipeline;

/// <summary>
/// Guards query shapes whose CTE scope cannot be preserved by the available lowering path.
/// Statement-root INSERT..SELECT CTEs have a dedicated provider-aware lowerer; derived-table-local
/// and set-branch-local CTEs still flow through SqlKata nested Select compilation and remain
/// fail-closed.
/// </summary>
internal static class CoreSqlKataBackendCompatibility
{
    public static void ValidateQuery(SqlStatement statement) =>
        ValidateStatement(statement, QueryPosition.Root);

    public static void ValidateInsertSelect(SqlStatement statement) =>
        ValidateStatement(statement, QueryPosition.InsertSelectSource);

    private static void ValidateStatement(SqlStatement statement, QueryPosition position)
    {
        switch (statement)
        {
            case SelectStatement select:
                ValidateCtePlacement(select.Ctes, position);
                foreach (var cte in select.Ctes)
                    ValidateStatement(cte.Query, QueryPosition.CteDefinition);
                if (select.From is DerivedTableSource derived)
                    ValidateStatement(derived.Query, QueryPosition.DerivedTable);
                foreach (var join in select.Joins)
                    if (join.Source is DerivedTableSource joinedDerived)
                        ValidateStatement(joinedDerived.Query, QueryPosition.DerivedTable);
                VisitSubqueryExpressions(select);
                return;

            case QueryStatement query:
                ValidateCtePlacement(query.Head.Ctes, position);
                if (position != QueryPosition.InsertSelectSource
                    && !query.Head.Ctes.IsDefaultOrEmpty
                    && RequiresSetTailWrapper(query))
                {
                    throw CteScopeError(
                        "select.cte_scope",
                        "a set-operation query with a root CTE and outer ORDER BY/LIMIT/OFFSET is wrapped as a derived table, which would drop the CTE definition");
                }
                ValidateStatement(query.Head, position);
                foreach (var operation in query.SetOperations)
                    ValidateStatement(operation.Query, QueryPosition.SetBranch);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement for SqlKata backend compatibility validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateCtePlacement(
        System.Collections.Immutable.ImmutableArray<CteDefinition> ctes,
        QueryPosition position)
    {
        if (ctes.IsDefaultOrEmpty)
            return;

        switch (position)
        {
            case QueryPosition.DerivedTable:
                throw CteScopeError(
                    "select.cte_scope",
                    "a derived-table-local CTE would be compiled through SqlKata CompileSelectQuery without its CTE definition");
            case QueryPosition.SetBranch:
                throw CteScopeError(
                    "select.cte_scope",
                    "a set-operation-branch-local CTE would be compiled through SqlKata CompileSelectQuery without its CTE definition");
        }
    }

    private static SqlCompilationException CteScopeError(string capability, string detail) =>
        new($"SQL capability '{capability}' is not supported by the current SqlKata backend: {detail}.");

    private static bool RequiresSetTailWrapper(QueryStatement query) =>
        !query.OrderBy.IsDefaultOrEmpty
        || query.Limit is not null
        || query.Offset is > 0;

    private static void VisitSubqueryExpressions(SelectStatement select)
    {
        foreach (var item in select.Select) VisitExpression(item.Expression);
        if (select.Where is not null) VisitExpression(select.Where);
        foreach (var expression in select.GroupBy) VisitExpression(expression);
        if (select.Having is not null) VisitExpression(select.Having);
        foreach (var item in select.OrderBy) VisitExpression(item.Expression);
        foreach (var join in select.Joins)
            if (join.Predicate is not null) VisitExpression(join.Predicate);
    }

    private static void VisitExpression(SqlExpr expression)
    {
        switch (expression)
        {
            case SubqueryExpr subquery:
                ValidateStatement(subquery.Query, QueryPosition.ScalarSubquery);
                return;
            case ExistsExpr exists:
                ValidateStatement(exists.Query, QueryPosition.ScalarSubquery);
                return;
            case UnaryExpr unary:
                VisitExpression(unary.Operand);
                return;
            case BinaryExpr binary:
                VisitExpression(binary.Left);
                VisitExpression(binary.Right);
                return;
            case FunctionCallExpr function:
                foreach (var argument in function.Arguments) VisitExpression(argument);
                return;
            case FilterExpr filter:
                VisitExpression(filter.Expression);
                VisitExpression(filter.Predicate);
                return;
            case WindowedExpr windowed:
                VisitExpression(windowed.Expression);
                foreach (var partition in windowed.Window.PartitionBy) VisitExpression(partition);
                foreach (var item in windowed.Window.OrderBy) VisitExpression(item.Expression);
                return;
            case CastExpr cast:
                VisitExpression(cast.Expression);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    VisitExpression(branch.Condition);
                    VisitExpression(branch.Value);
                }
                if (@case.ElseExpression is not null) VisitExpression(@case.ElseExpression);
                return;
            case InExpr @in:
                VisitExpression(@in.Value);
                foreach (var item in @in.Items) VisitExpression(item);
                return;
            case BetweenExpr between:
                VisitExpression(between.Value);
                VisitExpression(between.Lower);
                VisitExpression(between.Upper);
                return;
            case IsNullExpr isNull:
                VisitExpression(isNull.Value);
                return;
            case LiteralExpr or ColumnExpr or BoundColumnExpr or IntervalExpr:
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported expression for SqlKata backend compatibility validation: {expression.GetType().Name}");
        }
    }

    private enum QueryPosition
    {
        Root,
        InsertSelectSource,
        CteDefinition,
        DerivedTable,
        SetBranch,
        ScalarSubquery
    }
}
