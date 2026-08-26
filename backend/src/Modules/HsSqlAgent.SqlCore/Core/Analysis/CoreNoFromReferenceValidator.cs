namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Guards the semantic boundary between a Core SELECT with no table source and provider lowerers
/// that may introduce a physical single-row dummy source (Oracle DUAL / Firebird RDB$DATABASE).
/// A dummy source represents the implicit singleton row only; it must never become an accidental
/// schema that resolves user-written column names or wildcard projections.
/// </summary>
internal static class CoreNoFromReferenceValidator
{
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
                foreach (var item in query.OrderBy)
                    VisitNestedSubqueries(item.Expression, provider);
                return;
            case InsertStatement insert:
                switch (insert.Source)
                {
                    case InsertQuerySource querySource:
                        Validate(querySource.Query, provider);
                        break;
                    case InsertValuesSource values:
                        foreach (var row in values.Rows)
                        foreach (var value in row)
                            VisitNestedSubqueries(value, provider);
                        break;
                }
                return;
            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                    VisitNestedSubqueries(assignment.Value, provider);
                if (update.Predicate is not null)
                    VisitNestedSubqueries(update.Predicate, provider);
                return;
            case DeleteStatement delete:
                if (delete.Predicate is not null)
                    VisitNestedSubqueries(delete.Predicate, provider);
                return;
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
            if (join.Source is DerivedTableSource joinedDerived)
                Validate(joinedDerived.Query, provider);
            if (join.Predicate is not null)
                VisitNestedSubqueries(join.Predicate, provider);
        }

        if (select.From is not null)
        {
            foreach (var item in select.Select)
                VisitNestedSubqueries(item.Expression, provider);
            if (select.Where is not null)
                VisitNestedSubqueries(select.Where, provider);
            foreach (var expression in select.GroupBy)
                VisitNestedSubqueries(expression, provider);
            if (select.Having is not null)
                VisitNestedSubqueries(select.Having, provider);
            foreach (var item in select.OrderBy)
                VisitNestedSubqueries(item.Expression, provider);
            return;
        }

        if (!select.Joins.IsDefaultOrEmpty)
        {
            throw new SqlCompilationException(
                "A Core SELECT cannot contain JOIN sources without a primary FROM source.");
        }

        foreach (var item in select.Select)
            ValidateNoFromExpression(item.Expression, provider, allowCountWildcard: false);
        if (select.Where is not null)
            ValidateNoFromExpression(select.Where, provider, allowCountWildcard: false);
        foreach (var expression in select.GroupBy)
            ValidateNoFromExpression(expression, provider, allowCountWildcard: false);
        if (select.Having is not null)
            ValidateNoFromExpression(select.Having, provider, allowCountWildcard: false);

        foreach (var item in select.OrderBy)
        {
            if (IsProjectionAliasReference(item.Expression, select, provider))
                continue;
            ValidateNoFromExpression(item.Expression, provider, allowCountWildcard: false);
        }
    }

    private static void ValidateNoFromExpression(
        SqlExpr expression,
        SqlAgentToolType provider,
        bool allowCountWildcard)
    {
        switch (expression)
        {
            case LiteralExpr:
            case IntervalExpr:
                return;
            case ColumnExpr column:
                ValidateNoFromColumn(column.Name, allowCountWildcard);
                return;
            case BoundColumnExpr column:
                // A no-FROM subquery may legally correlate to an outer source. The binder records
                // that distinction explicitly; only truly unbound scalar references are invalid.
                // Wildcard projection remains non-portable even when an outer source exists, while
                // COUNT(*) retains its well-defined singleton-row aggregate semantics.
                if (!IsWildcard(column.Name) && column.Source is not null)
                    return;
                ValidateNoFromColumn(column.Name, allowCountWildcard);
                return;
            case UnaryExpr unary:
                ValidateNoFromExpression(unary.Operand, provider, allowCountWildcard: false);
                return;
            case BinaryExpr binary:
                ValidateNoFromExpression(binary.Left, provider, allowCountWildcard: false);
                ValidateNoFromExpression(binary.Right, provider, allowCountWildcard: false);
                return;
            case FunctionCallExpr function:
            {
                var isCountStar = IdentifierText(function.Name).Equals("COUNT", StringComparison.OrdinalIgnoreCase)
                    && function.Arguments.Length == 1
                    && IsUnqualifiedWildcard(function.Arguments[0]);
                for (var i = 0; i < function.Arguments.Length; i++)
                {
                    ValidateNoFromExpression(
                        function.Arguments[i],
                        provider,
                        allowCountWildcard: isCountStar && i == 0);
                }
                return;
            }
            case FilterExpr filter:
                ValidateNoFromExpression(filter.Expression, provider, allowCountWildcard: false);
                ValidateNoFromExpression(filter.Predicate, provider, allowCountWildcard: false);
                return;
            case WindowedExpr windowed:
                ValidateNoFromExpression(windowed.Expression, provider, allowCountWildcard: false);
                foreach (var partition in windowed.Window.PartitionBy)
                    ValidateNoFromExpression(partition, provider, allowCountWildcard: false);
                foreach (var item in windowed.Window.OrderBy)
                    ValidateNoFromExpression(item.Expression, provider, allowCountWildcard: false);
                return;
            case CastExpr cast:
                ValidateNoFromExpression(cast.Expression, provider, allowCountWildcard: false);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    ValidateNoFromExpression(branch.Condition, provider, allowCountWildcard: false);
                    ValidateNoFromExpression(branch.Value, provider, allowCountWildcard: false);
                }
                if (@case.ElseExpression is not null)
                    ValidateNoFromExpression(@case.ElseExpression, provider, allowCountWildcard: false);
                return;
            case InExpr @in:
                ValidateNoFromExpression(@in.Value, provider, allowCountWildcard: false);
                foreach (var item in @in.Items)
                    ValidateNoFromExpression(item, provider, allowCountWildcard: false);
                return;
            case BetweenExpr between:
                ValidateNoFromExpression(between.Value, provider, allowCountWildcard: false);
                ValidateNoFromExpression(between.Lower, provider, allowCountWildcard: false);
                ValidateNoFromExpression(between.Upper, provider, allowCountWildcard: false);
                return;
            case IsNullExpr isNull:
                ValidateNoFromExpression(isNull.Value, provider, allowCountWildcard: false);
                return;
            case SubqueryExpr subquery:
                Validate(subquery.Query, provider);
                return;
            case ExistsExpr exists:
                Validate(exists.Query, provider);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during no-FROM reference validation: {expression.GetType().Name}");
        }
    }

    private static void VisitNestedSubqueries(SqlExpr expression, SqlAgentToolType provider)
    {
        switch (expression)
        {
            case LiteralExpr:
            case IntervalExpr:
            case ColumnExpr:
            case BoundColumnExpr:
                return;
            case UnaryExpr unary:
                VisitNestedSubqueries(unary.Operand, provider);
                return;
            case BinaryExpr binary:
                VisitNestedSubqueries(binary.Left, provider);
                VisitNestedSubqueries(binary.Right, provider);
                return;
            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                    VisitNestedSubqueries(argument, provider);
                return;
            case FilterExpr filter:
                VisitNestedSubqueries(filter.Expression, provider);
                VisitNestedSubqueries(filter.Predicate, provider);
                return;
            case WindowedExpr windowed:
                VisitNestedSubqueries(windowed.Expression, provider);
                foreach (var partition in windowed.Window.PartitionBy)
                    VisitNestedSubqueries(partition, provider);
                foreach (var item in windowed.Window.OrderBy)
                    VisitNestedSubqueries(item.Expression, provider);
                return;
            case CastExpr cast:
                VisitNestedSubqueries(cast.Expression, provider);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    VisitNestedSubqueries(branch.Condition, provider);
                    VisitNestedSubqueries(branch.Value, provider);
                }
                if (@case.ElseExpression is not null)
                    VisitNestedSubqueries(@case.ElseExpression, provider);
                return;
            case InExpr @in:
                VisitNestedSubqueries(@in.Value, provider);
                foreach (var item in @in.Items)
                    VisitNestedSubqueries(item, provider);
                return;
            case BetweenExpr between:
                VisitNestedSubqueries(between.Value, provider);
                VisitNestedSubqueries(between.Lower, provider);
                VisitNestedSubqueries(between.Upper, provider);
                return;
            case IsNullExpr isNull:
                VisitNestedSubqueries(isNull.Value, provider);
                return;
            case SubqueryExpr subquery:
                Validate(subquery.Query, provider);
                return;
            case ExistsExpr exists:
                Validate(exists.Query, provider);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during nested no-FROM validation: {expression.GetType().Name}");
        }
    }

    private static void ValidateNoFromColumn(SqlIdentifier identifier, bool allowCountWildcard)
    {
        if (allowCountWildcard && IsUnqualifiedWildcard(identifier))
            return;

        throw new SqlCompilationException(
            $"Column reference '{IdentifierText(identifier)}' requires a FROM source in the portable Core query model.");
    }

    private static bool IsProjectionAliasReference(
        SqlExpr expression,
        SelectStatement select,
        SqlAgentToolType provider)
    {
        var identifier = expression switch
        {
            ColumnExpr column => column.Name,
            BoundColumnExpr column => column.Name,
            _ => null
        };
        if (identifier is null || identifier.Parts.Length != 1 || IsWildcard(identifier))
            return false;

        var reference = identifier.Parts[0];
        var matches = select.Select
            .Where(item => item.Alias is not null)
            .Count(item => IdentifiersEquivalent(item.Alias!, reference, provider));
        if (matches > 1)
        {
            throw new SqlCompilationException(
                $"ORDER BY projection alias '{reference.Value}' is ambiguous in a no-FROM query.");
        }
        return matches == 1;
    }

    private static bool IdentifiersEquivalent(
        IdentifierPart left,
        IdentifierPart right,
        SqlAgentToolType provider)
    {
        if (provider is SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite)
            return string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

        static string Normalize(IdentifierPart part, SqlAgentToolType target) => part.WasQuoted
            ? part.Value
            : target == SqlAgentToolType.Postgres
                ? part.Value.ToLowerInvariant()
                : part.Value.ToUpperInvariant();

        return string.Equals(
            Normalize(left, provider),
            Normalize(right, provider),
            StringComparison.Ordinal);
    }

    private static bool IsUnqualifiedWildcard(SqlExpr expression) => expression switch
    {
        ColumnExpr column => IsUnqualifiedWildcard(column.Name),
        BoundColumnExpr column => IsUnqualifiedWildcard(column.Name),
        _ => false
    };

    private static bool IsUnqualifiedWildcard(SqlIdentifier identifier) =>
        identifier.Parts.Length == 1
        && identifier.Parts[0].Value == "*"
        && !identifier.Parts[0].WasQuoted;

    private static bool IsWildcard(SqlIdentifier identifier) =>
        !identifier.Parts.IsDefaultOrEmpty
        && identifier.Parts[^1].Value == "*"
        && !identifier.Parts[^1].WasQuoted;

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
