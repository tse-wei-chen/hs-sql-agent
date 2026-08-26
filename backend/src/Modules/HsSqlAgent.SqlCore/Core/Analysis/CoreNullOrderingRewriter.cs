using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Binding;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Enums;

namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Canonicalizes explicit NULL ordering for targets that do not accept PostgreSQL-style
/// NULLS FIRST/LAST syntax. MySQL and SQL Server both sort NULL before non-NULL values in ascending
/// order and after non-NULL values in descending order, so those default-equivalent modifiers are
/// removed directly. The inverse orderings are lowered only for a direct bound row-source column,
/// where a CASE null-rank plus the original column does not duplicate arbitrary expression
/// evaluation. DISTINCT statement tails, set-operation tails, projection-alias references, and
/// computed expressions remain explicit so capability validation continues to fail closed.
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
            // A compound-query tail has no row-source binding scope. Keep inverse NULL ordering
            // explicit there so provider validation can enforce the existing set-result contract.
            OrderBy = RewriteOrderBy(
                query.OrderBy,
                targetProvider,
                allowInverseColumnRewrite: false,
                blockedAliases: null)
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

    private static SelectStatement RewriteSelect(SelectStatement select, SqlAgentToolType targetProvider)
    {
        var blockedAliases = select.Select
            .Where(item => item.Alias is not null)
            .Select(item => item.Alias!.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return select with
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
            OrderBy = RewriteOrderBy(
                select.OrderBy,
                targetProvider,
                allowInverseColumnRewrite: !select.Distinct,
                blockedAliases)
        };
    }

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
        SqlAgentToolType targetProvider,
        bool allowInverseColumnRewrite,
        IReadOnlySet<string>? blockedAliases)
    {
        var builder = ImmutableArray.CreateBuilder<OrderByItem>(orderBy.Length * 2);
        foreach (var item in orderBy)
        {
            var expression = RewriteExpression(item.Expression, targetProvider);
            if (IsTargetDefault(item))
            {
                builder.Add(item with
                {
                    Expression = expression,
                    NullOrdering = NullOrderingKind.Default
                });
                continue;
            }

            if (allowInverseColumnRewrite
                && IsInverseExplicitOrdering(item)
                && IsStableRowSourceColumn(expression, blockedAliases))
            {
                builder.Add(CreateNullRankOrder(item, expression));
                builder.Add(item with
                {
                    Expression = expression,
                    NullOrdering = NullOrderingKind.Default
                });
                continue;
            }

            builder.Add(item with { Expression = expression });
        }
        return builder.ToImmutable();
    }

    private static bool IsTargetDefault(OrderByItem item) =>
        (!item.Descending && item.NullOrdering == NullOrderingKind.First)
        || (item.Descending && item.NullOrdering == NullOrderingKind.Last);

    private static bool IsInverseExplicitOrdering(OrderByItem item) =>
        (!item.Descending && item.NullOrdering == NullOrderingKind.Last)
        || (item.Descending && item.NullOrdering == NullOrderingKind.First);

    private static bool IsStableRowSourceColumn(
        SqlExpr expression,
        IReadOnlySet<string>? blockedAliases)
    {
        if (expression is not BoundColumnExpr { Source: not null } column
            || column.Name.Parts.IsDefaultOrEmpty)
        {
            return false;
        }

        // Qualified identifiers cannot be SELECT-list aliases. For an unqualified identifier,
        // conservatively keep the old fail-closed behavior if any output alias has the same name;
        // SQL Server does not permit using a SELECT alias inside the injected CASE expression.
        return column.Name.Parts.Length > 1
            || blockedAliases is null
            || !blockedAliases.Contains(column.Name.Parts[0].Value);
    }

    private static OrderByItem CreateNullRankOrder(OrderByItem item, SqlExpr expression)
    {
        var nullRank = item.NullOrdering == NullOrderingKind.Last ? 1 : 0;
        var nonNullRank = item.NullOrdering == NullOrderingKind.Last ? 0 : 1;
        var rankExpression = new CaseExpr(
            [new CaseBranch(
                new IsNullExpr(expression, IsNegated: false, item.Span),
                new LiteralExpr(nullRank, item.Span))],
            new LiteralExpr(nonNullRank, item.Span),
            item.Span);

        return new OrderByItem(
            rankExpression,
            Descending: false,
            NullOrderingKind.Default,
            item.Span);
    }

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
                OrderBy = RewriteOrderBy(
                    windowed.Window.OrderBy,
                    targetProvider,
                    allowInverseColumnRewrite: true,
                    blockedAliases: null)
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
