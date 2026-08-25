using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Core.Lowering;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Pipeline;

/// <summary>
/// Guards query shapes whose CTE scope cannot be preserved by the available lowering path.
/// Statement-root INSERT..SELECT CTEs have a dedicated provider-aware lowerer. Query-graph derived
/// tables and set-operation branches can use fully compiled fragments only on providers that accept
/// WITH at the beginning of a parenthesized subquery. Core provider compilers apply the same
/// query-graph rewrite to nested SELECT compilation, while scalar/EXISTS expressions render their
/// subquery as a complete compiler fragment so root WITH definitions can be retained where legal.
/// </summary>
internal static class CoreSqlKataBackendCompatibility
{
    public static void ValidateQuery(
        SqlStatement statement,
        SqlAgentToolType provider) =>
        ValidateStatement(
            statement,
            QueryPosition.Root,
            provider,
            allowNestedCteFragments: true);

    public static void ValidateInsertSelect(
        SqlStatement statement,
        SqlAgentToolType provider) =>
        ValidateStatement(
            statement,
            QueryPosition.InsertSelectSource,
            provider,
            allowNestedCteFragments: true);

    public static void ValidateDml(
        SqlStatement statement,
        SqlAgentToolType provider)
    {
        switch (statement)
        {
            case InsertStatement { Source: InsertQuerySource querySource }:
                ValidateStatement(
                    querySource.Query,
                    QueryPosition.InsertSelectSource,
                    provider,
                    allowNestedCteFragments: true);
                return;

            case InsertStatement { Source: InsertValuesSource values }:
                foreach (var row in values.Rows)
                foreach (var value in row)
                    VisitExpression(value, provider, allowNestedCteFragments: true);
                return;

            case UpdateStatement update:
                foreach (var assignment in update.Assignments)
                    VisitExpression(assignment.Value, provider, allowNestedCteFragments: true);
                if (update.Predicate is not null)
                    VisitExpression(update.Predicate, provider, allowNestedCteFragments: true);
                return;

            case DeleteStatement delete:
                if (delete.Predicate is not null)
                    VisitExpression(delete.Predicate, provider, allowNestedCteFragments: true);
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement for SqlKata DML compatibility validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateStatement(
        SqlStatement statement,
        QueryPosition position,
        SqlAgentToolType provider,
        bool allowNestedCteFragments)
    {
        switch (statement)
        {
            case SelectStatement select:
                ValidateCtePlacement(
                    select.Ctes,
                    position,
                    provider,
                    allowNestedCteFragments);
                foreach (var cte in select.Ctes)
                {
                    ValidateStatement(
                        cte.Query,
                        QueryPosition.CteDefinition,
                        provider,
                        allowNestedCteFragments);
                }
                if (select.From is DerivedTableSource derived)
                {
                    ValidateStatement(
                        derived.Query,
                        QueryPosition.DerivedTable,
                        provider,
                        allowNestedCteFragments);
                }
                foreach (var join in select.Joins)
                {
                    if (join.Source is DerivedTableSource joinedDerived)
                    {
                        ValidateStatement(
                            joinedDerived.Query,
                            QueryPosition.DerivedTable,
                            provider,
                            allowNestedCteFragments);
                    }
                }
                VisitSubqueryExpressions(
                    select,
                    provider,
                    allowNestedCteFragments);
                return;

            case QueryStatement query:
                ValidateCtePlacement(
                    query.Head.Ctes,
                    position,
                    provider,
                    allowNestedCteFragments);
                if (!query.Head.Ctes.IsDefaultOrEmpty
                    && RequiresSetTailWrapper(query)
                    && !CanPreserveSetTailCte(
                        query,
                        position,
                        provider,
                        allowNestedCteFragments))
                {
                    var detail = position == QueryPosition.ScalarSubquery
                        ? "a scalar/EXISTS root CTE set query needs a scope-preserving direct set tail; Core currently permits that path only when ORDER BY references a combined output name or output ordinal"
                        : "a set-operation query with a root CTE and outer ORDER BY/LIMIT/OFFSET would enter a nested Select compilation path that cannot preserve its CTE definition";
                    throw CteScopeError("select.cte_scope", detail);
                }
                ValidateStatement(
                    query.Head,
                    position,
                    provider,
                    allowNestedCteFragments);
                foreach (var operation in query.SetOperations)
                {
                    ValidateStatement(
                        operation.Query,
                        QueryPosition.SetBranch,
                        provider,
                        allowNestedCteFragments);
                }
                return;

            default:
                throw new SqlCompilationException(
                    $"Unsupported statement for SqlKata backend compatibility validation: {statement.GetType().Name}");
        }
    }

    private static void ValidateCtePlacement(
        System.Collections.Immutable.ImmutableArray<CteDefinition> ctes,
        QueryPosition position,
        SqlAgentToolType provider,
        bool allowNestedCteFragments)
    {
        if (ctes.IsDefaultOrEmpty)
            return;

        if (position == QueryPosition.CteDefinition
            && !CanLowerNestedCteFragment(provider, allowNestedCteFragments))
        {
            throw CteScopeError(
                "select.cte_scope",
                $"provider {provider} has no declared portable nested-WITH-inside-a-CTE-definition contract");
        }

        if (position == QueryPosition.ScalarSubquery
            && !CanLowerNestedCteFragment(provider, allowNestedCteFragments))
        {
            throw CteScopeError(
                "select.cte_scope",
                $"provider {provider} has no declared portable WITH-at-the-root-of-a-scalar/EXISTS-subquery contract");
        }

        if (position is QueryPosition.DerivedTable or QueryPosition.SetBranch
            && !CanLowerNestedCteFragment(provider, allowNestedCteFragments))
        {
            var location = position == QueryPosition.DerivedTable
                ? "derived-table"
                : "set-operation-branch";
            throw CteScopeError(
                "select.cte_scope",
                $"provider {provider} has no declared portable WITH-in-{location} lowering contract");
        }
    }

    private static bool CanPreserveSetTailCte(
        QueryStatement query,
        QueryPosition position,
        SqlAgentToolType provider,
        bool allowNestedCteFragments) =>
        position is QueryPosition.Root or QueryPosition.InsertSelectSource
        || (CanLowerNestedCteFragment(provider, allowNestedCteFragments)
            && position is QueryPosition.DerivedTable
                or QueryPosition.SetBranch
                or QueryPosition.CteDefinition)
        || (position == QueryPosition.ScalarSubquery
            && CanLowerNestedCteFragment(provider, allowNestedCteFragments)
            && CoreSqlKataSetTailScope.CanRenderDirectTail(query));

    private static bool CanLowerNestedCteFragment(
        SqlAgentToolType provider,
        bool allowNestedCteFragments) =>
        allowNestedCteFragments
        && provider is SqlAgentToolType.Postgres
            or SqlAgentToolType.MySQL
            or SqlAgentToolType.Sqlite;

    private static SqlCompilationException CteScopeError(string capability, string detail) =>
        new($"SQL capability '{capability}' is not supported by the current SqlKata backend: {detail}.");

    private static bool RequiresSetTailWrapper(QueryStatement query) =>
        !query.OrderBy.IsDefaultOrEmpty
        || query.Limit is not null
        || query.Offset is > 0;

    private static void VisitSubqueryExpressions(
        SelectStatement select,
        SqlAgentToolType provider,
        bool allowNestedCteFragments)
    {
        foreach (var item in select.Select)
            VisitExpression(item.Expression, provider, allowNestedCteFragments);
        if (select.Where is not null)
            VisitExpression(select.Where, provider, allowNestedCteFragments);
        foreach (var expression in select.GroupBy)
            VisitExpression(expression, provider, allowNestedCteFragments);
        if (select.Having is not null)
            VisitExpression(select.Having, provider, allowNestedCteFragments);
        foreach (var item in select.OrderBy)
            VisitExpression(item.Expression, provider, allowNestedCteFragments);
        foreach (var join in select.Joins)
        {
            if (join.Predicate is not null)
                VisitExpression(join.Predicate, provider, allowNestedCteFragments);
        }
    }

    private static void VisitExpression(
        SqlExpr expression,
        SqlAgentToolType provider,
        bool allowNestedCteFragments)
    {
        switch (expression)
        {
            case SubqueryExpr subquery:
                ValidateStatement(
                    subquery.Query,
                    QueryPosition.ScalarSubquery,
                    provider,
                    allowNestedCteFragments);
                return;
            case ExistsExpr exists:
                ValidateStatement(
                    exists.Query,
                    QueryPosition.ScalarSubquery,
                    provider,
                    allowNestedCteFragments);
                return;
            case UnaryExpr unary:
                VisitExpression(unary.Operand, provider, allowNestedCteFragments);
                return;
            case BinaryExpr binary:
                VisitExpression(binary.Left, provider, allowNestedCteFragments);
                VisitExpression(binary.Right, provider, allowNestedCteFragments);
                return;
            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                    VisitExpression(argument, provider, allowNestedCteFragments);
                return;
            case FilterExpr filter:
                VisitExpression(filter.Expression, provider, allowNestedCteFragments);
                VisitExpression(filter.Predicate, provider, allowNestedCteFragments);
                return;
            case WindowedExpr windowed:
                VisitExpression(windowed.Expression, provider, allowNestedCteFragments);
                foreach (var partition in windowed.Window.PartitionBy)
                    VisitExpression(partition, provider, allowNestedCteFragments);
                foreach (var item in windowed.Window.OrderBy)
                    VisitExpression(item.Expression, provider, allowNestedCteFragments);
                return;
            case CastExpr cast:
                VisitExpression(cast.Expression, provider, allowNestedCteFragments);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    VisitExpression(branch.Condition, provider, allowNestedCteFragments);
                    VisitExpression(branch.Value, provider, allowNestedCteFragments);
                }
                if (@case.ElseExpression is not null)
                    VisitExpression(@case.ElseExpression, provider, allowNestedCteFragments);
                return;
            case InExpr @in:
                VisitExpression(@in.Value, provider, allowNestedCteFragments);
                foreach (var item in @in.Items)
                    VisitExpression(item, provider, allowNestedCteFragments);
                return;
            case BetweenExpr between:
                VisitExpression(between.Value, provider, allowNestedCteFragments);
                VisitExpression(between.Lower, provider, allowNestedCteFragments);
                VisitExpression(between.Upper, provider, allowNestedCteFragments);
                return;
            case IsNullExpr isNull:
                VisitExpression(isNull.Value, provider, allowNestedCteFragments);
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
