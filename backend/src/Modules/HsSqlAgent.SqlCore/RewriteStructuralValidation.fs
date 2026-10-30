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

    // Proof-only frames: strict nested-CTE placement and no-FROM validation
    // preserve their independent scope/diagnostic rules while sharing the
    // same Query/CTE/derived traversal strategy.
    type private CteProofFrame =
        | CteProofQuery of QueryPosition * Query
        | CteProofScalarTail of QueryPosition * Query
        | CteProofSource of TableSource
        | CteProofJoin of Join
        | CteProofExpression of Expr
        | CteProofOrderTail of Query

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

    // Shared scalar visitor for structural proofs. It is deliberately
    // iterative, with explicit query boundary frames, so a left-deep
    // boolean/arithmetic tree cannot overflow the CLR stack after Binder
    // and Normalizer become iterative.
    [<Struct>]
    type private StructuralExprFrame =
        | StructuralExpr of expression: Expr * allowCountWildcard: bool
        | StructuralQuery of query: Query

    // Shared scalar edge scheduler used by both the standalone DML visitor
    // and query-wide CTE/no-FROM worklists. In particular, expression
    // subqueries enqueue Query frames instead of calling back recursively.
    let private queueStructuralChildren
        (pushExpr: bool -> Expr -> unit) (pushQuery: Query -> unit)
        allowWildcard value =
        let push = pushExpr false
        let pushExprs (values: Expr list) =
            for item in List.rev values do push item
        let pushOrders (values: OrderBy list) =
            for order in List.rev values do push order.Expression
        match value with
        | Spanned(_, inner) -> pushExpr allowWildcard inner
        | Unary(_, operand) | Cast(operand, _) | Extract(_, operand)
        | PostgresJsonAccess(operand, _, _) | IsNull(operand, _) -> push operand
        | DateAdd(_, left, right) | DateDiff(_, left, right)
        | Binary(_, left, right) | Like(left, right, _, _, _)
        | RegexMatch(left, right) | FilteredAggregate(left, right) ->
            push right
            push left
        | RawRegexCall(args, _) -> pushExprs args
        | FunctionCall call ->
            pushOrders call.AggregateOrderBy
            let args = List.toArray call.Arguments
            let name = FunctionName.value call.Name |> fun v -> v.Trim().ToUpperInvariant()
            for index = args.Length - 1 downto 0 do
                let wildcard =
                    not (FunctionName.hasQuotedParts call.Name)
                    && name = "COUNT" && index = 0
                    && (match Expr.unspan args.[index] with
                        | Wildcard None -> true
                        | _ -> false)
                pushExpr wildcard args.[index]
        | Windowed(value, window) ->
            pushOrders window.OrderBy
            pushExprs window.PartitionBy
            push value
        | SimpleCase(input, branches, fallback) ->
            fallback |> Option.iter push
            NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                push branch.Result
                push branch.Match) branches
            push input
        | SearchedCase(branches, fallback) ->
            fallback |> Option.iter push
            NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                push branch.Result
                push branch.Condition) branches
        | InList(value, items, _) ->
            NonEmpty.iterReverse push items
            push value
        | InSubquery(value, query, _) ->
            pushQuery query
            push value
        | Between(value, lower, upper, _) ->
            push upper
            push lower
            push value
        | ScalarSubquery query | Exists(query, _) -> pushQuery query
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
        | Literal _ | Interval _ -> ()

    let private walkStructuralExpression onExpr onQuery allowWildcard root =
        let pending = ResizeArray<StructuralExprFrame>()
        pending.Add(StructuralExpr(root, allowWildcard))
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | StructuralQuery query -> onQuery query
            | StructuralExpr(value, allowed) ->
                onExpr allowed value
                queueStructuralChildren
                    (fun childAllowed child ->
                        pending.Add(StructuralExpr(child, childAllowed)))
                    (fun query -> pending.Add(StructuralQuery query))
                    allowed value

    let rec private validateNestedCteExpr targetRuntime expression =
        walkStructuralExpression
            (fun _ _ -> ())
            (validateNestedCteQuery targetRuntime ScalarSubqueryPosition)
            false expression

    and private validateNestedCteTable targetRuntime source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            validateNestedCteQuery targetRuntime DerivedTablePosition query

    and private validateNestedCteQuery targetRuntime position root =
        // The legacy proof recursed through Query -> CTE / Derived / LATERAL
        // -> Query even for SELECTs containing no scalar subqueries. Keep
        // the complete source-order walk on heap frames; scalar expression
        // subqueries retain their own entry callback for now.
        let pending = ResizeArray<CteProofFrame>()
        pending.Add(CteProofQuery(position, root))
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | CteProofQuery(currentPosition, current) ->
                let select = current.Head
                validateCtePlacement targetRuntime currentPosition select.Ctes
                // Push the exact inverse of the old recursive DFS order:
                // CTEs, FROM, JOIN sources/predicates, scalar expressions,
                // local scalar SET-tail check, SET branches, ORDER BY.
                pending.Add(CteProofOrderTail current)
                for branch in List.rev current.SetOperations do
                    pending.Add(CteProofQuery(SetBranchPosition, branch.Query))
                pending.Add(CteProofScalarTail(currentPosition, current))
                select.Having |> Option.iter (CteProofExpression >> pending.Add)
                for value in List.rev select.GroupBy do
                    pending.Add(CteProofExpression value)
                select.Where |> Option.iter (CteProofExpression >> pending.Add)
                for item in List.rev select.Projection do
                    pending.Add(CteProofExpression item.Expression)
                match select.DistinctMode with
                | SelectDistinct.DistinctOn values ->
                    for value in List.rev (NonEmpty.toList values) do
                        pending.Add(CteProofExpression value)
                | _ -> ()
                for join in List.rev select.Joins do
                    pending.Add(CteProofJoin join)
                select.From |> Option.iter (CteProofSource >> pending.Add)
                for cte in List.rev select.Ctes do
                    pending.Add(CteProofQuery(CteDefinition, cte.Query))
            | CteProofSource source ->
                match source with
                | NamedTable _ | CteTable _ -> ()
                | DerivedTable(query, _) | LateralDerivedTable(query, _) ->
                    pending.Add(CteProofQuery(DerivedTablePosition, query))
            | CteProofJoin join ->
                join.Predicate |> Option.iter (CteProofExpression >> pending.Add)
                pending.Add(CteProofSource join.Source)
            | CteProofExpression expression ->
                queueStructuralChildren
                    (fun _ child -> pending.Add(CteProofExpression child))
                    (fun query -> pending.Add(CteProofQuery(ScalarSubqueryPosition, query)))
                    false expression
            | CteProofScalarTail(currentPosition, current) ->
                if currentPosition = ScalarSubqueryPosition
                   && not current.Head.Ctes.IsEmpty
                   && not current.SetOperations.IsEmpty
                   && not current.OrderBy.IsEmpty then
                    let rec portableSetTailOrder expression =
                        match expression with
                        | Spanned(_, inner) -> portableSetTailOrder inner
                        | OrderOrdinal _ -> true
                        | Column identifier
                        | BoundColumn(identifier, ProjectionAlias) ->
                            Identifier.parts identifier |> List.length = 1
                        | _ -> false
                    if current.OrderBy
                       |> List.exists (fun order -> not (portableSetTailOrder order.Expression)) then
                        cteScopeError (
                            "scalar/EXISTS subquery with a root CTE and set-operation tail can order only by an output name "
                            + "or output ordinal; rich ordering expressions would require an unproven scope barrier")
            | CteProofOrderTail current ->
                for order in List.rev current.OrderBy do
                    pending.Add(CteProofExpression order.Expression)

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
            update.Ctes |> List.iter (fun cte ->
                validateNestedCteQuery targetRuntime RootQuery cte.Query)
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

    type private NoFromProofFrame =
        | NoFromProofQuery of Query
        | NoFromProofSource of TableSource
        | NoFromProofJoin of Join
        | NoFromProofExpression of strictNoFrom: bool * allowCountWildcard: bool * expression: Expr
        | NoFromProofJoinInvariant of Select
        | NoFromProofOrderTail of Query

    let rec private validateNoFromExpression allowWildcard expression =
        walkStructuralExpression
            (fun allowed value ->
                match value with
                | Column identifier -> noFromReferenceError identifier
                | BoundColumn(_, ColumnBinding.OuterRowSource) -> ()
                | BoundColumn(identifier, _) -> noFromReferenceError identifier
                | Wildcard _ when allowed -> ()
                | Wildcard None ->
                    raise (SqlCompilationException(
                        "Column reference '*' requires a FROM source in the portable Core query model."))
                | Wildcard(Some identifier) -> noFromReferenceError identifier
                | _ -> ())
            validateNoFromQuery allowWildcard expression

    and private visitNestedNoFromExpression expression =
        walkStructuralExpression
            (fun _ _ -> ())
            validateNoFromQuery false expression

    and private validateNoFromSource source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            validateNoFromQuery query

    and private validateNoFromQuery root =
        // Validate the same proof sequence as the recursive implementation:
        // CTEs, FROM, JOIN source/predicate, no-FROM invariant, then
        // DISTINCT/projection/WHERE/GROUP/HAVING, SET branches and tail.
        let pending = ResizeArray<NoFromProofFrame>()
        pending.Add(NoFromProofQuery root)
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | NoFromProofQuery current ->
                let select = current.Head
                pending.Add(NoFromProofOrderTail current)
                for branch in List.rev current.SetOperations do
                    pending.Add(NoFromProofQuery branch.Query)
                let strict = select.From.IsNone
                select.Having |> Option.iter (fun value ->
                    pending.Add(NoFromProofExpression(strict, false, value)))
                for value in List.rev select.GroupBy do
                    pending.Add(NoFromProofExpression(strict, false, value))
                select.Where |> Option.iter (fun value ->
                    pending.Add(NoFromProofExpression(strict, false, value)))
                for item in List.rev select.Projection do
                    pending.Add(NoFromProofExpression(strict, false, item.Expression))
                match select.DistinctMode with
                | SelectDistinct.DistinctOn expressions ->
                    for value in List.rev (NonEmpty.toList expressions) do
                        pending.Add(NoFromProofExpression(strict, false, value))
                | _ -> ()
                pending.Add(NoFromProofJoinInvariant select)
                for join in List.rev select.Joins do
                    pending.Add(NoFromProofJoin join)
                select.From |> Option.iter (NoFromProofSource >> pending.Add)
                for cte in List.rev select.Ctes do
                    pending.Add(NoFromProofQuery cte.Query)
            | NoFromProofSource source ->
                match source with
                | NamedTable _ | CteTable _ -> ()
                | DerivedTable(query, _) | LateralDerivedTable(query, _) ->
                    pending.Add(NoFromProofQuery query)
            | NoFromProofJoin join ->
                join.Predicate |> Option.iter (fun value ->
                    pending.Add(NoFromProofExpression(false, false, value)))
                pending.Add(NoFromProofSource join.Source)
            | NoFromProofJoinInvariant select ->
                if select.From.IsNone && not select.Joins.IsEmpty then
                    raise (SqlCompilationException(
                        "A Core SELECT cannot contain JOIN sources without a primary FROM source."))
            | NoFromProofExpression(strict, allowed, expression) ->
                if strict then
                    match expression with
                    | Column identifier -> noFromReferenceError identifier
                    | BoundColumn(_, ColumnBinding.OuterRowSource) -> ()
                    | BoundColumn(identifier, _) -> noFromReferenceError identifier
                    | Wildcard _ when allowed -> ()
                    | Wildcard None ->
                        raise (SqlCompilationException(
                            "Column reference '*' requires a FROM source in the portable Core query model."))
                    | Wildcard(Some identifier) -> noFromReferenceError identifier
                    | _ -> ()
                queueStructuralChildren
                    (fun childAllowed child ->
                        pending.Add(NoFromProofExpression(strict, childAllowed, child)))
                    (fun query -> pending.Add(NoFromProofQuery query))
                    allowed expression
            | NoFromProofOrderTail current ->
                for order in List.rev current.OrderBy do
                    match Expr.unspan order.Expression with
                    | BoundColumn(_, ColumnBinding.ProjectionAlias) -> ()
                    | _ ->
                        pending.Add(NoFromProofExpression(
                            current.Head.From.IsNone, false, order.Expression))

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


