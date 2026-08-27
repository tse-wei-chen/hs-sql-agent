namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Central fail-closed traversal for the Core AST expression graph. Capability validators use this
/// instead of maintaining parallel statement/expression walkers, so a newly introduced AST shape
/// has one structural traversal point to update. Unknown statement/source/expression nodes throw.
/// </summary>
internal static class CoreSqlAstTraversal
{
    public static IEnumerable<SqlExpr> EnumerateExpressions(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        foreach (var expression in EnumerateStatementExpressions(statement))
            yield return expression;
    }

    public static IEnumerable<SqlExpr> EnumerateExpressions(SqlExpr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        foreach (var descendant in EnumerateExpressionTree(expression))
            yield return descendant;
    }

    private static IEnumerable<SqlExpr> EnumerateStatementExpressions(SqlStatement statement)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                foreach (var expression in EnumerateStatementExpressions(cte.Query))
                    yield return expression;

                if (select.From is not null)
                {
                    foreach (var expression in EnumerateSourceExpressions(select.From))
                        yield return expression;
                }

                foreach (var join in select.Joins)
                {
                    foreach (var expression in EnumerateSourceExpressions(join.Source))
                        yield return expression;
                    if (join.Predicate is not null)
                    {
                        foreach (var expression in EnumerateExpressionTree(join.Predicate))
                            yield return expression;
                    }
                }

                foreach (var item in select.Select)
                foreach (var expression in EnumerateExpressionTree(item.Expression))
                    yield return expression;

                if (select.Where is not null)
                {
                    foreach (var expression in EnumerateExpressionTree(select.Where))
                        yield return expression;
                }

                foreach (var groupBy in select.GroupBy)
                foreach (var expression in EnumerateExpressionTree(groupBy))
                    yield return expression;

                if (select.Having is not null)
                {
                    foreach (var expression in EnumerateExpressionTree(select.Having))
                        yield return expression;
                }

                foreach (var item in select.OrderBy)
                foreach (var expression in EnumerateExpressionTree(item.Expression))
                    yield return expression;
                yield break;

            case QueryStatement query:
                foreach (var expression in EnumerateStatementExpressions(query.Head))
                    yield return expression;
                foreach (var operation in query.SetOperations)
                foreach (var expression in EnumerateStatementExpressions(operation.Query))
                    yield return expression;
                foreach (var item in query.OrderBy)
                foreach (var expression in EnumerateExpressionTree(item.Expression))
                    yield return expression;
                yield break;

            case InsertStatement insert:
                switch (insert.Source)
                {
                    case InsertValuesSource values:
                        foreach (var row in values.Rows)
                        foreach (var value in row)
                        foreach (var expression in EnumerateExpressionTree(value))
                            yield return expression;
                        yield break;

                    case InsertQuerySource querySource:
                        foreach (var expression in EnumerateStatementExpressions(querySource.Query))
                            yield return expression;
                        yield break;

                    default:
                        throw new SqlCompilationException(
                            $"Unsupported INSERT source during Core AST traversal: {insert.Source.GetType().Name}");
                }

            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                foreach (var expression in EnumerateExpressionTree(assignment.Value))
                    yield return expression;
                if (update.Predicate is not null)
                {
                    foreach (var expression in EnumerateExpressionTree(update.Predicate))
                        yield return expression;
                }
                yield break;

            case DeleteStatement delete:
                if (delete.Predicate is not null)
                {
                    foreach (var expression in EnumerateExpressionTree(delete.Predicate))
                        yield return expression;
                }
                yield break;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during Core AST traversal: {statement.GetType().Name}");
        }
    }

    private static IEnumerable<SqlExpr> EnumerateSourceExpressions(TableSource source)
    {
        switch (source)
        {
            case NamedTableSource:
                yield break;

            case DerivedTableSource derived:
                foreach (var expression in EnumerateStatementExpressions(derived.Query))
                    yield return expression;
                yield break;

            default:
                throw new SqlCompilationException(
                    $"Unsupported table source during Core AST traversal: {source.GetType().Name}");
        }
    }

    private static IEnumerable<SqlExpr> EnumerateExpressionTree(SqlExpr expression)
    {
        yield return expression;

        switch (expression)
        {
            case LiteralExpr:
            case ColumnExpr:
            case BoundColumnExpr:
            case IntervalExpr:
                yield break;

            case UnaryExpr unary:
                foreach (var descendant in EnumerateExpressionTree(unary.Operand))
                    yield return descendant;
                yield break;

            case BinaryExpr binary:
                foreach (var descendant in EnumerateExpressionTree(binary.Left))
                    yield return descendant;
                foreach (var descendant in EnumerateExpressionTree(binary.Right))
                    yield return descendant;
                yield break;

            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                foreach (var descendant in EnumerateExpressionTree(argument))
                    yield return descendant;
                foreach (var item in function.AggregateOrderBy)
                foreach (var descendant in EnumerateExpressionTree(item.Expression))
                    yield return descendant;
                yield break;

            case FilterExpr filter:
                foreach (var descendant in EnumerateExpressionTree(filter.Expression))
                    yield return descendant;
                foreach (var descendant in EnumerateExpressionTree(filter.Predicate))
                    yield return descendant;
                yield break;

            case WindowedExpr windowed:
                foreach (var descendant in EnumerateExpressionTree(windowed.Expression))
                    yield return descendant;
                foreach (var partition in windowed.Window.PartitionBy)
                foreach (var descendant in EnumerateExpressionTree(partition))
                    yield return descendant;
                foreach (var item in windowed.Window.OrderBy)
                foreach (var descendant in EnumerateExpressionTree(item.Expression))
                    yield return descendant;
                yield break;

            case CastExpr cast:
                foreach (var descendant in EnumerateExpressionTree(cast.Expression))
                    yield return descendant;
                yield break;

            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    foreach (var descendant in EnumerateExpressionTree(branch.Condition))
                        yield return descendant;
                    foreach (var descendant in EnumerateExpressionTree(branch.Value))
                        yield return descendant;
                }
                if (@case.ElseExpression is not null)
                {
                    foreach (var descendant in EnumerateExpressionTree(@case.ElseExpression))
                        yield return descendant;
                }
                yield break;

            case InExpr @in:
                foreach (var descendant in EnumerateExpressionTree(@in.Value))
                    yield return descendant;
                foreach (var item in @in.Items)
                foreach (var descendant in EnumerateExpressionTree(item))
                    yield return descendant;
                yield break;

            case BetweenExpr between:
                foreach (var descendant in EnumerateExpressionTree(between.Value))
                    yield return descendant;
                foreach (var descendant in EnumerateExpressionTree(between.Lower))
                    yield return descendant;
                foreach (var descendant in EnumerateExpressionTree(between.Upper))
                    yield return descendant;
                yield break;

            case IsNullExpr isNull:
                foreach (var descendant in EnumerateExpressionTree(isNull.Value))
                    yield return descendant;
                yield break;

            case SubqueryExpr subquery:
                foreach (var descendant in EnumerateStatementExpressions(subquery.Query))
                    yield return descendant;
                yield break;

            case ExistsExpr exists:
                foreach (var descendant in EnumerateStatementExpressions(exists.Query))
                    yield return descendant;
                yield break;

            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during Core AST traversal: {expression.GetType().Name}");
        }
    }
}
