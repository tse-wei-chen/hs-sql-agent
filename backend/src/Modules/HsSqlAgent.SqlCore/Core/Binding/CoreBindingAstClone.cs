using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Binding;

/// <summary>
/// Temporary immutable-copy seam used by the F# binder while the public AST is
/// still implemented as C# records. The binding logic lives in F#; this type
/// only preserves C# record clone semantics (including derived record runtime
/// types and init-only metadata). Delete it when the AST itself moves to F#.
/// </summary>
internal static class CoreBindingAstClone
{
    internal static CteDefinition Cte(CteDefinition source, SqlStatement query) =>
        source with { Query = query };

    internal static SelectStatement Select(
        SelectStatement source,
        ImmutableArray<CteDefinition> ctes,
        TableSource? from,
        ImmutableArray<JoinSource> joins,
        ImmutableArray<SelectItem> select,
        SqlExpr? where,
        ImmutableArray<SqlExpr> groupBy,
        SqlExpr? having,
        ImmutableArray<OrderByItem> orderBy) =>
        source with
        {
            Ctes = ctes,
            From = from,
            Joins = joins,
            Select = select,
            Where = where,
            GroupBy = groupBy,
            Having = having,
            OrderBy = orderBy
        };

    internal static QueryStatement Query(
        QueryStatement source,
        SelectStatement head,
        ImmutableArray<SetOperation> setOperations,
        ImmutableArray<OrderByItem> orderBy) =>
        source with
        {
            Head = head,
            SetOperations = setOperations,
            OrderBy = orderBy
        };

    internal static SetOperation SetOperation(
        SetOperation source,
        SqlStatement query) =>
        source with { Query = query };

    internal static DerivedTableSource Derived(
        DerivedTableSource source,
        SqlStatement query) =>
        source with { Query = query };

    internal static JoinSource Join(
        JoinSource source,
        TableSource tableSource,
        SqlExpr? predicate) =>
        source with { Source = tableSource, Predicate = predicate };

    internal static SelectItem SelectItem(
        SelectItem source,
        SqlExpr expression) =>
        source with { Expression = expression };

    internal static OrderByItem OrderBy(
        OrderByItem source,
        SqlExpr expression) =>
        source with { Expression = expression };

    internal static BoundColumnExpr BoundColumn(
        ColumnExpr source,
        TableSymbol? table,
        bool isOuterReference) =>
        new(source.Name, table, source.Span)
        {
            IsOuterReference = isOuterReference
        };

    internal static UnaryExpr Unary(UnaryExpr source, SqlExpr operand) =>
        source with { Operand = operand };

    internal static BinaryExpr Binary(
        BinaryExpr source,
        SqlExpr left,
        SqlExpr right) =>
        source with { Left = left, Right = right };

    internal static FunctionCallExpr Function(
        FunctionCallExpr source,
        ImmutableArray<SqlExpr> arguments,
        ImmutableArray<OrderByItem> aggregateOrderBy) =>
        source with
        {
            Arguments = arguments,
            AggregateOrderBy = aggregateOrderBy
        };

    internal static BinaryExpr BinaryOperator(
        BinaryExpr source,
        string @operator) =>
        source with { Operator = @operator };

    internal static FunctionCallExpr FunctionName(
        FunctionCallExpr source,
        SqlIdentifier name) =>
        source with { Name = name };

    internal static FilterExpr Filter(
        FilterExpr source,
        SqlExpr expression,
        SqlExpr predicate) =>
        source with { Expression = expression, Predicate = predicate };

    internal static WindowedExpr Windowed(
        WindowedExpr source,
        SqlExpr expression,
        WindowSpec window) =>
        source with { Expression = expression, Window = window };

    internal static WindowSpec Window(
        WindowSpec source,
        ImmutableArray<SqlExpr> partitionBy,
        ImmutableArray<OrderByItem> orderBy) =>
        source with { PartitionBy = partitionBy, OrderBy = orderBy };

    internal static CastExpr Cast(CastExpr source, SqlExpr expression) =>
        source with { Expression = expression };

    internal static CaseExpr Case(
        CaseExpr source,
        ImmutableArray<CaseBranch> branches,
        SqlExpr? elseExpression) =>
        source with { Branches = branches, ElseExpression = elseExpression };

    internal static InExpr In(
        InExpr source,
        SqlExpr value,
        ImmutableArray<SqlExpr> items) =>
        source with { Value = value, Items = items };

    internal static BetweenExpr Between(
        BetweenExpr source,
        SqlExpr value,
        SqlExpr lower,
        SqlExpr upper) =>
        source with { Value = value, Lower = lower, Upper = upper };

    internal static IsNullExpr IsNull(IsNullExpr source, SqlExpr value) =>
        source with { Value = value };

    internal static SubqueryExpr Subquery(
        SubqueryExpr source,
        SqlStatement query) =>
        source with { Query = query };

    internal static Assignment Assignment(
        Assignment source,
        SqlExpr value) =>
        source with { Value = value };

    internal static UpdateStatement Update(
        UpdateStatement source,
        ImmutableArray<Assignment> assignments,
        SqlExpr? predicate) =>
        source with { Assignments = assignments, Predicate = predicate };

    internal static DeleteStatement Delete(
        DeleteStatement source,
        SqlExpr? predicate) =>
        source with { Predicate = predicate };

    internal static InsertQuerySource InsertQuery(
        InsertQuerySource source,
        SqlStatement query) =>
        source with { Query = query };

    internal static InsertStatement Insert(
        InsertStatement source,
        InsertSource insertSource) =>
        source with { Source = insertSource };

    internal static ExistsExpr Exists(
        ExistsExpr source,
        SqlStatement query) =>
        source with { Query = query };
}
