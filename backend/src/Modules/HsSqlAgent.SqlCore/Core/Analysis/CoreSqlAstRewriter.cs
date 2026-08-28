using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Central fail-closed structural rewrite for the Core AST. Profile/capability passes subclass this
/// walker and override expression-node hooks instead of maintaining parallel statement/table/
/// expression recursion. Adding a new AST shape therefore has one mutation traversal point to update.
/// </summary>
internal abstract class CoreSqlAstRewriter
{
    private readonly string _context;

    protected CoreSqlAstRewriter(string context)
    {
        if (string.IsNullOrWhiteSpace(context))
            throw new ArgumentException("AST rewrite context cannot be empty.", nameof(context));
        _context = context.Trim();
    }

    public SqlStatement Rewrite(SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return RewriteStatement(statement);
    }

    private SqlStatement RewriteStatement(SqlStatement statement) => statement switch
    {
        SelectStatement select => RewriteSelect(select),
        QueryStatement query => query with
        {
            Head = RewriteSelect(query.Head),
            SetOperations = query.SetOperations
                .Select(operation => operation with
                {
                    Query = RewriteStatement(operation.Query)
                })
                .ToImmutableArray(),
            OrderBy = RewriteOrderBy(query.OrderBy)
        },
        InsertStatement insert => insert with
        {
            Source = RewriteInsertSource(insert.Source)
        },
        UpdateStatement update => update with
        {
            Assignments = update.Assignments
                .Select(assignment => assignment with
                {
                    Value = RewriteExpression(assignment.Value)
                })
                .ToImmutableArray(),
            Predicate = update.Predicate is null
                ? null
                : RewriteExpression(update.Predicate)
        },
        DeleteStatement delete => delete with
        {
            Predicate = delete.Predicate is null
                ? null
                : RewriteExpression(delete.Predicate)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported statement during {_context} AST rewrite: {statement.GetType().Name}")
    };

    private InsertSource RewriteInsertSource(InsertSource source) => source switch
    {
        InsertValuesSource values => values with
        {
            Rows = values.Rows
                .Select(row => row
                    .Select(RewriteExpression)
                    .ToImmutableArray())
                .ToImmutableArray()
        },
        InsertQuerySource querySource => querySource with
        {
            Query = RewriteStatement(querySource.Query)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported INSERT source during {_context} AST rewrite: {source.GetType().Name}")
    };

    private SelectStatement RewriteSelect(SelectStatement select) => select with
    {
        Ctes = select.Ctes
            .Select(cte => cte with
            {
                Query = RewriteStatement(cte.Query)
            })
            .ToImmutableArray(),
        Select = select.Select
            .Select(item => item with
            {
                Expression = RewriteExpression(item.Expression)
            })
            .ToImmutableArray(),
        From = select.From is null
            ? null
            : RewriteSource(select.From),
        Joins = select.Joins
            .Select(join => join with
            {
                Source = RewriteSource(join.Source),
                Predicate = join.Predicate is null
                    ? null
                    : RewriteExpression(join.Predicate)
            })
            .ToImmutableArray(),
        Where = select.Where is null
            ? null
            : RewriteExpression(select.Where),
        GroupBy = select.GroupBy
            .Select(RewriteExpression)
            .ToImmutableArray(),
        Having = select.Having is null
            ? null
            : RewriteExpression(select.Having),
        OrderBy = RewriteOrderBy(select.OrderBy)
    };

    private TableSource RewriteSource(TableSource source) => source switch
    {
        NamedTableSource => source,
        DerivedTableSource derived => derived with
        {
            Query = RewriteStatement(derived.Query)
        },
        _ => throw new SqlCompilationException(
            $"Unsupported table source during {_context} AST rewrite: {source.GetType().Name}")
    };

    private ImmutableArray<OrderByItem> RewriteOrderBy(
        ImmutableArray<OrderByItem> orderBy) =>
        orderBy
            .Select(item => item with
            {
                Expression = RewriteExpression(item.Expression)
            })
            .ToImmutableArray();

    private SqlExpr RewriteExpression(SqlExpr expression)
    {
        var rewritten = expression switch
        {
            LiteralExpr or ColumnExpr or BoundColumnExpr or IntervalExpr => expression,
            UnaryExpr unary => unary with
            {
                Operand = RewriteExpression(unary.Operand)
            },
            BinaryExpr binary => binary with
            {
                Left = RewriteExpression(binary.Left),
                Right = RewriteExpression(binary.Right)
            },
            FunctionCallExpr function => function with
            {
                Arguments = function.Arguments
                    .Select(RewriteExpression)
                    .ToImmutableArray(),
                AggregateOrderBy = RewriteOrderBy(function.AggregateOrderBy)
            },
            FilterExpr filter => filter with
            {
                Expression = RewriteExpression(filter.Expression),
                Predicate = RewriteExpression(filter.Predicate)
            },
            WindowedExpr windowed => windowed with
            {
                Expression = RewriteExpression(windowed.Expression),
                Window = windowed.Window with
                {
                    PartitionBy = windowed.Window.PartitionBy
                        .Select(RewriteExpression)
                        .ToImmutableArray(),
                    OrderBy = RewriteOrderBy(windowed.Window.OrderBy)
                }
            },
            CastExpr cast => cast with
            {
                Expression = RewriteExpression(cast.Expression)
            },
            SimpleCaseExpr simpleCase => new SimpleCaseExpr(
                RewriteBranches(simpleCase.Branches),
                simpleCase.ElseExpression is null
                    ? null
                    : RewriteExpression(simpleCase.ElseExpression),
                simpleCase.Span),
            CaseExpr @case => @case with
            {
                Branches = RewriteBranches(@case.Branches),
                ElseExpression = @case.ElseExpression is null
                    ? null
                    : RewriteExpression(@case.ElseExpression)
            },
            InExpr @in => @in with
            {
                Value = RewriteExpression(@in.Value),
                Items = @in.Items
                    .Select(RewriteExpression)
                    .ToImmutableArray()
            },
            BetweenExpr between => between with
            {
                Value = RewriteExpression(between.Value),
                Lower = RewriteExpression(between.Lower),
                Upper = RewriteExpression(between.Upper)
            },
            IsNullExpr isNull => isNull with
            {
                Value = RewriteExpression(isNull.Value)
            },
            SubqueryExpr subquery => subquery with
            {
                Query = RewriteStatement(subquery.Query)
            },
            ExistsExpr exists => exists with
            {
                Query = RewriteStatement(exists.Query)
            },
            _ => throw new SqlCompilationException(
                $"Unsupported expression during {_context} AST rewrite: {expression.GetType().Name}")
        };

        return RewriteExpressionNode(rewritten);
    }

    private ImmutableArray<CaseBranch> RewriteBranches(
        ImmutableArray<CaseBranch> branches) =>
        branches
            .Select(branch => branch with
            {
                Condition = RewriteExpression(branch.Condition),
                Value = RewriteExpression(branch.Value)
            })
            .ToImmutableArray();

    protected virtual SqlExpr RewriteExpressionNode(SqlExpr expression) => expression;
}
