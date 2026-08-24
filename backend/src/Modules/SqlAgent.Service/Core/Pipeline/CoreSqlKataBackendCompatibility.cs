using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Pipeline;

/// <summary>
/// Guards query shapes whose CTE scope cannot be preserved by the available lowering path.
/// Statement-root INSERT..SELECT CTEs have a dedicated provider-aware lowerer. Query-graph derived
/// tables and set-operation branches can use fully compiled fragments on providers that accept WITH
/// at the beginning of a general subquery. Eager scalar-subquery and INSERT-source nested CTEs
/// remain fail-closed because those paths are rendered before the query-graph adapter can intervene.
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
            allowDerivedCteFragments: true);

    public static void ValidateInsertSelect(
        SqlStatement statement,
        SqlAgentToolType provider) =>
        ValidateStatement(
            statement,
            QueryPosition.InsertSelectSource,
            provider,
            allowDerivedCteFragments: false);

    private static void ValidateStatement(
        SqlStatement statement,
        QueryPosition position,
        SqlAgentToolType provider,
        bool allowDerivedCteFragments)
    {
        switch (statement)
        {
            case SelectStatement select:
                ValidateCtePlacement(
                    select.Ctes,
                    position,
                    provider,
                    allowDerivedCteFragments);
                foreach (var cte in select.Ctes)
                {
                    ValidateStatement(
                        cte.Query,
                        QueryPosition.CteDefinition,
                        provider,
                        allowDerivedCteFragments);
                }
                if (select.From is DerivedTableSource derived)
                {
                    ValidateStatement(
                        derived.Query,
                        QueryPosition.DerivedTable,
                        provider,
                        allowDerivedCteFragments);
                }
                foreach (var join in select.Joins)
                {
                    if (join.Source is DerivedTableSource joinedDerived)
                    {
                        ValidateStatement(
                            joinedDerived.Query,
                            QueryPosition.DerivedTable,
                            provider,
                            allowDerivedCteFragments);
                    }
                }
                VisitSubqueryExpressions(select, provider);
                return;

            case QueryStatement query:
                ValidateCtePlacement(
                    query.Head.Ctes,
                    position,
                    provider,
                    allowDerivedCteFragments);
                if (!query.Head.Ctes.IsDefaultOrEmpty
                    && RequiresSetTailWrapper(query)
                    && !CanPreserveSetTailCte(
                        position,
                        provider,
                        allowDerivedCteFragments))
                {
                    throw CteScopeError(
                        "select.cte_scope",
                        "a set-operation query with a root CTE and outer ORDER BY/LIMIT/OFFSET would enter a nested Select compilation path that cannot preserve its CTE definition");
                }
                ValidateStatement(
                    query.Head,
                    position,
                    provider,
                    allowDerivedCteFragments);
                foreach (var operation in query.SetOperations)
                {
                    ValidateStatement(
                        operation.Query,
                        QueryPosition.SetBranch,
                        provider,
                        allowDerivedCteFragments);
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
        bool allowDerivedCteFragments)
    {
        if (ctes.IsDefaultOrEmpty)
            return;

        switch (position)
        {
            case QueryPosition.DerivedTable
                when !CanLowerNestedCteFragment(provider, allowDerivedCteFragments):
                throw CteScopeError(
                    "select.cte_scope",
                    allowDerivedCteFragments
                        ? $"provider {provider} has no declared portable WITH-in-derived-table lowering contract"
                        : "a derived-table-local CTE is inside an eager scalar/DML nested compilation path where the query-graph CTE adapter cannot preserve it");
            case QueryPosition.SetBranch
                when !CanLowerNestedCteFragment(provider, allowDerivedCteFragments):
                throw CteScopeError(
                    "select.cte_scope",
                    allowDerivedCteFragments
                        ? $"provider {provider} has no declared portable wrapped set-branch CTE lowering contract"
                        : "a set-operation-branch-local CTE is inside an eager scalar/DML nested compilation path where the query-graph CTE adapter cannot preserve it");
        }
    }

    private static bool CanPreserveSetTailCte(
        QueryPosition position,
        SqlAgentToolType provider,
        bool allowDerivedCteFragments) =>
        position is QueryPosition.Root or QueryPosition.InsertSelectSource
        || position is QueryPosition.DerivedTable or QueryPosition.SetBranch
            && CanLowerNestedCteFragment(provider, allowDerivedCteFragments);

    private static bool CanLowerNestedCteFragment(
        SqlAgentToolType provider,
        bool allowDerivedCteFragments) =>
        allowDerivedCteFragments
        && provider is SqlAgentToolType.Postgres
            or SqlAgentToolType.MySQL
            or SqlAgentToolType.Sqlite
            or SqlAgentToolType.Oracle;

    private static SqlCompilationException CteScopeError(string capability, string detail) =>
        new($"SQL capability '{capability}' is not supported by the current SqlKata backend: {detail}.");

    private static bool RequiresSetTailWrapper(QueryStatement query) =>
        !query.OrderBy.IsDefaultOrEmpty
        || query.Limit is not null
        || query.Offset is > 0;

    private static void VisitSubqueryExpressions(
        SelectStatement select,
        SqlAgentToolType provider)
    {
        foreach (var item in select.Select) VisitExpression(item.Expression, provider);
        if (select.Where is not null) VisitExpression(select.Where, provider);
        foreach (var expression in select.GroupBy) VisitExpression(expression, provider);
        if (select.Having is not null) VisitExpression(select.Having, provider);
        foreach (var item in select.OrderBy) VisitExpression(item.Expression, provider);
        foreach (var join in select.Joins)
        {
            if (join.Predicate is not null)
                VisitExpression(join.Predicate, provider);
        }
    }

    private static void VisitExpression(
        SqlExpr expression,
        SqlAgentToolType provider)
    {
        switch (expression)
        {
            case SubqueryExpr subquery:
                ValidateStatement(
                    subquery.Query,
                    QueryPosition.ScalarSubquery,
                    provider,
                    allowDerivedCteFragments: false);
                return;
            case ExistsExpr exists:
                ValidateStatement(
                    exists.Query,
                    QueryPosition.ScalarSubquery,
                    provider,
                    allowDerivedCteFragments: false);
                return;
            case UnaryExpr unary:
                VisitExpression(unary.Operand, provider);
                return;
            case BinaryExpr binary:
                VisitExpression(binary.Left, provider);
                VisitExpression(binary.Right, provider);
                return;
            case FunctionCallExpr function:
                foreach (var argument in function.Arguments)
                    VisitExpression(argument, provider);
                return;
            case FilterExpr filter:
                VisitExpression(filter.Expression, provider);
                VisitExpression(filter.Predicate, provider);
                return;
            case WindowedExpr windowed:
                VisitExpression(windowed.Expression, provider);
                foreach (var partition in windowed.Window.PartitionBy)
                    VisitExpression(partition, provider);
                foreach (var item in windowed.Window.OrderBy)
                    VisitExpression(item.Expression, provider);
                return;
            case CastExpr cast:
                VisitExpression(cast.Expression, provider);
                return;
            case CaseExpr @case:
                foreach (var branch in @case.Branches)
                {
                    VisitExpression(branch.Condition, provider);
                    VisitExpression(branch.Value, provider);
                }
                if (@case.ElseExpression is not null)
                    VisitExpression(@case.ElseExpression, provider);
                return;
            case InExpr @in:
                VisitExpression(@in.Value, provider);
                foreach (var item in @in.Items)
                    VisitExpression(item, provider);
                return;
            case BetweenExpr between:
                VisitExpression(between.Value, provider);
                VisitExpression(between.Lower, provider);
                VisitExpression(between.Upper, provider);
                return;
            case IsNullExpr isNull:
                VisitExpression(isNull.Value, provider);
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
