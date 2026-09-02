namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Structural target-plan validation that is independent of semantic function/type rules.
/// Nested CTE placement and no-FROM reference invariants are checked here before semantic
/// validation so RewriteStages can focus on stage sequencing and diagnostic ownership.
module internal RewriteStructuralValidation =

    let private iterDistinctOn action (select: Select) =
        match select.DistinctMode with
        | SelectDistinct.DistinctOn expressions -> expressions |> NonEmpty.iter action
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()

    type private QueryPosition =
        | RootQuery
        | InsertSelectSource
        | CteDefinition
        | DerivedTablePosition
        | SetBranchPosition
        | ScalarSubqueryPosition

    let private cteScopeError detail =
        raise (SqlCompilationException(
            "SQL capability 'select.cte_scope' is not supported by the native SQL backend: " + detail + "."))

    let private nestedCteSupported targetRuntime =
        SqlNestedCteCapabilityRules.SupportsTarget(TargetRuntime.provider targetRuntime)

    let private validateCtePlacement targetRuntime position (ctes: Cte list) =
        if not ctes.IsEmpty && not (nestedCteSupported targetRuntime) then
            match position with
            | RootQuery | InsertSelectSource -> ()
            | CteDefinition ->
                cteScopeError (
                    "provider " + string (TargetRuntime.provider targetRuntime)
                    + " has no declared portable nested-WITH-inside-a-CTE-definition contract")
            | DerivedTablePosition ->
                cteScopeError (
                    "provider " + string (TargetRuntime.provider targetRuntime)
                    + " has no declared portable WITH-in-derived-table lowering contract")
            | SetBranchPosition ->
                cteScopeError (
                    "provider " + string (TargetRuntime.provider targetRuntime)
                    + " has no declared portable WITH-in-set-operation-branch lowering contract")
            | ScalarSubqueryPosition ->
                cteScopeError (
                    "provider " + string (TargetRuntime.provider targetRuntime)
                    + " has no declared portable WITH-at-the-root-of-a-scalar/EXISTS-subquery contract")

    let rec private validateNestedCteExpr targetRuntime expression =
        match expression with
        | Spanned(_, inner) -> validateNestedCteExpr targetRuntime inner
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | DateAdd(_, amount, value)
        | DateDiff(_, amount, value) ->
            validateNestedCteExpr targetRuntime amount
            validateNestedCteExpr targetRuntime value
        | Unary(_, operand) -> validateNestedCteExpr targetRuntime operand
        | Binary(_, left, right) ->
            validateNestedCteExpr targetRuntime left
            validateNestedCteExpr targetRuntime right
        | Like(value, pattern, _, _, _) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteExpr targetRuntime pattern
        | RawRegexCall(arguments, _) -> arguments |> List.iter (validateNestedCteExpr targetRuntime)
        | RegexMatch(value, pattern) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteExpr targetRuntime pattern
        | PostgresJsonAccess(value, _, _) ->
            validateNestedCteExpr targetRuntime value
        | FunctionCall call ->
            call.Arguments |> List.iter (validateNestedCteExpr targetRuntime)
            call.AggregateOrderBy |> List.iter (fun order -> validateNestedCteExpr targetRuntime order.Expression)
        | FilteredAggregate(value, predicate) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteExpr targetRuntime predicate
        | Windowed(value, window) ->
            validateNestedCteExpr targetRuntime value
            window.PartitionBy |> List.iter (validateNestedCteExpr targetRuntime)
            window.OrderBy |> List.iter (fun order -> validateNestedCteExpr targetRuntime order.Expression)
        | Cast(value, _) | Extract(_, value) -> validateNestedCteExpr targetRuntime value
        | SimpleCase(input, branches, fallback) ->
            validateNestedCteExpr targetRuntime input
            branches |> NonEmpty.iter (fun branch ->
                validateNestedCteExpr targetRuntime branch.Match
                validateNestedCteExpr targetRuntime branch.Result)
            fallback |> Option.iter (validateNestedCteExpr targetRuntime)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                validateNestedCteExpr targetRuntime branch.Condition
                validateNestedCteExpr targetRuntime branch.Result)
            fallback |> Option.iter (validateNestedCteExpr targetRuntime)
        | InList(value, items, _) ->
            validateNestedCteExpr targetRuntime value
            items |> NonEmpty.iter (validateNestedCteExpr targetRuntime)
        | InSubquery(value, query, _) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteQuery targetRuntime ScalarSubqueryPosition query
        | Between(value, lower, upper, _) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteExpr targetRuntime lower
            validateNestedCteExpr targetRuntime upper
        | IsNull(value, _) -> validateNestedCteExpr targetRuntime value
        | ScalarSubquery query | Exists(query, _) ->
            validateNestedCteQuery targetRuntime ScalarSubqueryPosition query

    and private validateNestedCteTable targetRuntime source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            validateNestedCteQuery targetRuntime DerivedTablePosition query

    and private validateNestedCteSelect targetRuntime position select =
        validateCtePlacement targetRuntime position select.Ctes
        select.Ctes |> List.iter (fun cte ->
            validateNestedCteQuery targetRuntime CteDefinition cte.Query)
        select.From |> Option.iter (validateNestedCteTable targetRuntime)
        select.Joins |> List.iter (fun join ->
            validateNestedCteTable targetRuntime join.Source
            join.Predicate |> Option.iter (validateNestedCteExpr targetRuntime))
        iterDistinctOn (validateNestedCteExpr targetRuntime) select
        select.Projection |> List.iter (fun item -> validateNestedCteExpr targetRuntime item.Expression)
        select.Where |> Option.iter (validateNestedCteExpr targetRuntime)
        select.GroupBy |> List.iter (validateNestedCteExpr targetRuntime)
        select.Having |> Option.iter (validateNestedCteExpr targetRuntime)

    and private validateNestedCteQuery targetRuntime position query =
        validateNestedCteSelect targetRuntime position query.Head

        if position = ScalarSubqueryPosition
           && not query.Head.Ctes.IsEmpty
           && not query.SetOperations.IsEmpty
           && not query.OrderBy.IsEmpty then
            let rec portableSetTailOrder expression =
                match expression with
                | Spanned(_, inner) -> portableSetTailOrder inner
                | OrderOrdinal _ -> true
                | Column identifier
                | BoundColumn(identifier, ProjectionAlias) ->
                    Identifier.parts identifier |> List.length = 1
                | _ -> false
            if query.OrderBy
               |> List.exists (fun order -> not (portableSetTailOrder order.Expression)) then
                cteScopeError (
                    "scalar/EXISTS subquery with a root CTE and set-operation tail can order only by an output name "
                    + "or output ordinal; rich ordering expressions would require an unproven scope barrier")

        query.SetOperations |> List.iter (fun branch ->
            validateNestedCteQuery targetRuntime SetBranchPosition branch.Query)
        query.OrderBy |> List.iter (fun order -> validateNestedCteExpr targetRuntime order.Expression)

    let validateNestedCteDocument targetRuntime document =
        match document.Statement with
        | QueryStatement query -> validateNestedCteQuery targetRuntime RootQuery query
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query -> validateNestedCteQuery targetRuntime InsertSelectSource query
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (validateNestedCteExpr targetRuntime))
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> validateNestedCteExpr targetRuntime item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun item -> validateNestedCteExpr targetRuntime item.Value)
            update.From |> List.iter (validateNestedCteTable targetRuntime)
            update.Where |> Option.iter (validateNestedCteExpr targetRuntime)
            update.Returning |> List.iter (fun item -> validateNestedCteExpr targetRuntime item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (validateNestedCteTable targetRuntime)
            delete.Where |> Option.iter (validateNestedCteExpr targetRuntime)
            delete.Returning |> List.iter (fun item -> validateNestedCteExpr targetRuntime item.Expression)
        | MergeStatement merge ->
            merge.Source.SourceValues |> NonEmpty.iter (validateNestedCteExpr targetRuntime)
            validateNestedCteExpr targetRuntime merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> validateNestedCteExpr targetRuntime item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (validateNestedCteExpr targetRuntime))


    let private noFromReferenceError identifier =
        raise (SqlCompilationException(
            "Column reference '" + Identifier.text identifier
            + "' requires a FROM source in the portable Core query model."))

    let rec private validateNoFromExpression allowWildcard expression =
        match expression with
        | Spanned(_, inner) -> validateNoFromExpression allowWildcard inner
        | Literal _ | Interval _ | OrderOrdinal _ -> ()
        | DateAdd(_, amount, value)
        | DateDiff(_, amount, value) ->
            validateNoFromExpression false amount
            validateNoFromExpression false value
        | Column identifier ->
            noFromReferenceError identifier
        | BoundColumn(_, ColumnBinding.OuterRowSource) ->
            ()
        | BoundColumn(identifier, _) ->
            noFromReferenceError identifier
        | Wildcard _ when allowWildcard -> ()
        | Wildcard None ->
            raise (SqlCompilationException(
                "Column reference '*' requires a FROM source in the portable Core query model."))
        | Wildcard(Some identifier) ->
            noFromReferenceError identifier
        | Unary(_, operand) ->
            validateNoFromExpression false operand
        | Binary(_, left, right) ->
            validateNoFromExpression false left
            validateNoFromExpression false right
        | Like(value, pattern, _, _, _) ->
            validateNoFromExpression false value
            validateNoFromExpression false pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (validateNoFromExpression false)
        | RegexMatch(value, pattern) ->
            validateNoFromExpression false value
            validateNoFromExpression false pattern
        | PostgresJsonAccess(value, _, _) ->
            validateNoFromExpression false value
        | FunctionCall call ->
            let name =
                FunctionName.value call.Name
                |> fun value -> value.Trim().ToUpperInvariant()
            call.Arguments
            |> List.iteri (fun index argument ->
                let allowFunctionWildcard =
                    not (FunctionName.hasQuotedParts call.Name)
                    && name = "COUNT"
                    && index = 0
                    && (match Expr.unspan argument with
                        | Wildcard None -> true
                        | _ -> false)
                validateNoFromExpression allowFunctionWildcard argument)
            call.AggregateOrderBy
            |> List.iter (fun order ->
                validateNoFromExpression false order.Expression)
        | FilteredAggregate(value, predicate) ->
            validateNoFromExpression false value
            validateNoFromExpression false predicate
        | Windowed(value, window) ->
            validateNoFromExpression false value
            window.PartitionBy |> List.iter (validateNoFromExpression false)
            window.OrderBy
            |> List.iter (fun order ->
                validateNoFromExpression false order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            validateNoFromExpression false value
        | SimpleCase(input, branches, fallback) ->
            validateNoFromExpression false input
            branches
            |> NonEmpty.iter (fun branch ->
                validateNoFromExpression false branch.Match
                validateNoFromExpression false branch.Result)
            fallback |> Option.iter (validateNoFromExpression false)
        | SearchedCase(branches, fallback) ->
            branches
            |> NonEmpty.iter (fun branch ->
                validateNoFromExpression false branch.Condition
                validateNoFromExpression false branch.Result)
            fallback |> Option.iter (validateNoFromExpression false)
        | InList(value, items, _) ->
            validateNoFromExpression false value
            items |> NonEmpty.iter (validateNoFromExpression false)
        | InSubquery(value, query, _) ->
            validateNoFromExpression false value
            validateNoFromQuery query
        | Between(value, lower, upper, _) ->
            validateNoFromExpression false value
            validateNoFromExpression false lower
            validateNoFromExpression false upper
        | IsNull(value, _) ->
            validateNoFromExpression false value
        | ScalarSubquery query | Exists(query, _) ->
            validateNoFromQuery query

    and private visitNestedNoFromExpression expression =
        match expression with
        | Spanned(_, inner) -> visitNestedNoFromExpression inner
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | DateAdd(_, amount, value)
        | DateDiff(_, amount, value) ->
            visitNestedNoFromExpression amount
            visitNestedNoFromExpression value
        | Unary(_, operand) ->
            visitNestedNoFromExpression operand
        | Binary(_, left, right) ->
            visitNestedNoFromExpression left
            visitNestedNoFromExpression right
        | Like(value, pattern, _, _, _)
        | RegexMatch(value, pattern) ->
            visitNestedNoFromExpression value
            visitNestedNoFromExpression pattern
        | PostgresJsonAccess(value, _, _) ->
            visitNestedNoFromExpression value
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter visitNestedNoFromExpression
        | FunctionCall call ->
            call.Arguments |> List.iter visitNestedNoFromExpression
            call.AggregateOrderBy
            |> List.iter (fun order ->
                visitNestedNoFromExpression order.Expression)
        | FilteredAggregate(value, predicate) ->
            visitNestedNoFromExpression value
            visitNestedNoFromExpression predicate
        | Windowed(value, window) ->
            visitNestedNoFromExpression value
            window.PartitionBy |> List.iter visitNestedNoFromExpression
            window.OrderBy
            |> List.iter (fun order ->
                visitNestedNoFromExpression order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            visitNestedNoFromExpression value
        | SimpleCase(input, branches, fallback) ->
            visitNestedNoFromExpression input
            branches
            |> NonEmpty.iter (fun branch ->
                visitNestedNoFromExpression branch.Match
                visitNestedNoFromExpression branch.Result)
            fallback |> Option.iter visitNestedNoFromExpression
        | SearchedCase(branches, fallback) ->
            branches
            |> NonEmpty.iter (fun branch ->
                visitNestedNoFromExpression branch.Condition
                visitNestedNoFromExpression branch.Result)
            fallback |> Option.iter visitNestedNoFromExpression
        | InList(value, items, _) ->
            visitNestedNoFromExpression value
            items |> NonEmpty.iter visitNestedNoFromExpression
        | InSubquery(value, query, _) ->
            visitNestedNoFromExpression value
            validateNoFromQuery query
        | Between(value, lower, upper, _) ->
            visitNestedNoFromExpression value
            visitNestedNoFromExpression lower
            visitNestedNoFromExpression upper
        | IsNull(value, _) ->
            visitNestedNoFromExpression value
        | ScalarSubquery query | Exists(query, _) ->
            validateNoFromQuery query

    and private validateNoFromSource source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            validateNoFromQuery query

    and private validateNoFromSelect select =
        select.Ctes
        |> List.iter (fun cte ->
            validateNoFromQuery cte.Query)
        select.From |> Option.iter validateNoFromSource
        select.Joins
        |> List.iter (fun join ->
            validateNoFromSource join.Source
            join.Predicate |> Option.iter visitNestedNoFromExpression)

        match select.From with
        | Some _ ->
            iterDistinctOn visitNestedNoFromExpression select
            select.Projection
            |> List.iter (fun item ->
                visitNestedNoFromExpression item.Expression)
            select.Where |> Option.iter visitNestedNoFromExpression
            select.GroupBy |> List.iter visitNestedNoFromExpression
            select.Having |> Option.iter visitNestedNoFromExpression
        | None ->
            if not select.Joins.IsEmpty then
                raise (SqlCompilationException(
                    "A Core SELECT cannot contain JOIN sources without a primary FROM source."))
            iterDistinctOn (validateNoFromExpression false) select
            select.Projection
            |> List.iter (fun item ->
                validateNoFromExpression false item.Expression)
            select.Where |> Option.iter (validateNoFromExpression false)
            select.GroupBy |> List.iter (validateNoFromExpression false)
            select.Having |> Option.iter (validateNoFromExpression false)

    and private validateNoFromQuery query =
        validateNoFromSelect query.Head
        query.SetOperations
        |> List.iter (fun branch ->
            validateNoFromQuery branch.Query)
        query.OrderBy
        |> List.iter (fun order ->
            match Expr.unspan order.Expression with
            | BoundColumn(_, ColumnBinding.ProjectionAlias) -> ()
            | _ ->
                if query.Head.From.IsNone then
                    validateNoFromExpression false order.Expression
                else
                    visitNestedNoFromExpression order.Expression)

    let validateNoFromDocument targetRuntime document =
        let _ = targetRuntime
        match document.Statement with
        | QueryStatement query ->
            validateNoFromQuery query
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query ->
                validateNoFromQuery query
            | Values rows ->
                rows
                |> NonEmpty.iter (NonEmpty.iter visitNestedNoFromExpression)
            | DefaultValues -> ()
            insert.Returning
            |> List.iter (fun item ->
                visitNestedNoFromExpression item.Expression)
        | UpdateStatement update ->
            update.From |> List.iter validateNoFromSource
            update.AssignmentItems
            |> NonEmpty.iter (fun item ->
                visitNestedNoFromExpression item.Value)
            update.Where |> Option.iter visitNestedNoFromExpression
            update.Returning
            |> List.iter (fun item ->
                visitNestedNoFromExpression item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter validateNoFromSource
            delete.Where |> Option.iter visitNestedNoFromExpression
            delete.Returning
            |> List.iter (fun item ->
                visitNestedNoFromExpression item.Expression)
        | MergeStatement merge ->
            if NonEmpty.length merge.Source.SourceColumns <> NonEmpty.length merge.Source.SourceValues then
                raise (SqlCompilationException(
                    "MERGE source column aliases must match the single VALUES row width exactly."))
            if merge.Matched.IsNone && merge.NotMatched.IsNone then
                raise (SqlCompilationException("MERGE requires at least one WHEN action."))
            merge.Source.SourceValues |> NonEmpty.iter (validateNoFromExpression false)
            visitNestedNoFromExpression merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> visitNestedNoFromExpression item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter visitNestedNoFromExpression)


