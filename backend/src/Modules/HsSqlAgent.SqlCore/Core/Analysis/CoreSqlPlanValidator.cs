namespace HsSqlAgent.SqlCore.Core.Analysis;

public sealed class CoreSqlPlanValidator : ISqlPlanValidator
{
    private static readonly IReadOnlyDictionary<string, FunctionShape> FunctionShapes =
        new Dictionary<string, FunctionShape>(StringComparer.OrdinalIgnoreCase)
        {
            ["ABS"] = FunctionShape.Scalar(1),
            ["ROUND"] = FunctionShape.Scalar(1, 2),
            ["LOWER"] = FunctionShape.Scalar(1),
            ["UPPER"] = FunctionShape.Scalar(1),
            ["TRIM"] = FunctionShape.Scalar(1),
            ["LTRIM"] = FunctionShape.Scalar(1),
            ["RTRIM"] = FunctionShape.Scalar(1),
            ["NULLIF"] = FunctionShape.Scalar(2),

            ["AVG"] = FunctionShape.Aggregate(1),
            ["COUNT"] = FunctionShape.Aggregate(1),
            ["MAX"] = FunctionShape.Aggregate(1),
            ["MIN"] = FunctionShape.Aggregate(1),
            ["SUM"] = FunctionShape.Aggregate(1),

            ["ROW_NUMBER"] = FunctionShape.Window(0),
            ["RANK"] = FunctionShape.Window(0),
            ["DENSE_RANK"] = FunctionShape.Window(0),
            ["PERCENT_RANK"] = FunctionShape.Window(0),
            ["CUME_DIST"] = FunctionShape.Window(0),
            ["LAG"] = FunctionShape.Window(1, 3),
            ["LEAD"] = FunctionShape.Window(1, 3),
            ["FIRST_VALUE"] = FunctionShape.Window(1),
            ["LAST_VALUE"] = FunctionShape.Window(1),
            ["NTH_VALUE"] = FunctionShape.Window(2),
            ["NTILE"] = FunctionShape.Window(1),

            ["CORE_DATE_ADD"] = FunctionShape.Scalar(3),
            ["CORE_DATE_DIFF"] = FunctionShape.Scalar(3),
            ["CORE_DATE_PART"] = FunctionShape.Scalar(2),
            ["CORE_DATE_FORMAT"] = FunctionShape.Scalar(2),
            ["CORE_DATE_PARSE"] = FunctionShape.Scalar(2),
            ["CORE_POSITION"] = FunctionShape.Scalar(2),
            ["CORE_JSON_EXTRACT"] = FunctionShape.Scalar(2),
            ["CORE_JSON_SET"] = FunctionShape.Scalar(3),
            ["CORE_REGEX_MATCH"] = FunctionShape.Scalar(2),
            ["CORE_CURRENT_DATE"] = FunctionShape.Scalar(0),
            ["CORE_CURRENT_TIME"] = FunctionShape.Scalar(0),
            ["CORE_CURRENT_TIMESTAMP"] = FunctionShape.Scalar(0),
            ["CORE_STRING_AGG"] = new FunctionShape(
                2,
                2,
                AllowDistinct: false,
                AllowFilter: true,
                AllowWindow: false,
                RequireWindow: false)
        };

    public ValidatedSqlPlan Validate(CanonicalStatement statement, SqlPlanValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.PolicyVersion);

        // CTE output aliases are represented explicitly in the parser/Core AST, while SqlKata does
        // not model the list syntax. Canonicalize them to equivalent projection aliases before the
        // semantic/capability walk so every downstream stage sees one fully lowerable shape.
        var canonicalStatement = CoreCteColumnAliasRewriter.Rewrite(statement.Statement);

        ValidateTableAccess(statement.Facts, context.AllowedTables);
        CoreSqlSemanticValidator.Validate(canonicalStatement, statement.TargetProvider);
        ValidateCapabilities(canonicalStatement, statement.TargetProvider);
        return new ValidatedSqlPlan(
            canonicalStatement,
            statement.Facts,
            statement.SourceDialect,
            statement.TargetProvider,
            context.PolicyVersion);
    }

    private static void ValidateTableAccess(QueryFacts facts, IReadOnlySet<string>? allowedTables)
    {
        if (allowedTables is null || allowedTables.Count == 0) return;
        var allowed = allowedTables is HashSet<string> hash && hash.Comparer.Equals(StringComparer.OrdinalIgnoreCase)
            ? hash
            : allowedTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violations = facts.ReferencedTables
            .Where(table => !allowed.Contains(table))
            .OrderBy(table => table, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (violations.Length > 0)
        {
            throw new UnauthorizedAccessException(
                $"SQL plan is not authorized to access table(s): {string.Join(", ", violations)}");
        }
    }

    private static void ValidateCapabilities(SqlStatement statement, SqlAgentToolType provider)
    {
        switch (statement)
        {
            case SelectStatement select:
                ValidateSelect(select, provider);
                return;
            case QueryStatement query:
                ValidateSelect(query.Head, provider);
                foreach (var operation in query.SetOperations)
                    ValidateCapabilities(operation.Query, provider);
                ValidateOrdering(query.OrderBy, provider);
                foreach (var item in query.OrderBy)
                    ValidateExpression(item.Expression, provider, ExpressionContext.OrderBy);
                return;
            case UpdateStatement update:
                if (update.Assignments.IsDefaultOrEmpty)
                    throw new SqlCompilationException("UPDATE requires at least one assignment.");
                foreach (var assignment in update.Assignments)
                    ValidateExpression(assignment.Value, provider, ExpressionContext.Assignment);
                if (update.Predicate is not null)
                    ValidateExpression(update.Predicate, provider, ExpressionContext.Predicate);
                return;
            case DeleteStatement delete:
                if (delete.Predicate is not null)
                    ValidateExpression(delete.Predicate, provider, ExpressionContext.Predicate);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported statement during capability validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateSelect(SelectStatement select, SqlAgentToolType provider)
    {
        foreach (var cte in select.Ctes)
            ValidateCapabilities(cte.Query, provider);
        if (select.From is not null)
            ValidateSource(select.From, provider);
        foreach (var join in select.Joins)
        {
            ValidateJoinKind(join.Kind);
            ValidateSource(join.Source, provider);
            if (join.Predicate is not null)
                ValidateExpression(join.Predicate, provider, ExpressionContext.Predicate);
        }
        foreach (var item in select.Select)
        {
            ValidateExpression(item.Expression, provider, ExpressionContext.Projection);
            if (provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer
                && IsBooleanProjection(item.Expression))
            {
                throw CapabilityError(provider, "expression.boolean_select");
            }
        }
        if (select.Where is not null)
            ValidateExpression(select.Where, provider, ExpressionContext.Predicate);
        foreach (var expression in select.GroupBy)
            ValidateExpression(expression, provider, ExpressionContext.GroupBy);
        if (select.Having is not null)
            ValidateExpression(select.Having, provider, ExpressionContext.Predicate);
        ValidateOrdering(select.OrderBy, provider);
        foreach (var item in select.OrderBy)
            ValidateExpression(item.Expression, provider, ExpressionContext.OrderBy);
    }

    private static void ValidateSource(TableSource source, SqlAgentToolType provider)
    {
        switch (source)
        {
            case NamedTableSource:
                return;
            case DerivedTableSource derived:
                ValidateCapabilities(derived.Query, provider);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported table source during capability validation: {source.GetType().Name}");
        }
    }

    private static void ValidateOrdering(IEnumerable<OrderByItem> orderBy, SqlAgentToolType provider)
    {
        if (provider is not (SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)) return;
        if (orderBy.Any(item => item.NullOrdering != NullOrderingKind.Default))
            throw CapabilityError(provider, "ordering.nulls");
    }

    private static void ValidateExpression(
        SqlExpr expression,
        SqlAgentToolType provider,
        ExpressionContext context,
        bool withinWindow = false)
    {
        switch (expression)
        {
            case LiteralExpr:
            case ColumnExpr:
            case BoundColumnExpr:
                return;
            case IntervalExpr:
                if (provider != SqlAgentToolType.Postgres)
                    throw CapabilityError(provider, "expression.interval");
                return;
            case UnaryExpr unary:
                ValidateExpression(unary.Operand, provider, context);
                return;
            case BinaryExpr binary:
                if (binary.Operator.Equals("ILIKE", StringComparison.OrdinalIgnoreCase)
                    && provider != SqlAgentToolType.Postgres)
                {
                    throw CapabilityError(provider, "operator.ilike");
                }
                ValidateExpression(binary.Left, provider, context);
                ValidateExpression(binary.Right, provider, context);
                return;
            case FunctionCallExpr function:
                ValidateFunction(function, provider, withinWindow);
                foreach (var argument in function.Arguments)
                    ValidateExpression(argument, provider, ExpressionContext.FunctionArgument);
                return;
            case FilterExpr filter:
                if (provider is not (
                    SqlAgentToolType.Postgres or SqlAgentToolType.Sqlite or SqlAgentToolType.Firebird))
                {
                    throw CapabilityError(provider, "expression.filter");
                }
                ValidateFilterTarget(filter.Expression);
                ValidateExpression(filter.Expression, provider, context, withinWindow);
                ValidateExpression(filter.Predicate, provider, ExpressionContext.Predicate);
                return;
            case WindowedExpr windowed:
                ValidateWindowTarget(windowed.Expression);
                ValidateExpression(windowed.Expression, provider, context, withinWindow: true);
                foreach (var partition in windowed.Window.PartitionBy)
                    ValidateExpression(partition, provider, ExpressionContext.GroupBy);
                ValidateOrdering(windowed.Window.OrderBy, provider);
                foreach (var item in windowed.Window.OrderBy)
                    ValidateExpression(item.Expression, provider, ExpressionContext.OrderBy);
                ValidateWindowFrame(windowed.Window.Frame);
                return;
            case CastExpr cast:
                ValidateExpression(cast.Expression, provider, context);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    ValidateExpression(branch.Condition, provider, ExpressionContext.Predicate);
                    ValidateExpression(branch.Value, provider, context);
                }
                if (@case.ElseExpression is not null)
                    ValidateExpression(@case.ElseExpression, provider, context);
                return;
            case InExpr @in:
                ValidateExpression(@in.Value, provider, context);
                foreach (var item in @in.Items)
                    ValidateExpression(item, provider, context);
                return;
            case BetweenExpr between:
                ValidateExpression(between.Value, provider, context);
                ValidateExpression(between.Lower, provider, context);
                ValidateExpression(between.Upper, provider, context);
                return;
            case IsNullExpr isNull:
                ValidateExpression(isNull.Value, provider, context);
                return;
            case SubqueryExpr subquery:
                ValidateCapabilities(subquery.Query, provider);
                return;
            case ExistsExpr exists:
                ValidateCapabilities(exists.Query, provider);
                return;
            default:
                throw new SqlCompilationException(
                    $"Unsupported expression during capability validation: {expression.GetType().Name}");
        }
    }

    private static void ValidateFunction(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        bool withinWindow)
    {
        var name = IdentifierText(function.Name).ToUpperInvariant();
        if (FunctionShapes.TryGetValue(name, out var shape))
        {
            if (function.Arguments.Length < shape.MinArguments
                || function.Arguments.Length > shape.MaxArguments)
            {
                var expected = shape.MinArguments == shape.MaxArguments
                    ? shape.MinArguments.ToString()
                    : $"{shape.MinArguments}-{shape.MaxArguments}";
                throw new SqlCompilationException(
                    $"Function '{name}' requires {expected} argument(s); received {function.Arguments.Length}.");
            }

            if (function.IsDistinct && !shape.AllowDistinct)
            {
                throw new SqlCompilationException(
                    $"Function '{name}' does not support DISTINCT in the Core pipeline.");
            }

            if (shape.RequireWindow && !withinWindow)
                throw new SqlCompilationException($"Function '{name}' requires an OVER clause.");

            if (name == "COUNT" && function.IsDistinct && IsWildcard(function.Arguments[0]))
                throw new SqlCompilationException("COUNT(DISTINCT *) is not a valid Core aggregate shape.");

            if (name == "CORE_STRING_AGG"
                && function.Arguments[1] is not LiteralExpr { Value: string })
            {
                throw CapabilityError(provider, "aggregate.string.dynamic_separator");
            }
        }
        else if (function.IsDistinct)
        {
            // Registry-backed ordinary functions have a validated source/target name and arity, but
            // the registry does not model DISTINCT semantics. Never guess that modifier support.
            throw new SqlCompilationException(
                $"Function '{name}' has no Core DISTINCT capability declaration.");
        }

        if (name == "CORE_CURRENT_TIME" && provider == SqlAgentToolType.Oracle)
            throw CapabilityError(provider, "function.current_time");
    }

    private static void ValidateFilterTarget(SqlExpr expression)
    {
        if (expression is not FunctionCallExpr function)
            throw new SqlCompilationException("FILTER must modify a directly modeled aggregate function.");

        var name = IdentifierText(function.Name).ToUpperInvariant();
        if (!FunctionShapes.TryGetValue(name, out var shape) || !shape.AllowFilter)
        {
            throw new SqlCompilationException(
                $"Function '{name}' does not support FILTER in the Core pipeline.");
        }
    }

    private static void ValidateWindowTarget(SqlExpr expression)
    {
        var function = expression switch
        {
            FunctionCallExpr direct => direct,
            FilterExpr { Expression: FunctionCallExpr filtered } => filtered,
            _ => null
        };
        if (function is null)
            throw new SqlCompilationException("OVER must modify a directly modeled aggregate or window function.");

        var name = IdentifierText(function.Name).ToUpperInvariant();
        if (!FunctionShapes.TryGetValue(name, out var shape) || !shape.AllowWindow)
        {
            throw new SqlCompilationException(
                $"Function '{name}' does not support OVER in the Core pipeline.");
        }
    }

    private static void ValidateWindowFrame(WindowFrame? frame)
    {
        if (frame is null) return;
        ValidateWindowBound(frame.Start);
        if (frame.End is null)
        {
            if (frame.Start.Kind == WindowFrameBoundKindCore.UnboundedFollowing)
                throw new SqlCompilationException("Window frame cannot start with UNBOUNDED FOLLOWING.");
            return;
        }

        ValidateWindowBound(frame.End);
        if (frame.Start.Kind == WindowFrameBoundKindCore.UnboundedFollowing)
            throw new SqlCompilationException("Window frame cannot start with UNBOUNDED FOLLOWING.");
        if (frame.End.Kind == WindowFrameBoundKindCore.UnboundedPreceding)
            throw new SqlCompilationException("Window frame cannot end with UNBOUNDED PRECEDING.");
        if (WindowBoundPosition(frame.Start) > WindowBoundPosition(frame.End))
        {
            throw new SqlCompilationException(
                "Window frame start must not be logically after its end bound.");
        }
    }

    private static void ValidateWindowBound(WindowFrameBoundCore bound)
    {
        var requiresOffset = bound.Kind is
            WindowFrameBoundKindCore.Preceding or WindowFrameBoundKindCore.Following;
        if (requiresOffset && bound.Offset is null or < 0)
        {
            throw new SqlCompilationException(
                $"Window frame bound '{bound.Kind}' requires a non-negative offset.");
        }
        if (!requiresOffset && bound.Offset is not null)
        {
            throw new SqlCompilationException(
                $"Window frame bound '{bound.Kind}' must not carry an offset.");
        }
    }

    private static long WindowBoundPosition(WindowFrameBoundCore bound) => bound.Kind switch
    {
        WindowFrameBoundKindCore.UnboundedPreceding => long.MinValue,
        WindowFrameBoundKindCore.Preceding => -(long)bound.Offset!.Value,
        WindowFrameBoundKindCore.CurrentRow => 0L,
        WindowFrameBoundKindCore.Following => bound.Offset!.Value,
        WindowFrameBoundKindCore.UnboundedFollowing => long.MaxValue,
        _ => throw new SqlCompilationException($"Unsupported window frame bound '{bound.Kind}'.")
    };

    private static void ValidateJoinKind(string kind)
    {
        if (kind is "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS") return;
        throw new SqlCompilationException($"Unsupported JOIN kind '{kind}'.");
    }

    private static bool IsWildcard(SqlExpr expression) => expression switch
    {
        ColumnExpr { Name.Parts.Length: 1 } column =>
            column.Name.Parts[0].Value == "*" && !column.Name.Parts[0].WasQuoted,
        BoundColumnExpr { Name.Parts.Length: 1 } column =>
            column.Name.Parts[0].Value == "*" && !column.Name.Parts[0].WasQuoted,
        _ => false
    };

    private static bool IsBooleanProjection(SqlExpr expression) => expression switch
    {
        IsNullExpr or InExpr or BetweenExpr or ExistsExpr => true,
        UnaryExpr unary when unary.Operator == "NOT" => true,
        BinaryExpr binary when binary.Operator is
            "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" or
            "LIKE" or "ILIKE" or "AND" or "OR" or "IN" or "NOT IN" => true,
        _ => false
    };

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private static SqlCompilationException CapabilityError(
        SqlAgentToolType provider,
        string capability) =>
        new($"SQL capability '{capability}' is not supported by provider {provider} for this Core plan.");

    private enum ExpressionContext
    {
        Projection,
        Predicate,
        GroupBy,
        OrderBy,
        FunctionArgument,
        Assignment
    }

    private sealed record FunctionShape(
        int MinArguments,
        int MaxArguments,
        bool AllowDistinct,
        bool AllowFilter,
        bool AllowWindow,
        bool RequireWindow)
    {
        public static FunctionShape Scalar(int arguments) => Scalar(arguments, arguments);

        public static FunctionShape Scalar(int minArguments, int maxArguments) =>
            new(minArguments, maxArguments, false, false, false, false);

        public static FunctionShape Aggregate(int arguments) =>
            new(arguments, arguments, true, true, true, false);

        public static FunctionShape Window(int arguments) => Window(arguments, arguments);

        public static FunctionShape Window(int minArguments, int maxArguments) =>
            new(minArguments, maxArguments, false, false, true, true);
    }
}
