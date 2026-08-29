namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast

/// Root CTE / set-tail scope rewrite implemented in F#.
module internal FunctionalRootCteSetTailRewriter =

    let private requiresOuterWrapper
        (query: QueryStatement) =

        not query.OrderBy.IsDefaultOrEmpty
        || query.Limit.HasValue
        || (query.Offset.HasValue
            && query.Offset.Value > 0)

    let rewrite
        (statement: SqlStatement)
        : SqlStatement =

        match statement with
        | :? QueryStatement as query
            when not query.SetOperations.IsDefaultOrEmpty
                 && not query.Head.Ctes.IsDefaultOrEmpty
                 && requiresOuterWrapper query ->

            let generatedSpan =
                SourceSpan.Unknown

            let innerHead =
                SelectStatement(
                    ImmutableArray<CteDefinition>.Empty,
                    query.Head.Distinct,
                    query.Head.Select,
                    query.Head.From,
                    query.Head.Joins,
                    query.Head.Where,
                    query.Head.GroupBy,
                    query.Head.Having,
                    query.Head.OrderBy,
                    query.Head.Limit,
                    query.Head.Offset,
                    query.Head.Span)

            let inner =
                QueryStatement(
                    innerHead,
                    query.SetOperations,
                    ImmutableArray<OrderByItem>.Empty,
                    Nullable<int>(),
                    Nullable<int>(),
                    query.Span)

            let wildcard =
                ColumnExpr(
                    SqlIdentifier.Unquoted(
                        "*",
                        generatedSpan),
                    generatedSpan)

            let outer =
                SelectStatement(
                    query.Head.Ctes,
                    false,
                    ImmutableArray.Create(
                        SelectItem(
                            wildcard,
                            null,
                            generatedSpan)),
                    DerivedTableSource(
                        inner,
                        IdentifierPart(
                            "_set",
                            false,
                            generatedSpan),
                        generatedSpan),
                    ImmutableArray<JoinSource>.Empty,
                    null,
                    ImmutableArray<SqlExpr>.Empty,
                    null,
                    query.OrderBy,
                    query.Limit,
                    query.Offset,
                    query.Span)

            outer :> SqlStatement

        | _ ->
            statement
