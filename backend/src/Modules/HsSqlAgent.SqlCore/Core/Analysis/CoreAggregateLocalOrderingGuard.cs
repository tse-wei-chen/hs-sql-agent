namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Fail-closed gate for aggregate-local ORDER BY while the structural model is introduced ahead of
/// provider capability support. Keeping this guard before normalization/lowering prevents a newly
/// modeled modifier from ever being silently ignored by an older visitor or lowerer.
/// </summary>
internal static class CoreAggregateLocalOrderingGuard
{
    public static void Validate(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        VisitStatement(statement);
    }

    private static void VisitStatement(SqlStatement statement)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                    VisitStatement(cte.Query);
                if (select.From is not null)
                    VisitSource(select.From);
                foreach (var join in select.Joins)
                {
                    VisitSource(join.Source);
                    if (join.Predicate is not null)
                        VisitExpression(join.Predicate);
                }
                foreach (var item in select.Select)
                    VisitExpression(item.Expression);
                if (select.Where is not null)
                    VisitExpression(select.Where);
                foreach (var expression in select.GroupBy)
                    VisitExpression(expression);
                if (select.Having is not null)
                    VisitExpression(select.Having);
                foreach (var item in select.OrderBy)
                    VisitExpression(item.Expression);
                return;

            case QueryStatement query:
                VisitStatement(query.Head);
                foreach (var operation in query.SetOperations)
                    VisitStatement(operation.Query);
                foreach (var item in query.OrderBy)
                    VisitExpression(item.Expression);
                return;

            case InsertStatement insert:
                switch (insert.Source)
                {
                    case InsertValuesSource values:
                        foreach (var row in values.Rows)
                        foreach (var value in row)
                            VisitExpression(value);
                        return;
                    case InsertQuerySource querySource:
                        VisitStatement(querySource.Query);
                        return;
                    default:
                        throw new SqlCompilationException(
                            $"Unsupported INSERT source while validating aggregate-local ordering: {insert.Source.GetType().Name}");
                }

            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                    VisitExpression(assignment.Value);
                if (update.Predicate is not null)
                    VisitExpression(update.Predicate);
                return;

            case DeleteStatement delete:
                if (delete.Predicate is not null)
                    VisitExpression(delete.Predicate);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement while validating aggregate-local ordering: {statement.GetType().Name}");
        }
    }

    private static void VisitSource(TableSource source)
    {
        switch (source)
        {
            case NamedTableSource:
                return;
            case DerivedTableSource derived:
                VisitStatement(derived.Query);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported source while validating aggregate-local ordering: {source.GetType().Name}");
        }
    }

    private static void VisitExpression(SqlExpr expression)
    {
        switch (expression)
        {
            case LiteralExpr:
            case ColumnExpr:
            case BoundColumnExpr:
            case IntervalExpr:
                return;

            case UnaryExpr unary:
                VisitExpression(unary.Operand);
                return;

            case BinaryExpr binary:
                VisitExpression(binary.Left);
                VisitExpression(binary.Right);
                return;

            case FunctionCallExpr function:
                if (!function.AggregateOrderBy.IsDefaultOrEmpty)
                {
                    throw new SqlCompilationException(
                        "Aggregate-local ORDER BY is structurally modeled but no Core provider capability is enabled yet.");
                }
                foreach (var argument in function.Arguments)
                    VisitExpression(argument);
                return;

            case FilterExpr filter:
                VisitExpression(filter.Expression);
                VisitExpression(filter.Predicate);
                return;

            case WindowedExpr windowed:
                VisitExpression(windowed.Expression);
                foreach (var partition in windowed.Window.PartitionBy)
                    VisitExpression(partition);
                foreach (var item in windowed.Window.OrderBy)
                    VisitExpression(item.Expression);
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
                if (@case.ElseExpression is not null)
                    VisitExpression(@case.ElseExpression);
                return;

            case InExpr @in:
                VisitExpression(@in.Value);
                foreach (var item in @in.Items)
                    VisitExpression(item);
                return;

            case BetweenExpr between:
                VisitExpression(between.Value);
                VisitExpression(between.Lower);
                VisitExpression(between.Upper);
                return;

            case IsNullExpr isNull:
                VisitExpression(isNull.Value);
                return;

            case SubqueryExpr subquery:
                VisitStatement(subquery.Query);
                return;

            case ExistsExpr exists:
                VisitStatement(exists.Query);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported expression while validating aggregate-local ordering: {expression.GetType().Name}");
        }
    }
}
