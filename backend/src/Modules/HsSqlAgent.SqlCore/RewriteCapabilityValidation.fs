namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Capability-proof traversal over the normalized closed F# AST.
/// This module proves expression/filter/target capabilities only; stage ordering and
/// diagnostic wrapping remain owned by RewriteStages.
module internal RewriteCapabilityValidation =

    let private targetCapabilityMessage =
        RewriteCapabilityProvenance.targetMessage "target capability validation"

    let private iterDistinctOn action (select: Select) =
        match select.DistinctMode with
        | SelectDistinct.DistinctOn expressions -> expressions |> NonEmpty.iter action
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()

    let private proveTargetLiteral targetRuntime (proofs: ExpressionProofs) value =
        let requireProof proof =
            match proof with
            | ProvenCapability -> ()
            | RejectedCapability rejection ->
                raise (SqlCompilationException(targetCapabilityMessage rejection))
        match targetRuntime, value with
        | FirebirdRuntime, ScalarValue.Text text when text.Length > 8191 ->
            raise (SqlCompilationException(
                "Firebird string literal exceeds the safe UTF8 VARCHAR limit of 8191 characters."))
        | _, ScalarValue.OffsetDateTime _ ->
            requireProof proofs.OffsetTimestamp
        | _, ScalarValue.Time _ ->
            requireProof proofs.StandaloneTime
        | FirebirdRuntime, ScalarValue.Decimal value ->
            let shape = SqlFirebirdDecimalCapabilityRules.Shape(value)
            if shape.Precision > SqlFirebirdDecimalCapabilityRules.LegacyMaximumPrecision then
                match proofs.FirebirdExtendedDecimal with
                | ProvenCapability -> ()
                | RejectedCapability _ ->
                    raise (SqlCompilationException(
                        "SQL capability 'numeric.decimal_extended' requires an explicit Firebird target "
                        + "capability profile with ServerVersion 4.0 or newer for exact decimal precision "
                        + "above 18; this value requires "
                        + SqlFirebirdDecimalCapabilityRules.FirebirdCastType(value)
                        + "."))
        | _ -> ()

    let private proveSqlServerConcat targetRuntime =
        match targetRuntime with
        | SqlServerRuntime(Proven _) -> ()
        | SqlServerRuntime(Unproven message) -> invalidOp message
        | _ -> ()

    let private requireExpressionCapability = function
        | ProvenCapability -> ()
        | RejectedCapability rejection -> invalidOp (targetCapabilityMessage rejection)

    let private requireFilterCapability capabilityMessage = function
        | ProvenCapability -> ()
        | RejectedCapability rejection ->
            raise (SqlCompilationException(capabilityMessage rejection))

    let rec private proveFilterPredicate capabilityMessage (proofs: FilterPredicateProofs) expression =
        match expression with
        | Spanned(_, inner) -> proveFilterPredicate capabilityMessage proofs inner
        | BoundColumn(_, OuterRowSource) ->
            requireFilterCapability capabilityMessage proofs.OuterReference
        | Column _
        | BoundColumn(_, LocalRowSource)
        | BoundColumn(_, ProjectionAlias)
        | Wildcard _
        | OrderOrdinal _
        | Literal _
        | Interval _ -> ()
        | DateAdd(_, amount, value)
        | DateDiff(_, amount, value) ->
            proveFilterPredicate capabilityMessage proofs amount
            proveFilterPredicate capabilityMessage proofs value
        | Unary(_, operand) ->
            proveFilterPredicate capabilityMessage proofs operand
        | Binary(_, left, right) ->
            proveFilterPredicate capabilityMessage proofs left
            proveFilterPredicate capabilityMessage proofs right
        | Like(value, pattern, _, _, _) ->
            proveFilterPredicate capabilityMessage proofs value
            proveFilterPredicate capabilityMessage proofs pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (proveFilterPredicate capabilityMessage proofs)
        | RegexMatch(value, pattern) ->
            proveFilterPredicate capabilityMessage proofs value
            proveFilterPredicate capabilityMessage proofs pattern
        | PostgresJsonAccess(value, _, _) ->
            proveFilterPredicate capabilityMessage proofs value
        | FunctionCall call ->
            call.Arguments |> List.iter (proveFilterPredicate capabilityMessage proofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveFilterPredicate capabilityMessage proofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            proveFilterPredicate capabilityMessage proofs value
            proveFilterPredicate capabilityMessage proofs predicate
        | Windowed(value, window) ->
            requireFilterCapability capabilityMessage proofs.WindowFunction
            proveFilterPredicate capabilityMessage proofs value
            window.PartitionBy |> List.iter (proveFilterPredicate capabilityMessage proofs)
            window.OrderBy |> List.iter (fun order -> proveFilterPredicate capabilityMessage proofs order.Expression)
        | Cast(value, _)
        | Extract(_, value) ->
            proveFilterPredicate capabilityMessage proofs value
        | SimpleCase(input, branches, fallback) ->
            proveFilterPredicate capabilityMessage proofs input
            branches |> NonEmpty.iter (fun branch ->
                proveFilterPredicate capabilityMessage proofs branch.Match
                proveFilterPredicate capabilityMessage proofs branch.Result)
            fallback |> Option.iter (proveFilterPredicate capabilityMessage proofs)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveFilterPredicate capabilityMessage proofs branch.Condition
                proveFilterPredicate capabilityMessage proofs branch.Result)
            fallback |> Option.iter (proveFilterPredicate capabilityMessage proofs)
        | InList(value, items, _) ->
            proveFilterPredicate capabilityMessage proofs value
            items |> NonEmpty.iter (proveFilterPredicate capabilityMessage proofs)
        | InSubquery(value, _, _) ->
            proveFilterPredicate capabilityMessage proofs value
            requireFilterCapability capabilityMessage proofs.Subquery
        | Between(value, lower, upper, _) ->
            proveFilterPredicate capabilityMessage proofs value
            proveFilterPredicate capabilityMessage proofs lower
            proveFilterPredicate capabilityMessage proofs upper
        | IsNull(value, _) ->
            proveFilterPredicate capabilityMessage proofs value
        | ScalarSubquery _
        | Exists _ ->
            requireFilterCapability capabilityMessage proofs.Subquery

    // Source-order scalar proof traversal. Query callbacks remain scoped to
    // the owning capability pass, but no Expr branch consumes a CLR call
    // frame proportional to AST nesting depth.
    [<Struct>]
    type private CapabilityExprFrame =
        | CapabilityExpr of value: Expr
        | CapabilityQuery of body: Query

    let private walkCapabilityExpr onExpr onQuery root =
        let pending = ResizeArray<CapabilityExprFrame>()
        let inline push value = pending.Add(CapabilityExpr value)
        let pushExprs (values: Expr list) =
            let entries = List.toArray values
            for index = entries.Length - 1 downto 0 do
                push entries.[index]
        let pushOrders (orders: OrderBy list) =
            let entries: OrderBy array = List.toArray orders
            for index = entries.Length - 1 downto 0 do
                push entries.[index].Expression
        push root
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last
            match frame with
            | CapabilityQuery query -> onQuery query
            | CapabilityExpr expression ->
                onExpr expression
                match expression with
                | Spanned(_, inner) -> push inner
                | DateAdd(_, left, right) | DateDiff(_, left, right)
                | Binary(_, left, right) | Like(left, right, _, _, _)
                | RegexMatch(left, right) | FilteredAggregate(left, right) ->
                    push right
                    push left
                | Unary(_, inner) | Cast(inner, _) | Extract(_, inner)
                | PostgresJsonAccess(inner, _, _) | IsNull(inner, _) ->
                    push inner
                | RawRegexCall(args, _) -> pushExprs args
                | FunctionCall call ->
                    pushOrders call.AggregateOrderBy
                    pushExprs call.Arguments
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
                    pending.Add(CapabilityQuery query)
                    push value
                | Between(value, lower, upper, _) ->
                    push upper
                    push lower
                    push value
                | ScalarSubquery query | Exists(query, _) ->
                    pending.Add(CapabilityQuery query)
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ -> ()

    let rec private proveFilterExpr capabilityMessage (expressionProofs: ExpressionProofs) expression =
        let validate node =
            match node with
            | Interval _ ->
                requireFilterCapability capabilityMessage expressionProofs.IntervalLiteral
            | Binary((BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom), _, _) ->
                requireFilterCapability capabilityMessage expressionProofs.DistinctFrom
            | Like(_, _, _, _, true) ->
                requireFilterCapability capabilityMessage expressionProofs.ILike
            | FunctionCall call ->
                if FunctionName.hasQuotedParts call.Name then
                    requireFilterCapability capabilityMessage expressionProofs.QuotedFunction
                if FunctionName.isQualified call.Name then
                    requireFilterCapability capabilityMessage expressionProofs.QualifiedFunction
            | FilteredAggregate(_, predicate) ->
                requireFilterCapability capabilityMessage expressionProofs.AggregateFilter
                // Predicate-specific proof precedes the ordinary value walk,
                // exactly as in the original recursive validation.
                proveFilterPredicate capabilityMessage expressionProofs.FilterPredicate predicate
            | _ -> ()
        walkCapabilityExpr validate
            (proveFilterQuery capabilityMessage expressionProofs) expression

    and private proveFilterSource capabilityMessage expressionProofs source =
        match source with
        | NamedTable _
        | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            proveFilterQuery capabilityMessage expressionProofs query

    and private proveFilterSelect capabilityMessage expressionProofs select =
        select.Ctes |> List.iter (fun cte -> proveFilterQuery capabilityMessage expressionProofs cte.Query)
        iterDistinctOn (proveFilterExpr capabilityMessage expressionProofs) select
        select.Projection |> List.iter (fun item -> proveFilterExpr capabilityMessage expressionProofs item.Expression)
        select.From |> Option.iter (proveFilterSource capabilityMessage expressionProofs)
        select.Joins |> List.iter (fun join ->
            proveFilterSource capabilityMessage expressionProofs join.Source
            join.Predicate |> Option.iter (proveFilterExpr capabilityMessage expressionProofs))
        select.Where |> Option.iter (proveFilterExpr capabilityMessage expressionProofs)
        select.GroupBy |> List.iter (proveFilterExpr capabilityMessage expressionProofs)
        select.Having |> Option.iter (proveFilterExpr capabilityMessage expressionProofs)

    and private proveFilterQuery capabilityMessage expressionProofs query =
        // SET subtree proofs are DFS in source order, with ORDER BY
        // validated only after all child branches. Heap frames keep that
        // diagnostic precedence without proportional CLR stack consumption.
        let pending = ResizeArray<unit -> unit>()
        let rec scheduleBranches branches =
            for branch in List.rev branches do
                pending.Add(fun () ->
                    let child = branch.Query
                    proveFilterSelect capabilityMessage expressionProofs child.Head
                    pending.Add(fun () ->
                        child.OrderBy |> List.iter (fun order ->
                            proveFilterExpr capabilityMessage expressionProofs order.Expression))
                    scheduleBranches child.SetOperations)
        proveFilterSelect capabilityMessage expressionProofs query.Head
        pending.Add(fun () ->
            query.OrderBy |> List.iter (fun order ->
                proveFilterExpr capabilityMessage expressionProofs order.Expression))
        scheduleBranches query.SetOperations
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let task = pending.[last]
            pending.RemoveAt(last)
            task ()

    let proveFilterDocument capabilityMessage expressionProofs document =
        match document.Statement with
        | QueryStatement query ->
            proveFilterQuery capabilityMessage expressionProofs query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (proveFilterExpr capabilityMessage expressionProofs))
            | QuerySource query -> proveFilterQuery capabilityMessage expressionProofs query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveFilterExpr capabilityMessage expressionProofs item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveFilterExpr capabilityMessage expressionProofs assignment.Value)
            update.From |> List.iter (proveFilterSource capabilityMessage expressionProofs)
            update.Where |> Option.iter (proveFilterExpr capabilityMessage expressionProofs)
            update.Returning |> List.iter (fun item -> proveFilterExpr capabilityMessage expressionProofs item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveFilterSource capabilityMessage expressionProofs)
            delete.Where |> Option.iter (proveFilterExpr capabilityMessage expressionProofs)
            delete.Returning |> List.iter (fun item -> proveFilterExpr capabilityMessage expressionProofs item.Expression)
        | MergeStatement merge ->
            merge.Source.SourceValues |> NonEmpty.iter (proveFilterExpr capabilityMessage expressionProofs)
            proveFilterExpr capabilityMessage expressionProofs merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> proveFilterExpr capabilityMessage expressionProofs item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (proveFilterExpr capabilityMessage expressionProofs))

    let rec private isRepeatableDistinctOperand expression =
        match expression with
        | Spanned(_, inner) -> isRepeatableDistinctOperand inner
        | Column _ | BoundColumn _ | OrderOrdinal _ | Literal _ -> true
        | DateAdd(_, amount, value)
        | DateDiff(_, amount, value) ->
            isRepeatableDistinctOperand amount && isRepeatableDistinctOperand value
        | Unary(_, operand)
        | Cast(operand, _)
        | Extract(_, operand) ->
            isRepeatableDistinctOperand operand
        | Binary((BinaryOperator.Add
                 | BinaryOperator.Subtract
                 | BinaryOperator.Multiply
                 | BinaryOperator.Divide
                 | BinaryOperator.Modulo
                 | BinaryOperator.Concat), left, right) ->
            isRepeatableDistinctOperand left && isRepeatableDistinctOperand right
        | _ -> false

    let rec private proveTargetExpr targetRuntime (expressionProofs: ExpressionProofs) expression =
        let validate node =
            match node with
            | Literal value -> proveTargetLiteral targetRuntime expressionProofs value
            | Interval _ -> requireExpressionCapability expressionProofs.IntervalLiteral
            | DateAdd(unit, _, _) ->
                match SqlDateMathCapabilityRules.TargetValidationError(
                          unit, TargetRuntime.provider targetRuntime, "CORE_DATE_ADD") with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | DateDiff(unit, _, _) ->
                match SqlDateMathCapabilityRules.TargetValidationError(
                          unit, TargetRuntime.provider targetRuntime, "CORE_DATE_DIFF") with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | Binary(BinaryOperator.Concat, _, _) ->
                proveSqlServerConcat targetRuntime
            | Binary((BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom), left, right) ->
                requireExpressionCapability expressionProofs.DistinctFrom
                if TargetRuntime.provider targetRuntime = SqlAgentToolType.Oracle
                   && (not (isRepeatableDistinctOperand left)
                       || not (isRepeatableDistinctOperand right)) then
                    raise (SqlCompilationException(
                        "Oracle null-safe distinct lowering requires repeatable scalar operands because the proven CASE lowering may reference each operand more than once; volatile functions, windows, and subqueries remain fail-closed."))
            | Like(_, _, _, _, true) ->
                requireExpressionCapability expressionProofs.ILike
            | RawRegexCall _ ->
                invalidOp "Raw REGEXP_LIKE reached target validation before canonicalization."
            | RegexMatch _ ->
                requireExpressionCapability expressionProofs.RegexMatch
            | FunctionCall call ->
                if FunctionName.hasQuotedParts call.Name then
                    requireExpressionCapability expressionProofs.QuotedFunction
                if FunctionName.isQualified call.Name then
                    requireExpressionCapability expressionProofs.QualifiedFunction
            | Cast(_, targetType) ->
                match targetRuntime, CastType.semantic targetType with
                | FirebirdRuntime, Some(SqlTime(_, true))
                | FirebirdRuntime, Some(SqlTimestamp(_, true)) ->
                    requireExpressionCapability expressionProofs.FirebirdTimeZoneType
                | _, Some _ -> ()
                | _, None ->
                    invalidOp "Compatibility raw CAST type reached target capability validation before semantic normalization."
                RewriteCastTypes.validateTarget (TargetRuntime.provider targetRuntime) targetType
            | _ -> ()
        walkCapabilityExpr validate
            (proveTargetQuery targetRuntime expressionProofs) expression

    and private proveTargetSource targetRuntime expressionProofs source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _) -> proveTargetQuery targetRuntime expressionProofs query
        | LateralDerivedTable(query, _) ->
            match SqlLateralDerivedTableCapabilityRules.TargetValidationError(
                      TargetRuntime.provider targetRuntime,
                      null) with
            | null -> proveTargetQuery targetRuntime expressionProofs query
            | message -> raise (SqlCompilationException(message))

    and private proveTargetSelect targetRuntime expressionProofs select =
        if select.Ctes |> List.exists (fun cte -> cte.RecursiveScope) then
            let provider = TargetRuntime.provider targetRuntime
            if not (SqlRecursiveCteCapabilityRules.SupportsWithRecursiveSyntax(provider)) then
                raise (SqlCompilationException(
                    "SQL capability 'select.recursive_cte' is not supported by target provider "
                    + string provider + "; this provider does not use the modeled WITH RECURSIVE syntax contract."))
        select.Ctes |> List.iter (fun cte -> proveTargetQuery targetRuntime expressionProofs cte.Query)
        match select.DistinctMode with
        | SelectDistinct.DistinctOn expressions ->
            match SqlDistinctOnCapabilityRules.TargetValidationError(TargetRuntime.provider targetRuntime) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            expressions |> NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs)
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()
        select.ProjectionItems |> NonEmpty.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        select.From |> Option.iter (proveTargetSource targetRuntime expressionProofs)
        select.Joins
        |> List.iter (function
            | CrossJoin source ->
                proveTargetSource targetRuntime expressionProofs source
            | NaturalJoin(_, source) ->
                match SqlNaturalJoinCapabilityRules.TargetValidationError(TargetRuntime.provider targetRuntime) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
                proveTargetSource targetRuntime expressionProofs source
            | OnJoin(_, source, predicate) ->
                proveTargetSource targetRuntime expressionProofs source
                proveTargetExpr targetRuntime expressionProofs predicate
            | UsingJoin(_, source, _) ->
                match SqlUsingJoinCapabilityRules.TargetValidationError(TargetRuntime.provider targetRuntime) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
                proveTargetSource targetRuntime expressionProofs source)
        select.Where |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
        select.GroupBy |> List.iter (proveTargetExpr targetRuntime expressionProofs)
        select.Having |> Option.iter (proveTargetExpr targetRuntime expressionProofs)

    and private proveTargetQuery targetRuntime expressionProofs query =
        // Check each SET node's FETCH policy before its SELECT and each
        // branch's operator capability before entering that child query.
        // Queue branch tails on the heap to preserve diagnostic precedence.
        let pending = ResizeArray<unit -> unit>()
        let proveHead (node: Query) =
            if node.FetchPercent.IsSome then
                match SqlFetchPercentCapabilityRules.TargetValidationError(
                          TargetRuntime.provider targetRuntime,
                          null) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            if node.FetchWithTies then
                match SqlFetchWithTiesCapabilityRules.TargetValidationError(
                          TargetRuntime.provider targetRuntime,
                          null) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            proveTargetSelect targetRuntime expressionProofs node.Head

        let proveOperator operator =
            match operator with
            | SetOperator.IntersectAll ->
                match SqlSetAllCapabilityRules.TargetValidationError(
                          "INTERSECT",
                          TargetRuntime.provider targetRuntime) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SetOperator.ExceptAll ->
                match SqlSetAllCapabilityRules.TargetValidationError(
                          "EXCEPT",
                          TargetRuntime.provider targetRuntime) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SetOperator.Union
            | SetOperator.UnionAll
            | SetOperator.Intersect
            | SetOperator.Except -> ()

        let rec scheduleBranches branches =
            for branch in List.rev branches do
                pending.Add(fun () ->
                    proveOperator branch.Operator
                    let child = branch.Query
                    proveHead child
                    pending.Add(fun () ->
                        child.OrderBy |> List.iter (fun order ->
                            proveTargetExpr targetRuntime expressionProofs order.Expression))
                    scheduleBranches child.SetOperations)

        proveHead query
        pending.Add(fun () ->
            query.OrderBy |> List.iter (fun order ->
                proveTargetExpr targetRuntime expressionProofs order.Expression))
        scheduleBranches query.SetOperations
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let task = pending.[last]
            pending.RemoveAt(last)
            task ()

    let proveTargetDocument targetRuntime expressionProofs document =
        match document.Statement with
        | QueryStatement query -> proveTargetQuery targetRuntime expressionProofs query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs))
            | QuerySource query -> proveTargetQuery targetRuntime expressionProofs query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveTargetExpr targetRuntime expressionProofs assignment.Value)
            update.From |> List.iter (proveTargetSource targetRuntime expressionProofs)
            update.Where |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
            update.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveTargetSource targetRuntime expressionProofs)
            delete.Where |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
            delete.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        | MergeStatement merge ->
            merge.Source.SourceValues |> NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs)
            proveTargetExpr targetRuntime expressionProofs merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs))
        document


