namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Enforces runtime-version boundaries for aggregate FILTER after binding has exposed the complete
/// query graph. Source and target profiles remain independent: a source profile never authorizes a
/// target capability, even when both sides name the same provider. Oracle 26ai additionally limits
/// FILTER conditions, so those predicates are checked while bound outer-reference provenance is
/// still available.
/// </summary>
internal static class CoreAggregateFilterProfileValidator
{
    public static void Validate(
        SqlStatement statement,
        bool enforceSourceDialectSyntax,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile,
        SqlAgentToolType targetProvider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (!ContainsFilter(statement)) return;

        if (enforceSourceDialectSyntax)
        {
            ValidateRuntime("source", sourceDialect, sourceProfile);
            if (sourceDialect == SqlAgentToolType.Oracle)
                ValidateOracleFilterPredicates(statement, "source");
        }

        ValidateRuntime("target", targetProvider, targetProfile);
        if (targetProvider == SqlAgentToolType.Oracle)
            ValidateOracleFilterPredicates(statement, "target");
    }

    private static void ValidateRuntime(
        string side,
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? profile)
    {
        var error = SqlAggregateFilterCapabilityRules.ValidationError(provider, profile, side);
        if (error is not null)
            throw new SqlCompilationException(error);
    }

    private static void ValidateOracleFilterPredicates(SqlStatement statement, string side)
    {
        switch (statement)
        {
            case SelectStatement select:
                foreach (var cte in select.Ctes)
                    ValidateOracleFilterPredicates(cte.Query, side);
                if (select.From is not null)
                    ValidateOracleFilterPredicates(select.From, side);
                foreach (var join in select.Joins)
                {
                    ValidateOracleFilterPredicates(join.Source, side);
                    if (join.Predicate is not null)
                        VisitOracleFilterExpressions(join.Predicate, side);
                }
                foreach (var item in select.Select)
                    VisitOracleFilterExpressions(item.Expression, side);
                if (select.Where is not null)
                    VisitOracleFilterExpressions(select.Where, side);
                foreach (var expression in select.GroupBy)
                    VisitOracleFilterExpressions(expression, side);
                if (select.Having is not null)
                    VisitOracleFilterExpressions(select.Having, side);
                foreach (var item in select.OrderBy)
                    VisitOracleFilterExpressions(item.Expression, side);
                return;

            case QueryStatement query:
                ValidateOracleFilterPredicates(query.Head, side);
                foreach (var operation in query.SetOperations)
                    ValidateOracleFilterPredicates(operation.Query, side);
                foreach (var item in query.OrderBy)
                    VisitOracleFilterExpressions(item.Expression, side);
                return;

            case InsertStatement insert:
                switch (insert.Source)
                {
                    case InsertValuesSource values:
                        foreach (var row in values.Rows)
                        foreach (var value in row)
                            VisitOracleFilterExpressions(value, side);
                        return;
                    case InsertQuerySource querySource:
                        ValidateOracleFilterPredicates(querySource.Query, side);
                        return;
                    default:
                        throw new SqlCompilationException(
                            $"Unsupported INSERT source during Oracle aggregate FILTER validation: {insert.Source.GetType().Name}");
                }

            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                    VisitOracleFilterExpressions(assignment.Value, side);
                if (update.Predicate is not null)
                    VisitOracleFilterExpressions(update.Predicate, side);
                return;

            case DeleteStatement delete:
                if (delete.Predicate is not null)
                    VisitOracleFilterExpressions(delete.Predicate, side);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during Oracle aggregate FILTER validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateOracleFilterPredicates(TableSource source, string side)
    {
        switch (source)
        {
            case NamedTableSource:
                return;
            case DerivedTableSource derived:
                ValidateOracleFilterPredicates(derived.Query, side);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported table source during Oracle aggregate FILTER validation: {source.GetType().Name}");
        }
    }

    private static void VisitOracleFilterExpressions(SqlExpr expression, string side)
    {
        switch (expression)
        {
            case LiteralExpr:
            case ColumnExpr:
            case BoundColumnExpr:
            case IntervalExpr:
                return;

            case UnaryExpr unary:
                VisitOracleFilterExpressions(unary.Operand, side);
                return;

            case BinaryExpr binary:
                VisitOracleFilterExpressions(binary.Left, side);
                VisitOracleFilterExpressions(binary.Right, side);
                return;

            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                    VisitOracleFilterExpressions(argument, side);
                return;

            case FilterExpr filter:
                ValidateOraclePredicate(filter.Predicate, side);
                VisitOracleFilterExpressions(filter.Expression, side);
                VisitOracleFilterExpressions(filter.Predicate, side);
                return;

            case WindowedExpr windowed:
                VisitOracleFilterExpressions(windowed.Expression, side);
                foreach (var partition in windowed.Window.PartitionBy)
                    VisitOracleFilterExpressions(partition, side);
                foreach (var item in windowed.Window.OrderBy)
                    VisitOracleFilterExpressions(item.Expression, side);
                return;

            case CastExpr cast:
                VisitOracleFilterExpressions(cast.Expression, side);
                return;

            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    VisitOracleFilterExpressions(branch.Condition, side);
                    VisitOracleFilterExpressions(branch.Value, side);
                }
                if (@case.ElseExpression is not null)
                    VisitOracleFilterExpressions(@case.ElseExpression, side);
                return;

            case InExpr @in:
                VisitOracleFilterExpressions(@in.Value, side);
                foreach (var item in @in.Items)
                    VisitOracleFilterExpressions(item, side);
                return;

            case BetweenExpr between:
                VisitOracleFilterExpressions(between.Value, side);
                VisitOracleFilterExpressions(between.Lower, side);
                VisitOracleFilterExpressions(between.Upper, side);
                return;

            case IsNullExpr isNull:
                VisitOracleFilterExpressions(isNull.Value, side);
                return;

            case SubqueryExpr subquery:
                ValidateOracleFilterPredicates(subquery.Query, side);
                return;

            case ExistsExpr exists:
                ValidateOracleFilterPredicates(exists.Query, side);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during Oracle aggregate FILTER validation: {expression.GetType().Name}");
        }
    }

    private static void ValidateOraclePredicate(SqlExpr expression, string side)
    {
        switch (expression)
        {
            case BoundColumnExpr { IsOuterReference: true }:
                throw OraclePredicateError(side, "outer references");

            case SubqueryExpr:
            case ExistsExpr:
                throw OraclePredicateError(side, "subqueries");

            case WindowedExpr:
                throw OraclePredicateError(side, "window functions");

            case LiteralExpr:
            case ColumnExpr:
            case BoundColumnExpr:
            case IntervalExpr:
                return;

            case UnaryExpr unary:
                ValidateOraclePredicate(unary.Operand, side);
                return;

            case BinaryExpr binary:
                ValidateOraclePredicate(binary.Left, side);
                ValidateOraclePredicate(binary.Right, side);
                return;

            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                    ValidateOraclePredicate(argument, side);
                return;

            case FilterExpr filter:
                ValidateOraclePredicate(filter.Expression, side);
                ValidateOraclePredicate(filter.Predicate, side);
                return;

            case CastExpr cast:
                ValidateOraclePredicate(cast.Expression, side);
                return;

            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    ValidateOraclePredicate(branch.Condition, side);
                    ValidateOraclePredicate(branch.Value, side);
                }
                if (@case.ElseExpression is not null)
                    ValidateOraclePredicate(@case.ElseExpression, side);
                return;

            case InExpr @in:
                ValidateOraclePredicate(@in.Value, side);
                foreach (var item in @in.Items)
                    ValidateOraclePredicate(item, side);
                return;

            case BetweenExpr between:
                ValidateOraclePredicate(between.Value, side);
                ValidateOraclePredicate(between.Lower, side);
                ValidateOraclePredicate(between.Upper, side);
                return;

            case IsNullExpr isNull:
                ValidateOraclePredicate(isNull.Value, side);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during Oracle FILTER predicate validation: {expression.GetType().Name}");
        }
    }

    private static SqlCompilationException OraclePredicateError(string side, string restriction) =>
        new(
            $"SQL capability 'expression.filter' requires an Oracle 26ai {side} FILTER condition " +
            $"without {restriction}.");

    private static bool ContainsFilter(SqlStatement statement) => statement switch
    {
        SelectStatement select =>
            select.Ctes.Any(cte => ContainsFilter(cte.Query))
            || select.From is not null && ContainsFilter(select.From)
            || select.Joins.Any(join =>
                ContainsFilter(join.Source)
                || join.Predicate is not null && ContainsFilter(join.Predicate))
            || select.Select.Any(item => ContainsFilter(item.Expression))
            || select.Where is not null && ContainsFilter(select.Where)
            || select.GroupBy.Any(ContainsFilter)
            || select.Having is not null && ContainsFilter(select.Having)
            || select.OrderBy.Any(item => ContainsFilter(item.Expression)),
        QueryStatement query =>
            ContainsFilter(query.Head)
            || query.SetOperations.Any(operation => ContainsFilter(operation.Query))
            || query.OrderBy.Any(item => ContainsFilter(item.Expression)),
        InsertStatement insert => insert.Source switch
        {
            InsertValuesSource values => values.Rows.Any(row => row.Any(ContainsFilter)),
            InsertQuerySource querySource => ContainsFilter(querySource.Query),
            _ => throw new SqlCompilationException(
                $"Unsupported INSERT source during aggregate FILTER profile validation: {insert.Source.GetType().Name}")
        },
        UpdateStatement update =>
            update.Assignments.Any(assignment => ContainsFilter(assignment.Value))
            || update.Predicate is not null && ContainsFilter(update.Predicate),
        DeleteStatement delete =>
            delete.Predicate is not null && ContainsFilter(delete.Predicate),
        _ => throw new SqlCompilationException(
            $"Unsupported statement during aggregate FILTER profile validation: {statement.GetType().Name}")
    };

    private static bool ContainsFilter(TableSource source) => source switch
    {
        NamedTableSource => false,
        DerivedTableSource derived => ContainsFilter(derived.Query),
        _ => throw new SqlCompilationException(
            $"Unsupported table source during aggregate FILTER profile validation: {source.GetType().Name}")
    };

    private static bool ContainsFilter(SqlExpr expression) => expression switch
    {
        FilterExpr => true,
        LiteralExpr or ColumnExpr or BoundColumnExpr or IntervalExpr => false,
        UnaryExpr unary => ContainsFilter(unary.Operand),
        BinaryExpr binary => ContainsFilter(binary.Left) || ContainsFilter(binary.Right),
        FunctionCallExpr function => function.Arguments.Any(ContainsFilter),
        WindowedExpr windowed =>
            ContainsFilter(windowed.Expression)
            || windowed.Window.PartitionBy.Any(ContainsFilter)
            || windowed.Window.OrderBy.Any(item => ContainsFilter(item.Expression)),
        CastExpr cast => ContainsFilter(cast.Expression),
        CaseExpr @case =>
            @case.Branches.Any(branch => ContainsFilter(branch.Condition) || ContainsFilter(branch.Value))
            || @case.ElseExpression is not null && ContainsFilter(@case.ElseExpression),
        InExpr @in => ContainsFilter(@in.Value) || @in.Items.Any(ContainsFilter),
        BetweenExpr between =>
            ContainsFilter(between.Value)
            || ContainsFilter(between.Lower)
            || ContainsFilter(between.Upper),
        IsNullExpr isNull => ContainsFilter(isNull.Value),
        SubqueryExpr subquery => ContainsFilter(subquery.Query),
        ExistsExpr exists => ContainsFilter(exists.Query),
        _ => throw new SqlCompilationException(
            $"Unsupported expression during aggregate FILTER profile validation: {expression.GetType().Name}")
    };
}
