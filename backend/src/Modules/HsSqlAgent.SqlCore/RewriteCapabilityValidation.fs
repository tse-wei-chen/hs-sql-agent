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

    // A capability traversal never needs to rebuild the tree or propagate
    // query scope. Keep *all* Query/Expr/CTE/Source/JOIN edges on one heap
    // stack instead of re-entering a Query from expression/source callbacks.
    // Pre-order source diagnostics and per-query SET-tail order are stable.
    type private CapabilityGraphFrame =
        | VisitCapabilityExpr of Expr
        | VisitCapabilityQuery of Query
        | VisitCapabilitySource of TableSource
        | VisitCapabilityJoin of Join
        | VisitCapabilityDistinct of SelectDistinct
        | VisitCapabilitySetBranch of SetBranch
        | VisitCapabilityTail of Query

    let private walkCapabilityGraph
        onExpr onQuery onSelect onDistinct onSource onJoin onSet root =
        let pending = ResizeArray<CapabilityGraphFrame>()
        let pushExpr value = pending.Add(VisitCapabilityExpr value)
        let pushExprs (values: Expr list) =
            for value in List.rev values do pushExpr value
        let pushOrders (values: OrderBy list) =
            for order in List.rev values do pushExpr order.Expression
        pending.Add(root)
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last
            match frame with
            | VisitCapabilityExpr expression ->
                onExpr expression
                match expression with
                | Spanned(_, inner) -> pushExpr inner
                | DateAdd(_, left, right) | DateDiff(_, left, right)
                | Binary(_, left, right) | Like(left, right, _, _, _)
                | RegexMatch(left, right) | FilteredAggregate(left, right) ->
                    pushExpr right
                    pushExpr left
                | Unary(_, inner) | Cast(inner, _) | Extract(_, inner)
                | PostgresJsonAccess(inner, _, _) | IsNull(inner, _) ->
                    pushExpr inner
                | RawRegexCall(args, _) -> pushExprs args
                | FunctionCall call ->
                    pushOrders call.AggregateOrderBy
                    pushExprs call.Arguments
                | Windowed(value, window) ->
                    pushOrders window.OrderBy
                    pushExprs window.PartitionBy
                    pushExpr value
                | SimpleCase(input, branches, fallback) ->
                    fallback |> Option.iter pushExpr
                    NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                        pushExpr branch.Result
                        pushExpr branch.Match) branches
                    pushExpr input
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter pushExpr
                    NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                        pushExpr branch.Result
                        pushExpr branch.Condition) branches
                | InList(value, items, _) ->
                    NonEmpty.iterReverse pushExpr items
                    pushExpr value
                | InSubquery(value, query, _) ->
                    pending.Add(VisitCapabilityQuery query)
                    pushExpr value
                | Between(value, lower, upper, _) ->
                    pushExpr upper
                    pushExpr lower
                    pushExpr value
                | ScalarSubquery query | Exists(query, _) ->
                    pending.Add(VisitCapabilityQuery query)
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ -> ()
            | VisitCapabilityQuery query ->
                // Target-only FETCH and recursive-CTE checks execute before
                // SELECT proof, as before. The Filter pass supplies no-ops.
                onQuery query
                let select = query.Head
                onSelect select
                pending.Add(VisitCapabilityTail query)
                for branch in List.rev query.SetOperations do
                    pending.Add(VisitCapabilitySetBranch branch)
                select.Having |> Option.iter pushExpr
                pushExprs select.GroupBy
                select.Where |> Option.iter pushExpr
                for join in List.rev select.Joins do
                    pending.Add(VisitCapabilityJoin join)
                select.From |> Option.iter (VisitCapabilitySource >> pending.Add)
                for item in List.rev select.Projection do
                    pushExpr item.Expression
                pending.Add(VisitCapabilityDistinct select.DistinctMode)
                for cte in List.rev select.Ctes do
                    pending.Add(VisitCapabilityQuery cte.Query)
            | VisitCapabilityDistinct mode ->
                onDistinct mode
                match mode with
                | SelectDistinct.DistinctOn expressions ->
                    NonEmpty.iterReverse pushExpr expressions
                | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> ()
            | VisitCapabilitySource source ->
                onSource source
                match source with
                | NamedTable _ | CteTable _ -> ()
                | DerivedTable(query, _) | LateralDerivedTable(query, _) ->
                    pending.Add(VisitCapabilityQuery query)
            | VisitCapabilityJoin join ->
                onJoin join
                join.Predicate |> Option.iter pushExpr
                pending.Add(VisitCapabilitySource join.Source)
            | VisitCapabilitySetBranch branch ->
                onSet branch.Operator
                pending.Add(VisitCapabilityQuery branch.Query)
            | VisitCapabilityTail query ->
                pushOrders query.OrderBy

    let private proveFilterNode capabilityMessage (expressionProofs: ExpressionProofs) node =
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
            // Predicate check precedes the scalar-operand traversal.
            proveFilterPredicate capabilityMessage expressionProofs.FilterPredicate predicate
        | _ -> ()

    let private proveFilterGraph capabilityMessage expressionProofs frame =
        walkCapabilityGraph
            (proveFilterNode capabilityMessage expressionProofs)
            (fun _ -> ()) (fun _ -> ()) (fun _ -> ())
            (fun _ -> ()) (fun _ -> ()) (fun _ -> ()) frame

    let private proveFilterExpr capabilityMessage expressionProofs expression =
        proveFilterGraph capabilityMessage expressionProofs (VisitCapabilityExpr expression)

    let private proveFilterSource capabilityMessage expressionProofs source =
        proveFilterGraph capabilityMessage expressionProofs (VisitCapabilitySource source)

    let private proveFilterQuery capabilityMessage expressionProofs query =
        proveFilterGraph capabilityMessage expressionProofs (VisitCapabilityQuery query)

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

    let private proveTargetNode targetRuntime (expressionProofs: ExpressionProofs) node =
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
        | Binary(BinaryOperator.Concat, _, _) -> proveSqlServerConcat targetRuntime
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
        | RegexMatch _ -> requireExpressionCapability expressionProofs.RegexMatch
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

    let private proveTargetGraph targetRuntime expressionProofs root =
        let provider = TargetRuntime.provider targetRuntime
        let onQuery (node: Query) =
            if node.FetchPercent.IsSome then
                match SqlFetchPercentCapabilityRules.TargetValidationError(provider, null) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            if node.FetchWithTies then
                match SqlFetchWithTiesCapabilityRules.TargetValidationError(provider, null) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
        let onSelect (select: Select) =
            if select.Ctes |> List.exists (fun cte -> cte.RecursiveScope) then
                if not (SqlRecursiveCteCapabilityRules.SupportsWithRecursiveSyntax(provider)) then
                    raise (SqlCompilationException(
                        "SQL capability 'select.recursive_cte' is not supported by target provider "
                        + string provider + "; this provider does not use the modeled WITH RECURSIVE syntax contract."))
        let onDistinct mode =
            match mode with
            | SelectDistinct.DistinctOn _ ->
                match SqlDistinctOnCapabilityRules.TargetValidationError(provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> ()
        let onSource source =
            match source with
            | LateralDerivedTable _ ->
                match SqlLateralDerivedTableCapabilityRules.TargetValidationError(provider, null) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | NamedTable _ | CteTable _ | DerivedTable _ -> ()
        let onJoin join =
            match join with
            | NaturalJoin _ ->
                match SqlNaturalJoinCapabilityRules.TargetValidationError(provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | UsingJoin _ ->
                match SqlUsingJoinCapabilityRules.TargetValidationError(provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | CrossJoin _ | OnJoin _ -> ()
        let onSet operator =
            match operator with
            | SetOperator.IntersectAll ->
                match SqlSetAllCapabilityRules.TargetValidationError("INTERSECT", provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SetOperator.ExceptAll ->
                match SqlSetAllCapabilityRules.TargetValidationError("EXCEPT", provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SetOperator.Union | SetOperator.UnionAll
            | SetOperator.Intersect | SetOperator.Except -> ()
        walkCapabilityGraph
            (proveTargetNode targetRuntime expressionProofs)
            onQuery onSelect onDistinct onSource onJoin onSet root

    let private proveTargetExpr targetRuntime expressionProofs expression =
        proveTargetGraph targetRuntime expressionProofs (VisitCapabilityExpr expression)

    let private proveTargetSource targetRuntime expressionProofs source =
        proveTargetGraph targetRuntime expressionProofs (VisitCapabilitySource source)

    let private proveTargetQuery targetRuntime expressionProofs query =
        proveTargetGraph targetRuntime expressionProofs (VisitCapabilityQuery query)

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


