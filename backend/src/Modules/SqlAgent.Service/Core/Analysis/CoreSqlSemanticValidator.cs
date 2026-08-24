using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Analysis;

/// <summary>
/// Validates semantic placement rules that are independent of provider SQL rendering. The parser
/// can represent these expression trees, but accepting a modeled node does not imply it is legal in
/// every SQL clause. Reject invalid set/window-function placement and statement shapes before they
/// can reach provider lowering or the database.
/// </summary>
internal static class CoreSqlSemanticValidator
{
    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "AVG", "COUNT", "MAX", "MIN", "SUM", "CORE_STRING_AGG"
    };

    private static readonly HashSet<string> WindowFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ROW_NUMBER", "RANK", "DENSE_RANK", "PERCENT_RANK", "CUME_DIST",
        "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE", "NTH_VALUE", "NTILE"
    };

    public static void Validate(SqlStatement statement, SqlAgentToolType provider)
    {
        ArgumentNullException.ThrowIfNull(statement);
        switch (statement)
        {
            case SelectStatement select:
                ValidateSelect(select, provider);
                return;
            case QueryStatement query:
                ValidateSelect(query.Head, provider);
                foreach (var operation in query.SetOperations)
                    Validate(operation.Query, provider);
                ValidateSetOperationWidths(query);
                ValidateOrderingSemantics(
                    query.OrderBy,
                    query.Head,
                    provider,
                    setOperationScope: true);
                foreach (var item in query.OrderBy)
                    Visit(item.Expression, ClauseContext.OrderBy, insideSetFunction: false, withinWindow: false, provider);
                return;
            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                    Visit(assignment.Value, ClauseContext.Assignment, insideSetFunction: false, withinWindow: false, provider);
                if (update.Predicate is not null)
                    Visit(update.Predicate, ClauseContext.Predicate, insideSetFunction: false, withinWindow: false, provider);
                return;
            case DeleteStatement delete:
                if (delete.Predicate is not null)
                    Visit(delete.Predicate, ClauseContext.Predicate, insideSetFunction: false, withinWindow: false, provider);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during Core semantic validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateSelect(SelectStatement select, SqlAgentToolType provider)
    {
        foreach (var cte in select.Ctes)
            Validate(cte.Query, provider);
        if (select.From is DerivedTableSource derived)
            Validate(derived.Query, provider);

        foreach (var join in select.Joins)
        {
            ValidateJoinShape(join, provider);
            if (join.Source is DerivedTableSource joinedDerived)
                Validate(joinedDerived.Query, provider);
            if (join.Predicate is not null)
                Visit(join.Predicate, ClauseContext.Predicate, insideSetFunction: false, withinWindow: false, provider);
        }

        foreach (var item in select.Select)
            Visit(item.Expression, ClauseContext.Projection, insideSetFunction: false, withinWindow: false, provider);
        if (select.Where is not null)
            Visit(select.Where, ClauseContext.Predicate, insideSetFunction: false, withinWindow: false, provider);
        foreach (var expression in select.GroupBy)
            Visit(expression, ClauseContext.GroupBy, insideSetFunction: false, withinWindow: false, provider);
        if (select.Having is not null)
            Visit(select.Having, ClauseContext.Having, insideSetFunction: false, withinWindow: false, provider);

        ValidateOrderingSemantics(
            select.OrderBy,
            select,
            provider,
            setOperationScope: false);
        foreach (var item in select.OrderBy)
            Visit(item.Expression, ClauseContext.OrderBy, insideSetFunction: false, withinWindow: false, provider);
    }

    private static void ValidateOrderingSemantics(
        IEnumerable<OrderByItem> orderBy,
        SelectStatement projection,
        SqlAgentToolType provider,
        bool setOperationScope)
    {
        var width = ProjectionWidth(projection);
        var outputNames = setOperationScope ? ProjectionOutputNames(projection) : null;

        foreach (var item in orderBy)
        {
            if (TryOrderByOrdinal(item.Expression, out var position))
            {
                if (position <= 0)
                {
                    throw new SqlCompilationException(
                        $"ORDER BY output position must be positive; received {position}.");
                }
                if (width is not null && position > width.Value)
                {
                    throw new SqlCompilationException(
                        $"ORDER BY output position {position} exceeds projection width {width.Value}.");
                }
                continue;
            }

            if (setOperationScope)
                ValidateSetOrderingReferences(item.Expression, outputNames, provider);
        }
    }

    private static IReadOnlyList<IdentifierPart>? ProjectionOutputNames(SelectStatement select)
    {
        if (select.Select.Any(item => IsProjectionWildcard(item.Expression)))
            return null;

        var result = new List<IdentifierPart>(select.Select.Length);
        foreach (var item in select.Select)
        {
            if (item.Alias is not null)
            {
                result.Add(item.Alias);
                continue;
            }

            var identifier = item.Expression switch
            {
                BoundColumnExpr column => column.Name,
                ColumnExpr column => column.Name,
                _ => null
            };
            if (identifier is null || identifier.Parts.IsDefaultOrEmpty || IsWildcard(identifier))
                return null;
            result.Add(identifier.Parts[^1]);
        }
        return result;
    }

    private static void ValidateSetOrderingReferences(
        SqlExpr expression,
        IReadOnlyList<IdentifierPart>? outputNames,
        SqlAgentToolType provider)
    {
        switch (expression)
        {
            case LiteralExpr:
            case IntervalExpr:
                return;
            case ColumnExpr column:
                ValidateSetOutputReference(column.Name, outputNames, provider);
                return;
            case BoundColumnExpr column:
                ValidateSetOutputReference(column.Name, outputNames, provider);
                return;
            case UnaryExpr unary:
                ValidateSetOrderingReferences(unary.Operand, outputNames, provider);
                return;
            case BinaryExpr binary:
                ValidateSetOrderingReferences(binary.Left, outputNames, provider);
                ValidateSetOrderingReferences(binary.Right, outputNames, provider);
                return;
            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                    ValidateSetOrderingReferences(argument, outputNames, provider);
                return;
            case FilterExpr filter:
                ValidateSetOrderingReferences(filter.Expression, outputNames, provider);
                ValidateSetOrderingReferences(filter.Predicate, outputNames, provider);
                return;
            case WindowedExpr windowed:
                ValidateSetOrderingReferences(windowed.Expression, outputNames, provider);
                foreach (var partition in windowed.Window.PartitionBy)
                    ValidateSetOrderingReferences(partition, outputNames, provider);
                foreach (var item in windowed.Window.OrderBy)
                    ValidateSetOrderingReferences(item.Expression, outputNames, provider);
                return;
            case CastExpr cast:
                ValidateSetOrderingReferences(cast.Expression, outputNames, provider);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    ValidateSetOrderingReferences(branch.Condition, outputNames, provider);
                    ValidateSetOrderingReferences(branch.Value, outputNames, provider);
                }
                if (@case.ElseExpression is not null)
                    ValidateSetOrderingReferences(@case.ElseExpression, outputNames, provider);
                return;
            case InExpr @in:
                ValidateSetOrderingReferences(@in.Value, outputNames, provider);
                foreach (var value in @in.Items)
                    ValidateSetOrderingReferences(value, outputNames, provider);
                return;
            case BetweenExpr between:
                ValidateSetOrderingReferences(between.Value, outputNames, provider);
                ValidateSetOrderingReferences(between.Lower, outputNames, provider);
                ValidateSetOrderingReferences(between.Upper, outputNames, provider);
                return;
            case IsNullExpr isNull:
                ValidateSetOrderingReferences(isNull.Value, outputNames, provider);
                return;
            case SubqueryExpr:
            case ExistsExpr:
                // A subquery has its own scope. Its internal identifiers are validated when the
                // subquery itself is visited and must not be mistaken for set-result references.
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported set-operation ORDER BY expression '{expression.GetType().Name}'.");
        }
    }

    private static void ValidateSetOutputReference(
        SqlIdentifier identifier,
        IReadOnlyList<IdentifierPart>? outputNames,
        SqlAgentToolType provider)
    {
        if (identifier.Parts.Length != 1 || IsWildcard(identifier))
        {
            throw new SqlCompilationException(
                $"Set-operation ORDER BY can reference combined output columns only; " +
                $"branch-qualified reference '{IdentifierText(identifier)}' is not valid after combination.");
        }

        if (outputNames is null) return;

        var reference = identifier.Parts[0];
        var matches = outputNames
            .Count(candidate => IdentifiersEquivalent(candidate, reference, provider));
        if (matches == 0)
        {
            throw new SqlCompilationException(
                $"Set-operation ORDER BY reference '{reference.Value}' is not present in the combined output projection.");
        }
        if (matches > 1)
        {
            throw new SqlCompilationException(
                $"Set-operation ORDER BY reference '{reference.Value}' is ambiguous in the combined output projection; use an output position.");
        }
    }

    private static bool IdentifiersEquivalent(
        IdentifierPart left,
        IdentifierPart right,
        SqlAgentToolType provider)
    {
        if (provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite)
            return string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

        static string NormalizeDelimitedAware(IdentifierPart part, SqlAgentToolType target) =>
            part.WasQuoted
                ? part.Value
                : target == SqlAgentToolType.Postgres
                    ? part.Value.ToLowerInvariant()
                    : part.Value.ToUpperInvariant();

        return string.Equals(
            NormalizeDelimitedAware(left, provider),
            NormalizeDelimitedAware(right, provider),
            StringComparison.Ordinal);
    }

    private static bool TryOrderByOrdinal(SqlExpr expression, out int position)
    {
        if (expression is LiteralExpr { Value: OrderByOrdinalValue ordinal })
        {
            position = ordinal.Position;
            return true;
        }
        position = default;
        return false;
    }

    private static void ValidateJoinShape(JoinSource join, SqlAgentToolType provider)
    {
        if (join.Kind == "CROSS")
        {
            if (join.Predicate is not null)
                throw new SqlCompilationException("CROSS JOIN cannot have an ON predicate.");
        }
        else if (join.Predicate is null)
        {
            throw new SqlCompilationException($"{join.Kind} JOIN requires an ON predicate.");
        }

        if (provider == SqlAgentToolType.MySQL && join.Kind == "FULL")
        {
            throw new SqlCompilationException(
                "SQL capability 'join.full' is not supported by provider MySQL for this Core plan.");
        }
    }

    private static void ValidateSetOperationWidths(QueryStatement query)
    {
        var expectedWidth = ProjectionWidth(query.Head);
        if (expectedWidth is null) return;

        foreach (var operation in query.SetOperations)
        {
            var actualWidth = ProjectionWidth(operation.Query);
            if (actualWidth is not null && actualWidth.Value != expectedWidth.Value)
            {
                throw new SqlCompilationException(
                    $"Set operation '{operation.Kind}' projection width {actualWidth.Value} " +
                    $"does not match head projection width {expectedWidth.Value}.");
            }
        }
    }

    private static int? ProjectionWidth(SqlStatement statement) => statement switch
    {
        SelectStatement select when select.Select.Any(item => IsProjectionWildcard(item.Expression)) => null,
        SelectStatement select => select.Select.Length,
        QueryStatement query => ProjectionWidth(query.Head),
        _ => null
    };

    private static bool IsProjectionWildcard(SqlExpr expression) => expression switch
    {
        ColumnExpr column => IsWildcard(column.Name),
        BoundColumnExpr column => IsWildcard(column.Name),
        _ => false
    };

    private static void Visit(
        SqlExpr expression,
        ClauseContext context,
        bool insideSetFunction,
        bool withinWindow,
        SqlAgentToolType provider)
    {
        switch (expression)
        {
            case LiteralExpr { Value: OrderByOrdinalValue ordinal }:
                if (context != ClauseContext.OrderBy)
                {
                    throw new SqlCompilationException(
                        $"ORDER BY output position {ordinal.Position} is not a scalar SQL value and cannot appear in '{ContextName(context)}'.");
                }
                return;
            case LiteralExpr literal:
                CoreProviderCapabilityRules.ValidateLiteral(literal, provider);
                return;
            case ColumnExpr:
            case BoundColumnExpr:
            case IntervalExpr:
                return;

            case UnaryExpr unary:
                Visit(unary.Operand, context, insideSetFunction, withinWindow, provider);
                return;

            case BinaryExpr binary:
                Visit(binary.Left, context, insideSetFunction, withinWindow, provider);
                Visit(binary.Right, context, insideSetFunction, withinWindow, provider);
                return;

            case FunctionCallExpr function:
                VisitFunction(function, context, insideSetFunction, withinWindow, provider);
                return;

            case FilterExpr filter:
                Visit(filter.Expression, context, insideSetFunction, withinWindow, provider);
                Visit(filter.Predicate, ClauseContext.Predicate, insideSetFunction: false, withinWindow: false, provider);
                return;

            case WindowedExpr windowed:
                CoreProviderCapabilityRules.ValidateWindow(windowed, provider);
                if (context is not (ClauseContext.Projection or ClauseContext.OrderBy))
                {
                    throw new SqlCompilationException(
                        $"Window expressions are not allowed in SQL clause '{ContextName(context)}'.");
                }
                if (insideSetFunction)
                    throw new SqlCompilationException("Window functions cannot be nested inside aggregate or window functions.");

                Visit(windowed.Expression, context, insideSetFunction: false, withinWindow: true, provider);
                foreach (var partition in windowed.Window.PartitionBy)
                    Visit(partition, ClauseContext.WindowSpecification, insideSetFunction: false, withinWindow: false, provider);
                foreach (var item in windowed.Window.OrderBy)
                    Visit(item.Expression, ClauseContext.WindowSpecification, insideSetFunction: false, withinWindow: false, provider);
                return;

            case CastExpr cast:
                Visit(cast.Expression, context, insideSetFunction, withinWindow, provider);
                return;

            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    Visit(branch.Condition, context, insideSetFunction, withinWindow, provider);
                    Visit(branch.Value, context, insideSetFunction, withinWindow, provider);
                }
                if (@case.ElseExpression is not null)
                    Visit(@case.ElseExpression, context, insideSetFunction, withinWindow, provider);
                return;

            case InExpr @in:
                Visit(@in.Value, context, insideSetFunction, withinWindow, provider);
                foreach (var item in @in.Items)
                    Visit(item, context, insideSetFunction, withinWindow, provider);
                return;

            case BetweenExpr between:
                Visit(between.Value, context, insideSetFunction, withinWindow, provider);
                Visit(between.Lower, context, insideSetFunction, withinWindow, provider);
                Visit(between.Upper, context, insideSetFunction, withinWindow, provider);
                return;

            case IsNullExpr isNull:
                Visit(isNull.Value, context, insideSetFunction, withinWindow, provider);
                return;

            case SubqueryExpr subquery:
                Validate(subquery.Query, provider);
                return;

            case ExistsExpr exists:
                Validate(exists.Query, provider);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during Core semantic validation: {expression.GetType().Name}");
        }
    }

    private static void VisitFunction(
        FunctionCallExpr function,
        ClauseContext context,
        bool insideSetFunction,
        bool withinWindow,
        SqlAgentToolType provider)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        CoreProviderCapabilityRules.ValidateFunction(function, provider);
        var isAggregate = AggregateFunctions.Contains(name);
        var isWindowFunction = WindowFunctions.Contains(name);

        if (isAggregate)
        {
            if (!withinWindow && context is not (
                    ClauseContext.Projection or ClauseContext.Having or ClauseContext.OrderBy))
            {
                throw new SqlCompilationException(
                    $"Aggregate function '{name}' is not allowed in SQL clause '{ContextName(context)}'.");
            }
            if (withinWindow && context is not (ClauseContext.Projection or ClauseContext.OrderBy))
            {
                throw new SqlCompilationException(
                    $"Windowed aggregate function '{name}' is not allowed in SQL clause '{ContextName(context)}'.");
            }
            if (insideSetFunction)
                throw new SqlCompilationException($"Aggregate function '{name}' cannot be nested inside another aggregate or window function.");
        }

        if (isWindowFunction)
        {
            if (context is not (ClauseContext.Projection or ClauseContext.OrderBy))
            {
                throw new SqlCompilationException(
                    $"Window function '{name}' is not allowed in SQL clause '{ContextName(context)}'.");
            }
            if (insideSetFunction)
                throw new SqlCompilationException($"Window function '{name}' cannot be nested inside another aggregate or window function.");
        }

        var nextInsideSetFunction = insideSetFunction || isAggregate || isWindowFunction;
        foreach (var argument in function.Arguments)
            Visit(argument, context, nextInsideSetFunction, withinWindow: false, provider);
    }

    private static bool IsWildcard(SqlIdentifier identifier) =>
        !identifier.Parts.IsDefaultOrEmpty
        && identifier.Parts[^1].Value == "*"
        && !identifier.Parts[^1].WasQuoted;

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private static string ContextName(ClauseContext context) => context switch
    {
        ClauseContext.Projection => "SELECT",
        ClauseContext.Predicate => "WHERE/ON/FILTER",
        ClauseContext.GroupBy => "GROUP BY",
        ClauseContext.Having => "HAVING",
        ClauseContext.OrderBy => "ORDER BY",
        ClauseContext.WindowSpecification => "window specification",
        ClauseContext.Assignment => "UPDATE SET",
        _ => context.ToString()
    };

    private enum ClauseContext
    {
        Projection,
        Predicate,
        GroupBy,
        Having,
        OrderBy,
        WindowSpecification,
        Assignment
    }
}
