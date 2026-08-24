using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.Core.Analysis;

/// <summary>
/// Removes explicit NULL ordering only when the target provider's native default is already
/// semantically identical. MySQL and SQL Server sort NULL before non-NULL values in ascending
/// order and after non-NULL values in descending order, but do not accept PostgreSQL-style
/// NULLS FIRST/LAST syntax. The inverse explicit orderings remain intact so capability validation
/// can fail closed rather than introducing an expression rewrite that could duplicate evaluation.
/// </summary>
internal static class CoreNullOrderingRewriter
{
    public static SqlStatement Rewrite(SqlStatement statement, SqlAgentToolType targetProvider)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (targetProvider is not (SqlAgentToolType.MySQL or SqlAgentToolType.MsSqlServer))
            return statement;

        return RewriteStatement(statement, targetProvider);
    }

    private static SqlStatement RewriteStatement(SqlStatement statement, SqlAgentToolType targetProvider) => statement switch
    {
        SelectStatement select => RewriteSelect(select, targetProvider),
        QueryStatement query => query with
        {
            Head = RewriteSelect(query.Head, targetProvider),
            SetOperations = query.SetOperations.Select(operation => operation with
            {
                Query = RewriteStatement(operation.Query, targetProvider)
            }).ToImmutableArray(),
            OrderBy = RewriteOrderBy(query.OrderBy, targetProvider)
        },
        UpdateStatement update => update with
        {
            Assignments = update.Assignments.Select(assignment => assignment with
            {
                Value = RewriteExpression(assignment.Value, targetProvider)
            }).ToImmutableArray(),
            Predicate = update.Predicate is null ? null : RewriteExpression(update.Predicate, targetProvider)
        },
        DeleteStatement delete => delete with
        {
            Predicate = delete.Predicate is null ? null : RewriteExpression(delete.Predicate, targetProvider)
        },
        InsertStatement insert => insert with
        {
            Source = RewriteInsertSource(insert.Source, targetProvider)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported statement while canonicalizing NULL ordering: {statement.GetType().Name}")
    };

    private static SelectStatement RewriteSelect(SelectStatement select, SqlAgentToolType targetProvider) => select with
    {
        Ctes = select.Ctes.Select(cte => cte with
        {
            Query = RewriteStatement(cte.Query, targetProvider)
        }).ToImmutableArray(),
        Select = select.Select.Select(item => item with
        {
            Expression = RewriteExpression(item.Expression, targetProvider)
        }).ToImmutableArray(),
        From = select.From is null ? null : RewriteSource(select.From, targetProvider),
        Joins = select.Joins.Select(join => join with
        {
            Source = RewriteSource(join.Source, targetProvider),
            Predicate = join.Predicate is null ? null : RewriteExpression(join.Predicate, targetProvider)
        }).ToImmutableArray(),
        Where = select.Where is null ? null : RewriteExpression(select.Where, targetProvider),
        GroupBy = select.GroupBy.Select(expression => RewriteExpression(expression, targetProvider)).ToImmutableArray(),
        Having = select.Having is null ? null : RewriteExpression(select.Having, targetProvider),
        OrderBy = RewriteOrderBy(select.OrderBy, targetProvider)
    };

    private static TableSource RewriteSource(TableSource source, SqlAgentToolType targetProvider) => source switch
    {
        NamedTableSource named => named,
        DerivedTableSource derived => derived with
        {
            Query = RewriteStatement(derived.Query, targetProvider)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported table source while canonicalizing NULL ordering: {source.GetType().Name}")
    };

    private static InsertSource RewriteInsertSource(InsertSource source, SqlAgentToolType targetProvider) => source switch
    {
        InsertValuesSource values => values with
        {
            Rows = values.Rows.Select(row => row
                .Select(value => RewriteExpression(value, targetProvider))
                .ToImmutableArray()).ToImmutableArray()
        },
        InsertQuerySource query => query with
        {
            Query = RewriteStatement(query.Query, targetProvider)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported INSERT source while canonicalizing NULL ordering: {source.GetType().Name}")
    };

    private static ImmutableArray<OrderByItem> RewriteOrderBy(
        ImmutableArray<OrderByItem> orderBy,
        SqlAgentToolType targetProvider) => orderBy.Select(item =>
    {
        var nullOrdering = IsTargetDefault(item)
            ? NullOrderingKind.Default
            : item.NullOrdering;
        return item with
        {
            Expression = RewriteExpression(item.Expression, targetProvider),
            NullOrdering = nullOrdering
        };
    }).ToImmutableArray();

    private static bool IsTargetDefault(OrderByItem item) =>
        (!item.Descending && item.NullOrdering == NullOrderingKind.First)
        || (item.Descending && item.NullOrdering == NullOrderingKind.Last);

    private static SqlExpr RewriteExpression(SqlExpr expression, SqlAgentToolType targetProvider) => expression switch
    {
        LiteralExpr literal => literal,
        ColumnExpr column => column,
        BoundColumnExpr column => column,
        IntervalExpr interval => interval,
        UnaryExpr unary => unary with
        {
            Operand = RewriteExpression(unary.Operand, targetProvider)
        },
        BinaryExpr binary => binary with
        {
            Left = RewriteExpression(binary.Left, targetProvider),
            Right = RewriteExpression(binary.Right, targetProvider)
        },
        FunctionCallExpr function => function with
        {
            Arguments = function.Arguments
                .Select(argument => RewriteExpression(argument, targetProvider))
                .ToImmutableArray()
        },
        FilterExpr filter => filter with
        {
            Expression = RewriteExpression(filter.Expression, targetProvider),
            Predicate = RewriteExpression(filter.Predicate, targetProvider)
        },
        WindowedExpr windowed => windowed with
        {
            Expression = RewriteExpression(windowed.Expression, targetProvider),
            Window = windowed.Window with
            {
                PartitionBy = windowed.Window.PartitionBy
                    .Select(partition => RewriteExpression(partition, targetProvider))
                    .ToImmutableArray(),
                OrderBy = RewriteOrderBy(windowed.Window.OrderBy, targetProvider)
            }
        },
        CastExpr cast => cast with
        {
            Expression = RewriteExpression(cast.Expression, targetProvider)
        },
        CaseExpr @case => @case with
        {
            Branches = @case.Branches.Select(branch => new CaseBranch(
                RewriteExpression(branch.Condition, targetProvider),
                RewriteExpression(branch.Value, targetProvider))).ToImmutableArray(),
            ElseExpression = @case.ElseExpression is null
                ? null
                : RewriteExpression(@case.ElseExpression, targetProvider)
        },
        InExpr @in => @in with
        {
            Value = RewriteExpression(@in.Value, targetProvider),
            Items = @in.Items.Select(item => RewriteExpression(item, targetProvider)).ToImmutableArray()
        },
        BetweenExpr between => between with
        {
            Value = RewriteExpression(between.Value, targetProvider),
            Lower = RewriteExpression(between.Lower, targetProvider),
            Upper = RewriteExpression(between.Upper, targetProvider)
        },
        IsNullExpr isNull => isNull with
        {
            Value = RewriteExpression(isNull.Value, targetProvider)
        },
        SubqueryExpr subquery => subquery with
        {
            Query = RewriteStatement(subquery.Query, targetProvider)
        },
        ExistsExpr exists => exists with
        {
            Query = RewriteStatement(exists.Query, targetProvider)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported expression while canonicalizing NULL ordering: {expression.GetType().Name}")
    };
}
