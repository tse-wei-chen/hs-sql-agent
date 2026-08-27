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

    public static IEnumerable<JoinSource> EnumerateJoins(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        foreach (var join in EnumerateStatementJoins(statement))
            yield return join;
    }

    private static IEnumerable<JoinSource> EnumerateStatementJoins(SqlStatement statement)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                foreach (var join in EnumerateStatementJoins(cte.Query))
                    yield return join;

                if (select.From is not null)
                {
                    foreach (var join in EnumerateSourceJoins(select.From))
                        yield return join;
                }

                foreach (var join in select.Joins)
                {
                    yield return join;

                    foreach (var nested in EnumerateSourceJoins(join.Source))
                        yield return nested;

                    if (join.Predicate is not null)
                    {
                        foreach (var nested in EnumerateExpressionJoins(join.Predicate))
                            yield return nested;
                    }
                }

                foreach (var item in select.Select)
                foreach (var join in EnumerateExpressionJoins(item.Expression))
                    yield return join;

                if (select.Where is not null)
                {
                    foreach (var join in EnumerateExpressionJoins(select.Where))
                        yield return join;
                }

                foreach (var groupBy in select.GroupBy)
                foreach (var join in EnumerateExpressionJoins(groupBy))
                    yield return join;

                if (select.Having is not null)
                {
                    foreach (var join in EnumerateExpressionJoins(select.Having))
                        yield return join;
                }

                foreach (var item in select.OrderBy)
                foreach (var join in EnumerateExpressionJoins(item.Expression))
                    yield return join;
                yield break;

            case QueryStatement query:
                foreach (var join in EnumerateStatementJoins(query.Head))
                    yield return join;
                foreach (var operation in query.SetOperations)
                foreach (var join in EnumerateStatementJoins(operation.Query))
                    yield return join;
                foreach (var item in query.OrderBy)
                foreach (var join in EnumerateExpressionJoins(item.Expression))
                    yield return join;
                yield break;

            case InsertStatement insert:
                switch (insert.Source)
                {
                    case InsertValuesSource values:
                        foreach (var row in values.Rows)
                        foreach (var value in row)
                        foreach (var join in EnumerateExpressionJoins(value))
                            yield return join;
                        yield break;

                    case InsertQuerySource querySource:
                        foreach (var join in EnumerateStatementJoins(querySource.Query))
                            yield return join;
                        yield break;

                    default:
                        throw new SqlCompilationException(
                            $"Unsupported INSERT source during Core JOIN traversal: {insert.Source.GetType().Name}");
                }

            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                foreach (var join in EnumerateExpressionJoins(assignment.Value))
                    yield return join;
                if (update.Predicate is not null)
                {
                    foreach (var join in EnumerateExpressionJoins(update.Predicate))
                        yield return join;
                }
                yield break;

            case DeleteStatement delete:
                if (delete.Predicate is not null)
                {
                    foreach (var join in EnumerateExpressionJoins(delete.Predicate))
                        yield return join;
                }
                yield break;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during Core JOIN traversal: {statement.GetType().Name}");
        }
    }

    private static IEnumerable<JoinSource> EnumerateSourceJoins(TableSource source)
    {
        switch (source)
        {
            case NamedTableSource:
                yield break;

            case DerivedTableSource derived:
                foreach (var join in EnumerateStatementJoins(derived.Query))
                    yield return join;
                yield break;

            default:
                throw new SqlCompilationException(
                    $"Unsupported table source during Core JOIN traversal: {source.GetType().Name}");
        }
    }

    private static IEnumerable<JoinSource> EnumerateExpressionJoins(SqlExpr expression)
    {
        switch (expression)
        {
            case SubqueryExpr subquery:
                foreach (var join in EnumerateStatementJoins(subquery.Query))
                    yield return join;
                yield break;

            case ExistsExpr exists:
                foreach (var join in EnumerateStatementJoins(exists.Query))
                    yield return join;
                yield break;
        }

        foreach (var child in EnumerateDirectChildren(expression))
        foreach (var join in EnumerateExpressionJoins(child))
            yield return join;
    }

    /// <summary>
    /// Enumerates the direct structural expression children of one Core AST expression without
    /// crossing into a subquery statement. This is the single child-shape map used by recursive
    /// expression walkers that need to retain control over statement/query-position semantics.
    /// Unknown expression nodes fail closed here.
    /// </summary>
    internal static IEnumerable<SqlExpr> EnumerateDirectChildren(SqlExpr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        switch (expression)
        {
            case LiteralExpr:
            case ColumnExpr:
            case BoundColumnExpr:
            case IntervalExpr:
            case SubqueryExpr:
            case ExistsExpr:
                yield break;

            case UnaryExpr unary:
                yield return unary.Operand;
                yield break;

            case BinaryExpr binary:
                yield return binary.Left;
                yield return binary.Right;
                yield break;

            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                    yield return argument;
                foreach (var item in function.AggregateOrderBy)
                    yield return item.Expression;
                yield break;

            case FilterExpr filter:
                yield return filter.Expression;
                yield return filter.Predicate;
                yield break;

            case WindowedExpr windowed:
                yield return windowed.Expression;
                foreach (var partition in windowed.Window.PartitionBy)
                    yield return partition;
                foreach (var item in windowed.Window.OrderBy)
                    yield return item.Expression;
                yield break;

            case CastExpr cast:
                yield return cast.Expression;
                yield break;

            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    yield return branch.Condition;
                    yield return branch.Value;
                }
                if (@case.ElseExpression is not null)
                    yield return @case.ElseExpression;
                yield break;

            case InExpr @in:
                yield return @in.Value;
                foreach (var item in @in.Items)
                    yield return item;
                yield break;

            case BetweenExpr between:
                yield return between.Value;
                yield return between.Lower;
                yield return between.Upper;
                yield break;

            case IsNullExpr isNull:
                yield return isNull.Value;
                yield break;

            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during Core AST traversal: {expression.GetType().Name}");
        }
    }

    private static IEnumerable<SqlExpr> EnumerateExpressionTree(SqlExpr expression)
    {
        yield return expression;

        switch (expression)
        {
            case SubqueryExpr subquery:
                foreach (var descendant in EnumerateStatementExpressions(subquery.Query))
                    yield return descendant;
                yield break;

            case ExistsExpr exists:
                foreach (var descendant in EnumerateStatementExpressions(exists.Query))
                    yield return descendant;
                yield break;
        }

        foreach (var child in EnumerateDirectChildren(expression))
        foreach (var descendant in EnumerateExpressionTree(child))
            yield return descendant;
    }

}
