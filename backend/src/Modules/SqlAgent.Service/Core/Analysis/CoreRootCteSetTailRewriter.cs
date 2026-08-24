using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;

namespace SqlAgent.Service.Core.Analysis;

/// <summary>
/// Preserves root CTE scope when a set-operation query needs an outer ORDER BY/LIMIT/OFFSET
/// wrapper. SqlKata compiles a nested set query through CompileSelectQuery, which does not emit
/// that nested query's WITH components. Moving only the statement-root CTEs to the mechanically
/// generated outer SELECT is scope-equivalent and keeps nested/local CTE shapes fail-closed.
/// </summary>
internal static class CoreRootCteSetTailRewriter
{
    public static SqlStatement Rewrite(SqlStatement statement)
    {
        if (statement is not QueryStatement query
            || query.SetOperations.IsDefaultOrEmpty
            || query.Head.Ctes.IsDefaultOrEmpty
            || !RequiresOuterWrapper(query))
        {
            return statement;
        }

        var generatedSpan = SourceSpan.Unknown;
        var inner = query with
        {
            Head = query.Head with { Ctes = ImmutableArray<CteDefinition>.Empty },
            OrderBy = ImmutableArray<OrderByItem>.Empty,
            Limit = null,
            Offset = null
        };

        var wildcard = new ColumnExpr(
            SqlIdentifier.Unquoted("*", generatedSpan),
            generatedSpan);
        var outer = new SelectStatement(
            query.Head.Ctes,
            Distinct: false,
            Select: ImmutableArray.Create(new SelectItem(wildcard, null, generatedSpan)),
            From: new DerivedTableSource(
                inner,
                new IdentifierPart("_set", false, generatedSpan),
                generatedSpan),
            Joins: ImmutableArray<JoinSource>.Empty,
            Where: null,
            GroupBy: ImmutableArray<SqlExpr>.Empty,
            Having: null,
            OrderBy: query.OrderBy,
            Limit: query.Limit,
            Offset: query.Offset,
            Span: query.Span);

        return outer;
    }

    private static bool RequiresOuterWrapper(QueryStatement query) =>
        !query.OrderBy.IsDefaultOrEmpty
        || query.Limit is not null
        || query.Offset is > 0;
}
