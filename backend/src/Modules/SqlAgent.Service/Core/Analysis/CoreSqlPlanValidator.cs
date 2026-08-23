using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Analysis;

public sealed class CoreSqlPlanValidator : ISqlPlanValidator
{
    public ValidatedSqlPlan Validate(CanonicalStatement statement, SqlPlanValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.PolicyVersion);
        ValidateTableAccess(statement.Facts, context.AllowedTables);
        ValidateCapabilities(statement.Statement, statement.TargetProvider);
        return new ValidatedSqlPlan(statement.Statement, statement.Facts, statement.SourceDialect, statement.TargetProvider, context.PolicyVersion);
    }

    private static void ValidateTableAccess(QueryFacts facts, IReadOnlySet<string>? allowedTables)
    {
        if (allowedTables is null || allowedTables.Count == 0) return;
        var allowed = allowedTables is HashSet<string> hash && hash.Comparer.Equals(StringComparer.OrdinalIgnoreCase)
            ? hash : allowedTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violations = facts.ReferencedTables.Where(table => !allowed.Contains(table)).OrderBy(table => table, StringComparer.OrdinalIgnoreCase).ToArray();
        if (violations.Length > 0)
            throw new UnauthorizedAccessException($"SQL plan is not authorized to access table(s): {string.Join(", ", violations)}");
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
                foreach (var operation in query.SetOperations) ValidateCapabilities(operation.Query, provider);
                ValidateOrdering(query.OrderBy, provider);
                foreach (var item in query.OrderBy) ValidateExpression(item.Expression, provider, ExpressionContext.OrderBy);
                return;
            case UpdateStatement update:
                if (update.Assignments.IsDefaultOrEmpty)
                    throw new SqlCompilationException("UPDATE requires at least one assignment.");
                foreach (var assignment in update.Assignments)
                    ValidateExpression(assignment.Value, provider, ExpressionContext.Assignment);
                if (update.Predicate is not null) ValidateExpression(update.Predicate, provider, ExpressionContext.Predicate);
                return;
            case DeleteStatement delete:
                if (delete.Predicate is not null) ValidateExpression(delete.Predicate, provider, ExpressionContext.Predicate);
                return;
            default:
                throw new SqlCompilationException($"Unsupported statement during capability validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateSelect(SelectStatement select, SqlAgentToolType provider)
    {
        foreach (var cte in select.Ctes)
        {
            if (!cte.ColumnAliases.IsDefaultOrEmpty)
                throw CapabilityError(provider, "query.cte_column_aliases");
            ValidateCapabilities(cte.Query, provider);
        }
        if (select.From is not null) ValidateSource(select.From, provider);
        foreach (var join in select.Joins)
        {
            ValidateJoinKind(join.Kind);
            ValidateSource(join.Source, provider);
            if (join.Predicate is not null) ValidateExpression(join.Predicate, provider, ExpressionContext.Predicate);
        }
        foreach (var item in select.Select)
        {
            ValidateExpression(item.Expression, provider, ExpressionContext.Projection);
            if (provider is SqlAgentToolType.Oracle or SqlAgentToolType.MsSqlServer && IsBooleanProjection(item.Expression))
                throw CapabilityError(provider, "expression.boolean_select");
        }
        if (select.Where is not null) ValidateExpression(select.Where, provider, ExpressionContext.Predicate);
        foreach (var expression in select.GroupBy) ValidateExpression(expression, provider, ExpressionContext.GroupBy);
        if (select.Having is not null) ValidateExpression(select.Having, provider, ExpressionContext.Predicate);
        ValidateOrdering(select.OrderBy, provider);
        foreach (var item in select.OrderBy) ValidateExpression(item.Expression, provider, ExpressionContext.OrderBy);
    }

    private static void ValidateSource(TableSource source, SqlAgentToolType provider)
    {
        switch (source)
        {
            case NamedTableSource: return;
            case DerivedTableSource derived: ValidateCapabilities(derived.Query, provider); return;
            default: throw new SqlCompilationException($"Unsupported table source during capability validation: {source.GetType().Name}");
        }
    }

    private static void ValidateOrdering(IEnumerable<OrderByItem> orderBy, SqlAgentToolType provider)
    {
        if (provider is not (SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer)) return;
        if (orderBy.Any(item => item.NullOrdering != NullOrderingKind.Default)) throw CapabilityError(provider, "ordering.nulls");
    }

    private static void ValidateExpression(SqlExpr expression, SqlAgentToolType provider, ExpressionContext context)
    {
        switch (expression)
        {
            case LiteralExpr:
            case ColumnExpr:
            case BoundColumnExpr: return;
            case IntervalExpr:
                if (provider != SqlAgentToolType.Postgres) throw CapabilityError(provider, "expression.interval");
                return;
            case UnaryExpr unary: ValidateExpression(unary.Operand, provider, context); return;
            case BinaryExpr binary:
                ValidateExpression(binary.Left, provider, context);
                ValidateExpression(binary.Right, provider, context);
                return;
            case FunctionCallExpr function:
                foreach (var argument in function.Arguments) ValidateExpression(argument, provider, ExpressionContext.FunctionArgument);
                return;
            case FilterExpr filter:
                if (provider is not (SqlAgentToolType.Postgres or SqlAgentToolType.Sqlite or SqlAgentToolType.Firebird))
                    throw CapabilityError(provider, "expression.filter");
                ValidateExpression(filter.Expression, provider, context);
                ValidateExpression(filter.Predicate, provider, ExpressionContext.Predicate);
                return;
            case WindowedExpr windowed:
                ValidateExpression(windowed.Expression, provider, context);
                foreach (var partition in windowed.Window.PartitionBy) ValidateExpression(partition, provider, ExpressionContext.GroupBy);
                ValidateOrdering(windowed.Window.OrderBy, provider);
                foreach (var item in windowed.Window.OrderBy) ValidateExpression(item.Expression, provider, ExpressionContext.OrderBy);
                ValidateWindowFrame(windowed.Window.Frame);
                return;
            case CastExpr cast: ValidateExpression(cast.Expression, provider, context); return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    ValidateExpression(branch.Condition, provider, ExpressionContext.Predicate);
                    ValidateExpression(branch.Value, provider, context);
                }
                if (@case.ElseExpression is not null) ValidateExpression(@case.ElseExpression, provider, context);
                return;
            case InExpr @in:
                ValidateExpression(@in.Value, provider, context);
                foreach (var item in @in.Items) ValidateExpression(item, provider, context);
                return;
            case BetweenExpr between:
                ValidateExpression(between.Value, provider, context);
                ValidateExpression(between.Lower, provider, context);
                ValidateExpression(between.Upper, provider, context);
                return;
            case IsNullExpr isNull: ValidateExpression(isNull.Value, provider, context); return;
            case SubqueryExpr subquery: ValidateCapabilities(subquery.Query, provider); return;
            case ExistsExpr exists: ValidateCapabilities(exists.Query, provider); return;
            default: throw new SqlCompilationException($"Unsupported expression during capability validation: {expression.GetType().Name}");
        }
    }

    private static void ValidateWindowFrame(WindowFrame? frame)
    {
        if (frame is null) return;
        ValidateWindowBound(frame.Start);
        if (frame.End is not null) ValidateWindowBound(frame.End);
    }

    private static void ValidateWindowBound(WindowFrameBoundCore bound)
    {
        var requiresOffset = bound.Kind is WindowFrameBoundKindCore.Preceding or WindowFrameBoundKindCore.Following;
        if (requiresOffset && bound.Offset is null or < 0) throw new SqlCompilationException($"Window frame bound '{bound.Kind}' requires a non-negative offset.");
        if (!requiresOffset && bound.Offset is not null) throw new SqlCompilationException($"Window frame bound '{bound.Kind}' must not carry an offset.");
    }

    private static void ValidateJoinKind(string kind)
    {
        if (kind is "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS") return;
        throw new SqlCompilationException($"Unsupported JOIN kind '{kind}'.");
    }

    private static bool IsBooleanProjection(SqlExpr expression) => expression switch
    {
        IsNullExpr or InExpr or BetweenExpr or ExistsExpr => true,
        UnaryExpr unary when unary.Operator == "NOT" => true,
        BinaryExpr binary when binary.Operator is "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=" or "LIKE" or "ILIKE" or "AND" or "OR" or "IN" or "NOT IN" => true,
        _ => false
    };

    private static SqlCompilationException CapabilityError(SqlAgentToolType provider, string capability) =>
        new($"SQL capability '{capability}' is not supported by provider {provider} for this Core plan.");

    private enum ExpressionContext { Projection, Predicate, GroupBy, OrderBy, FunctionArgument, Assignment }
}
