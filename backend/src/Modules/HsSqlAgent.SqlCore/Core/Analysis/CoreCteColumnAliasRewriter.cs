using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Binding;
using SqlAgent.Service.Core.Compilation;

namespace HsSqlAgent.SqlCore.Core.Analysis;

/// <summary>
/// Canonicalizes a modeled CTE column-alias list into explicit aliases on the CTE output
/// projection. SqlKata does not model the `cte(col1, col2) AS (...)` surface directly, but the
/// projection-alias form is semantically equivalent when the output width is statically known.
/// Shapes whose output width depends on wildcard expansion remain fail-closed.
/// </summary>
internal static class CoreCteColumnAliasRewriter
{
    public static SqlStatement Rewrite(SqlStatement statement) => statement switch
    {
        SelectStatement select => RewriteSelect(select),
        QueryStatement query => RewriteQuery(query),
        UpdateStatement update => update with
        {
            Assignments = update.Assignments.Select(assignment => assignment with
            {
                Value = RewriteExpression(assignment.Value)
            }).ToImmutableArray(),
            Predicate = update.Predicate is null ? null : RewriteExpression(update.Predicate)
        },
        DeleteStatement delete => delete with
        {
            Predicate = delete.Predicate is null ? null : RewriteExpression(delete.Predicate)
        },
        InsertStatement insert => insert with { Source = RewriteInsertSource(insert.Source) },
        _ => throw new SqlCompilationException(
            $"Unsupported statement while canonicalizing CTE column aliases: {statement.GetType().Name}")
    };

    private static SelectStatement RewriteSelect(SelectStatement select)
    {
        var ctes = select.Ctes.Select(RewriteCte).ToImmutableArray();
        return select with
        {
            Ctes = ctes,
            From = select.From is null ? null : RewriteSource(select.From),
            Joins = select.Joins.Select(join => join with
            {
                Source = RewriteSource(join.Source),
                Predicate = join.Predicate is null ? null : RewriteExpression(join.Predicate)
            }).ToImmutableArray(),
            Select = select.Select.Select(item => item with
            {
                Expression = RewriteExpression(item.Expression)
            }).ToImmutableArray(),
            Where = select.Where is null ? null : RewriteExpression(select.Where),
            GroupBy = select.GroupBy.Select(RewriteExpression).ToImmutableArray(),
            Having = select.Having is null ? null : RewriteExpression(select.Having),
            OrderBy = select.OrderBy.Select(item => item with
            {
                Expression = RewriteExpression(item.Expression)
            }).ToImmutableArray()
        };
    }

    private static QueryStatement RewriteQuery(QueryStatement query) => query with
    {
        Head = RewriteSelect(query.Head),
        SetOperations = query.SetOperations.Select(operation => operation with
        {
            Query = Rewrite(operation.Query)
        }).ToImmutableArray(),
        OrderBy = query.OrderBy.Select(item => item with
        {
            Expression = RewriteExpression(item.Expression)
        }).ToImmutableArray()
    };

    private static CteDefinition RewriteCte(CteDefinition cte)
    {
        var query = Rewrite(cte.Query);
        if (cte.ColumnAliases.IsDefaultOrEmpty)
            return cte with { Query = query };

        query = ApplyOutputAliases(query, cte.ColumnAliases, cte.Name);
        return cte with
        {
            Query = query,
            ColumnAliases = ImmutableArray<SqlIdentifier>.Empty
        };
    }

    private static SqlStatement ApplyOutputAliases(
        SqlStatement statement,
        ImmutableArray<SqlIdentifier> aliases,
        SqlIdentifier cteName)
    {
        return statement switch
        {
            SelectStatement select => ApplyOutputAliases(select, aliases, cteName),
            QueryStatement query => query with
            {
                Head = ApplyOutputAliases(query.Head, aliases, cteName)
            },
            _ => throw new SqlCompilationException(
                $"CTE '{IdentifierText(cteName)}' column aliases require a SELECT query body.")
        };
    }

    private static SelectStatement ApplyOutputAliases(
        SelectStatement select,
        ImmutableArray<SqlIdentifier> aliases,
        SqlIdentifier cteName)
    {
        if (aliases.Any(alias => alias.Parts.Length != 1))
        {
            throw new SqlCompilationException(
                $"CTE '{IdentifierText(cteName)}' column aliases must be unqualified identifiers.");
        }

        if (select.Select.Any(item => ContainsWildcard(item.Expression)))
        {
            throw new SqlCompilationException(
                $"CTE '{IdentifierText(cteName)}' column aliases cannot be lowered safely when the CTE projection contains a wildcard.");
        }

        if (select.Select.Length != aliases.Length)
        {
            throw new SqlCompilationException(
                $"CTE '{IdentifierText(cteName)}' declares {aliases.Length} column alias(es) " +
                $"but its statically modeled projection has {select.Select.Length} column(s).");
        }

        var projection = select.Select
            .Select((item, index) => item with { Alias = aliases[index].Parts[0] })
            .ToImmutableArray();
        return select with { Select = projection };
    }

    private static TableSource RewriteSource(TableSource source) => source switch
    {
        NamedTableSource named => named,
        DerivedTableSource derived => derived with { Query = Rewrite(derived.Query) },
        _ => throw new SqlCompilationException(
            $"Unsupported table source while canonicalizing CTE column aliases: {source.GetType().Name}")
    };

    private static InsertSource RewriteInsertSource(InsertSource source) => source switch
    {
        InsertValuesSource values => values with
        {
            Rows = values.Rows.Select(row => row.Select(RewriteExpression).ToImmutableArray()).ToImmutableArray()
        },
        InsertQuerySource query => query with { Query = Rewrite(query.Query) },
        _ => throw new SqlCompilationException(
            $"Unsupported INSERT source while canonicalizing CTE column aliases: {source.GetType().Name}")
    };

    private static SqlExpr RewriteExpression(SqlExpr expression) => expression switch
    {
        LiteralExpr literal => literal,
        ColumnExpr column => column,
        BoundColumnExpr column => column,
        IntervalExpr interval => interval,
        UnaryExpr unary => unary with { Operand = RewriteExpression(unary.Operand) },
        BinaryExpr binary => binary with
        {
            Left = RewriteExpression(binary.Left),
            Right = RewriteExpression(binary.Right)
        },
        FunctionCallExpr function => function with
        {
            Arguments = function.Arguments.Select(RewriteExpression).ToImmutableArray()
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
                PartitionBy = windowed.Window.PartitionBy.Select(RewriteExpression).ToImmutableArray(),
                OrderBy = windowed.Window.OrderBy.Select(item => item with
                {
                    Expression = RewriteExpression(item.Expression)
                }).ToImmutableArray()
            }
        },
        CastExpr cast => cast with { Expression = RewriteExpression(cast.Expression) },
        CaseExpr @case => @case with
        {
            Branches = @case.Branches.Select(branch => new CaseBranch(
                RewriteExpression(branch.Condition),
                RewriteExpression(branch.Value))).ToImmutableArray(),
            ElseExpression = @case.ElseExpression is null ? null : RewriteExpression(@case.ElseExpression)
        },
        InExpr @in => @in with
        {
            Value = RewriteExpression(@in.Value),
            Items = @in.Items.Select(RewriteExpression).ToImmutableArray()
        },
        BetweenExpr between => between with
        {
            Value = RewriteExpression(between.Value),
            Lower = RewriteExpression(between.Lower),
            Upper = RewriteExpression(between.Upper)
        },
        IsNullExpr isNull => isNull with { Value = RewriteExpression(isNull.Value) },
        SubqueryExpr subquery => subquery with { Query = Rewrite(subquery.Query) },
        ExistsExpr exists => exists with { Query = Rewrite(exists.Query) },
        _ => throw new SqlCompilationException(
            $"Unsupported expression while canonicalizing CTE column aliases: {expression.GetType().Name}")
    };

    private static bool ContainsWildcard(SqlExpr expression) => expression switch
    {
        ColumnExpr column => IsWildcard(column.Name),
        BoundColumnExpr column => IsWildcard(column.Name),
        _ => false
    };

    private static bool IsWildcard(SqlIdentifier identifier) =>
        !identifier.Parts.IsDefaultOrEmpty
        && identifier.Parts[^1].Value == "*"
        && !identifier.Parts[^1].WasQuoted;

    private static string IdentifierText(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));
}
