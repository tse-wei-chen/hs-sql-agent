namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteInspection =

    let private queryFactsDataKey = "HsSqlAgent.SqlCore.QueryFacts"

    let attachToException (facts: QueryFacts) (ex: exn) =
        ex.Data[queryFactsDataKey] <- facts

    let tryFromException (ex: exn) =
        match ex.Data[queryFactsDataKey] with
        | :? QueryFacts as facts -> Some facts
        | _ -> None

    type State =
        { Tables: HashSet<string>
          Aliases: ResizeArray<QueryAliasFact>
          mutable ContainsSubquery: bool
          mutable ContainsCte: bool
          mutable NextScopeId: int }

    let identifierText = CoreModel.Identifier.text

    let newState () =
        { Tables = HashSet<string>(StringComparer.OrdinalIgnoreCase)
          Aliases = ResizeArray<QueryAliasFact>()
          ContainsSubquery = false
          ContainsCte = false
          NextScopeId = 0 }

    let scopeId (state: State) =
        let value = state.NextScopeId
        state.NextScopeId <- value + 1
        value

    let registerAlias (state: State) scope target (alias: CoreModel.IdentifierPart option) =
        match alias with
        | Some value when not (String.IsNullOrWhiteSpace(value.Value)) ->
            state.Aliases.Add(QueryAliasFact(value.Value, target, scope))
        | _ -> ()

    // Query Facts is a public inspection boundary. It must not reintroduce
    // Query -> Source/Expr -> Query CLR recursion after the compiler has
    // already proven the corresponding SQL safe on a constrained stack.
    // Keep the historic depth-first order so scope IDs, alias facts and
    // first-seen table ordering remain unchanged.
    type private InspectionFrame =
        | VisitQuery of Query
        | VisitSelect of Select
        | VisitSource of int * TableSource
        | VisitJoin of int * Join
        | VisitExpr of Expr

    let inspectDocument (document: Document) =
        let state = newState ()
        let pending = ResizeArray<InspectionFrame>()
        let pushExpr value = pending.Add(VisitExpr value)
        let pushExprs values =
            for value in List.rev values do pushExpr value
        let pushReturning (items: ReturningItem list) =
            for item in List.rev items do pushExpr item.Expression

        // Seed the stack in reverse evaluation order. Each frame schedules
        // children in reverse so the resulting walk is identical to the
        // former recursive visitor, including projection and JOIN order.
        match document.Statement with
        | Statement.QueryStatement query ->
            pending.Add(VisitQuery query)
        | Statement.InsertStatement insert ->
            state.Tables.Add(identifierText insert.Target) |> ignore
            pushReturning insert.Returning
            match insert.Input with
            | InsertInput.QuerySource query ->
                state.ContainsSubquery <- true
                pending.Add(VisitQuery query)
            | InsertInput.Values rows ->
                for row in List.rev (NonEmpty.toList rows) do
                    pushExprs (NonEmpty.toList row)
            | InsertInput.DefaultValues -> ()
        | Statement.UpdateStatement update ->
            state.Tables.Add(identifierText update.Target) |> ignore
            let scope = scopeId state
            pushReturning update.Returning
            update.Where |> Option.iter pushExpr
            for assignment in List.rev update.Assignments do
                pushExpr assignment.Value
            for source in List.rev update.From do
                pending.Add(VisitSource(scope, source))
            if not update.Ctes.IsEmpty then
                state.ContainsCte <- true
                state.ContainsSubquery <- true
            for cte in List.rev update.Ctes do
                pending.Add(VisitQuery cte.Query)
        | Statement.DeleteStatement delete ->
            state.Tables.Add(identifierText delete.Target) |> ignore
            let scope = scopeId state
            pushReturning delete.Returning
            delete.Where |> Option.iter pushExpr
            for source in List.rev delete.Using do
                pending.Add(VisitSource(scope, source))
            if not delete.Ctes.IsEmpty then
                state.ContainsCte <- true
                state.ContainsSubquery <- true
            for cte in List.rev delete.Ctes do
                pending.Add(VisitQuery cte.Query)
        | Statement.MergeStatement merge ->
            state.Tables.Add(identifierText merge.Target) |> ignore
            merge.NotMatched |> Option.iter (fun value ->
                pushExprs (NonEmpty.toList value.InsertValues))
            merge.Matched |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    for item in List.rev (NonEmpty.toList assignments) do
                        pushExpr item.Value)
            pushExpr merge.MatchPredicate
            pushExprs (NonEmpty.toList merge.Source.SourceValues)

        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | VisitQuery query ->
                for order in List.rev query.OrderBy do
                    pushExpr order.Expression
                for branch in List.rev query.SetOperations do
                    pending.Add(VisitQuery branch.Query)
                pending.Add(VisitSelect query.Head)
            | VisitSelect select ->
                let scope = scopeId state
                if not select.Ctes.IsEmpty then state.ContainsCte <- true
                select.Having |> Option.iter pushExpr
                pushExprs select.GroupBy
                select.Where |> Option.iter pushExpr
                for item in List.rev select.Projection do
                    pushExpr item.Expression
                match select.DistinctMode with
                | SelectDistinct.DistinctOn expressions ->
                    pushExprs (NonEmpty.toList expressions)
                | SelectDistinct.AllRows
                | SelectDistinct.DistinctRows -> ()
                for join in List.rev select.Joins do
                    pending.Add(VisitJoin(scope, join))
                select.From |> Option.iter (fun source ->
                    pending.Add(VisitSource(scope, source)))
                for cte in List.rev select.Ctes do
                    pending.Add(VisitQuery cte.Query)
            | VisitSource(scope, source) ->
                match source with
                | NamedTable(name, alias) ->
                    let target = identifierText name
                    state.Tables.Add(target) |> ignore
                    registerAlias state scope target alias
                | CteTable(name, alias) ->
                    registerAlias state scope (identifierText name) alias
                | DerivedTable(query, alias)
                | LateralDerivedTable(query, alias) ->
                    state.ContainsSubquery <- true
                    registerAlias state scope "<subquery>" (Some alias)
                    pending.Add(VisitQuery query)
            | VisitJoin(scope, join) ->
                join.Predicate |> Option.iter pushExpr
                pending.Add(VisitSource(scope, join.Source))
            | VisitExpr expression ->
                match expression with
                | Spanned(_, inner) -> pushExpr inner
                | Column _
                | BoundColumn _
                | Wildcard _
                | OrderOrdinal _
                | Literal _
                | Interval _ -> ()
                | DateAdd(_, amount, value)
                | DateDiff(_, amount, value)
                | Binary(_, amount, value)
                | Like(amount, value, _, _, _)
                | RegexMatch(amount, value) ->
                    pushExpr value
                    pushExpr amount
                | Unary(_, value)
                | PostgresJsonAccess(value, _, _)
                | Cast(value, _)
                | Extract(_, value)
                | IsNull(value, _) ->
                    pushExpr value
                | RawRegexCall(arguments, _) ->
                    pushExprs arguments
                | FunctionCall call ->
                    for order in List.rev call.AggregateOrderBy do
                        pushExpr order.Expression
                    pushExprs call.Arguments
                | FilteredAggregate(value, predicate) ->
                    pushExpr predicate
                    pushExpr value
                | Windowed(value, window) ->
                    for order in List.rev window.OrderBy do
                        pushExpr order.Expression
                    pushExprs window.PartitionBy
                    pushExpr value
                | SimpleCase(input, branches, fallback) ->
                    fallback |> Option.iter pushExpr
                    for branch in List.rev (NonEmpty.toList branches) do
                        pushExpr branch.Result
                        pushExpr branch.Match
                    pushExpr input
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter pushExpr
                    for branch in List.rev (NonEmpty.toList branches) do
                        pushExpr branch.Result
                        pushExpr branch.Condition
                | InList(value, items, _) ->
                    pushExprs (NonEmpty.toList items)
                    pushExpr value
                | InSubquery(value, query, _) ->
                    state.ContainsSubquery <- true
                    pending.Add(VisitQuery query)
                    pushExpr value
                | Between(value, lower, upper, _) ->
                    pushExpr upper
                    pushExpr lower
                    pushExpr value
                | ScalarSubquery query
                | Exists(query, _) ->
                    state.ContainsSubquery <- true
                    pending.Add(VisitQuery query)

        QueryFacts(
            state.Tables.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            state.Aliases.ToImmutableArray(),
            state.ContainsSubquery,
            state.ContainsCte)

    let inspectBound bound =
        bound
        |> Bound.value
        |> inspectDocument
