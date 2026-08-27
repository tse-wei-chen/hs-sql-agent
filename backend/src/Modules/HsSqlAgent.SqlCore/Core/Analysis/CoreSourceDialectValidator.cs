namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Validates provider-specific source syntax while the bound AST still carries the original
/// function names. This prevents normalization from turning SQL that is not valid for the declared
/// source dialect into valid SQL for a different target provider.
/// </summary>
internal static class CoreSourceDialectValidator
{
    public static void Validate(SqlStatement statement, SqlAgentToolType sourceDialect)
    {
        ArgumentNullException.ThrowIfNull(statement);
        VisitStatement(statement, sourceDialect);
    }

    private static void VisitStatement(SqlStatement statement, SqlAgentToolType sourceDialect)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                    VisitStatement(cte.Query, sourceDialect);
                if (select.From is not null)
                    VisitSource(select.From, sourceDialect);
                foreach (var join in select.Joins)
                {
                    VisitSource(join.Source, sourceDialect);
                    if (join.Predicate is not null) ValidateExpressionTree(join.Predicate, sourceDialect);
                }
                foreach (var item in select.Select) ValidateExpressionTree(item.Expression, sourceDialect);
                if (select.Where is not null) ValidateExpressionTree(select.Where, sourceDialect);
                foreach (var item in select.GroupBy) ValidateExpressionTree(item, sourceDialect);
                if (select.Having is not null) ValidateExpressionTree(select.Having, sourceDialect);
                foreach (var item in select.OrderBy) VisitOrderBy(item, sourceDialect);
                return;

            case QueryStatement query:
                VisitStatement(query.Head, sourceDialect);
                foreach (var operation in query.SetOperations)
                    VisitStatement(operation.Query, sourceDialect);
                foreach (var item in query.OrderBy) VisitOrderBy(item, sourceDialect);
                return;

            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                    ValidateExpressionTree(assignment.Value, sourceDialect);
                if (update.Predicate is not null) ValidateExpressionTree(update.Predicate, sourceDialect);
                return;

            case DeleteStatement delete:
                if (delete.Predicate is not null) ValidateExpressionTree(delete.Predicate, sourceDialect);
                return;

            case InsertStatement insert:
                switch (insert.Source)
                {
                    case InsertValuesSource values:
                        foreach (var row in values.Rows)
                        foreach (var value in row)
                            ValidateExpressionTree(value, sourceDialect);
                        return;
                    case InsertQuerySource querySource:
                        VisitStatement(querySource.Query, sourceDialect);
                        return;
                    default:
                        throw new SqlCompilationException(
                            $"Unsupported INSERT source during source-dialect validation: {insert.Source.GetType().Name}");
                }

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during source-dialect validation: {statement.GetType().Name}");
        }
    }

    private static void VisitSource(TableSource source, SqlAgentToolType sourceDialect)
    {
        if (source is DerivedTableSource derived)
            VisitStatement(derived.Query, sourceDialect);
    }

    private static void VisitOrderBy(OrderByItem item, SqlAgentToolType sourceDialect)
    {
        ValidateOrderByModifier(item, sourceDialect);
        ValidateExpressionTree(item.Expression, sourceDialect);
    }

    private static void ValidateExpressionTree(
        SqlExpr expression,
        SqlAgentToolType sourceDialect)
    {
        foreach (var node in CoreSqlAstTraversal.EnumerateExpressions(expression))
            ValidateExpressionNode(node, sourceDialect);
    }

    private static void ValidateExpressionNode(
        SqlExpr expression,
        SqlAgentToolType sourceDialect)
    {
        switch (expression)
        {
            case IntervalExpr:
                var intervalError = SqlIntervalLiteralCapabilityRules.SourceValidationError(sourceDialect);
                if (intervalError is not null)
                    throw new SqlCompilationException(intervalError);
                return;

            case BinaryExpr binary
                when binary.Operator.Equals("||", StringComparison.OrdinalIgnoreCase):
                var concatSourceError = SqlConcatCapabilityRules.RawSourceSyntaxError(sourceDialect);
                if (concatSourceError is not null)
                    throw new SqlCompilationException(concatSourceError);
                return;

            case FunctionCallExpr function:
                ValidateFunction(function, sourceDialect);
                ValidateAggregateSeparatorClause(function, sourceDialect);
                foreach (var item in function.AggregateOrderBy)
                    ValidateOrderByModifier(item, sourceDialect);
                return;

            case FilterExpr:
                var filterSourceError = SqlAggregateFilterCapabilityRules.RawSourceSyntaxError(sourceDialect);
                if (filterSourceError is not null)
                    throw new SqlCompilationException(filterSourceError);
                return;

            case WindowedExpr windowed:
                foreach (var item in windowed.Window.OrderBy)
                    ValidateOrderByModifier(item, sourceDialect);
                return;

            case SubqueryExpr subquery:
                ValidateStatementOrderByModifiers(subquery.Query, sourceDialect);
                return;

            case ExistsExpr exists:
                ValidateStatementOrderByModifiers(exists.Query, sourceDialect);
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// The shared expression traversal descends through scalar/EXISTS subqueries, but ORDER BY
    /// modifiers are statement metadata rather than SqlExpr nodes. Keep this small structural walk
    /// only for that metadata so source validation does not reintroduce a parallel expression walker.
    /// Deeper scalar/EXISTS subqueries are reached by CoreSqlAstTraversal and invoke this method from
    /// their own wrapper node.
    /// </summary>
    private static void ValidateStatementOrderByModifiers(
        SqlStatement statement,
        SqlAgentToolType sourceDialect)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                    ValidateStatementOrderByModifiers(cte.Query, sourceDialect);
                if (select.From is DerivedTableSource derived)
                    ValidateStatementOrderByModifiers(derived.Query, sourceDialect);
                foreach (var join in select.Joins)
                {
                    if (join.Source is DerivedTableSource joinedDerived)
                        ValidateStatementOrderByModifiers(joinedDerived.Query, sourceDialect);
                }
                foreach (var item in select.OrderBy)
                    ValidateOrderByModifier(item, sourceDialect);
                return;

            case QueryStatement query:
                ValidateStatementOrderByModifiers(query.Head, sourceDialect);
                foreach (var operation in query.SetOperations)
                    ValidateStatementOrderByModifiers(operation.Query, sourceDialect);
                foreach (var item in query.OrderBy)
                    ValidateOrderByModifier(item, sourceDialect);
                return;

            case InsertStatement { Source: InsertQuerySource querySource }:
                ValidateStatementOrderByModifiers(querySource.Query, sourceDialect);
                return;

            case InsertStatement:
            case UpdateStatement:
            case DeleteStatement:
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during source ORDER BY metadata validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateOrderByModifier(
        OrderByItem item,
        SqlAgentToolType sourceDialect)
    {
        var error = SqlNullOrderingCapabilityRules.SourceValidationError(
            sourceDialect,
            item.NullOrdering);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateFunction(FunctionCallExpr function, SqlAgentToolType sourceDialect)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        var arity = function.Arguments.Length;

        if (SqlSourceFunctionRegistry.Find(name) is { } contract)
        {
            var error = contract.ValidationError(sourceDialect, arity);
            if (error is not null)
                throw new SqlCompilationException(error);
            return;
        }

        switch (name)
        {
            case "CURRENT_DATE":
                ValidateCurrentTemporalSource(
                    SqlCurrentTemporalKind.Date,
                    sourceDialect);
                return;
            case "CURRENT_TIME":
                ValidateCurrentTemporalSource(
                    SqlCurrentTemporalKind.Time,
                    sourceDialect);
                return;
            case "CURRENT_TIMESTAMP":
                ValidateCurrentTemporalSource(
                    SqlCurrentTemporalKind.Timestamp,
                    sourceDialect);
                return;
        }
    }

    private static void ValidateAggregateSeparatorClause(
        FunctionCallExpr function,
        SqlAgentToolType sourceDialect)
    {
        if (function.AggregateSeparatorClause is null) return;

        var name = IdentifierText(function.Name).ToUpperInvariant();
        if (sourceDialect != SqlAgentToolType.MySQL || name != "GROUP_CONCAT")
        {
            throw new SqlCompilationException(
                "Aggregate SEPARATOR clause is modeled only for MySQL GROUP_CONCAT raw source syntax.");
        }
    }

    private static void ValidateCurrentTemporalSource(
        SqlCurrentTemporalKind kind,
        SqlAgentToolType sourceDialect)
    {
        var error = SqlCurrentTemporalCapabilityRules.SourceValidationError(
            kind,
            sourceDialect);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
