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
        | PostgreSqlRuntime, ScalarValue.ExactNumeric _ -> ()
        | MySqlRuntime, ScalarValue.ExactNumeric number ->
            match ExactNumeric.tryBoundedDecimal 65 30 number with
            | Some _ -> ()
            | None ->
                raise (SqlCompilationException(
                    "SQL capability 'numeric.exact_literal' cannot preserve this value "
                    + "as MySQL DECIMAL: maximum precision 65 and scale 30."))
        | SqlServerRuntime _, ScalarValue.ExactNumeric number ->
            match ExactNumeric.tryBoundedDecimal 38 38 number with
            | Some _ -> ()
            | None ->
                raise (SqlCompilationException(
                    "SQL capability 'numeric.exact_literal' cannot preserve this value "
                    + "as SQL Server DECIMAL: maximum precision 38 and scale 38."))
        | OracleRuntime, ScalarValue.ExactNumeric number ->
            // Oracle NUMBER is exact up to 38 decimal digits. Never assume
            // that an unconstrained cast can carry more or silently round.
            // Oracle's independent NLS decimal parsing is explicitly fixed
            // at the bound-text renderer.
            match ExactNumeric.tryBoundedDecimal 38 38 number with
            | Some _ -> ()
            | None ->
                raise (SqlCompilationException(
                    "SQL capability 'numeric.exact_literal' cannot preserve this value "
                    + "as Oracle NUMBER: maximum precision 38 and portable scale 38."))
        | FirebirdRuntime, ScalarValue.ExactNumeric number ->
            // Only integer-scale INT128 bindings are proven by the
            // pinned ADO.NET driver. VARCHAR -> NUMERIC(p,s) was observed
            // rounding and overflowing for scale >0 and wide integers.
            // Do not substitute DECFLOAT(34) or implicit CLR Decimal.
            match ExactNumeric.tryBoundedDecimal 38 0 number with
            | None ->
                raise (SqlCompilationException(
                    "SQL capability 'numeric.exact_literal' cannot preserve this value "
                    + "via the Firebird INT128 driver binding: only exact integer scale 0 "
                    + "and maximum precision 38 are proven; fractional values remain fail-closed."))
            | Some(_, precision, _) when precision > 18 ->
                match proofs.FirebirdExtendedDecimal with
                | ProvenCapability -> ()
                | RejectedCapability _ ->
                    raise (SqlCompilationException(
                        "SQL capability 'numeric.decimal_extended' requires an explicit Firebird "
                        + "ServerVersion 4.0+ target profile to prove INT128-backed NUMERIC."))
            | Some _ -> ()

        | _, ScalarValue.ExactNumeric _ ->
            raise (SqlCompilationException(
                "SQL capability 'numeric.exact_literal' requires a proven exact "
                + "text-parameter DECIMAL CAST target; no lossless proof exists for this provider."))
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

    // FILTER aggregate predicates can be deep Boolean/expression trees.
    // Keep the original left-to-right validation order without recursion,
    // including the post-operand Subquery proof for IN (SELECT ...).
    type private FilterPredicateFrame =
        | VisitFilterPredicate of Expr
        | CheckFilterSubquery

    let private proveFilterPredicate capabilityMessage (proofs: FilterPredicateProofs) root =
        let pending = ResizeArray<FilterPredicateFrame>()
        let push value = pending.Add(VisitFilterPredicate value)
        let pushReverse (expressions: Expr list) =
            for expression in List.rev expressions do push expression
        push root
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last
            match frame with
            | CheckFilterSubquery ->
                requireFilterCapability capabilityMessage proofs.Subquery
            | VisitFilterPredicate expression ->
                match expression with
                | Spanned(_, inner) -> push inner
                | BoundColumn(_, OuterRowSource) ->
                    requireFilterCapability capabilityMessage proofs.OuterReference
                | Column _ | BoundColumn(_, LocalRowSource)
                | BoundColumn(_, ProjectionAlias) | Wildcard _
                | OrderOrdinal _ | Literal _ | Interval _ -> ()
                | DateAdd(_, left, right) | DateDiff(_, left, right)
                | Binary(_, left, right) | Like(left, right, _, _, _)
                | RegexMatch(left, right) | FilteredAggregate(left, right) ->
                    push right
                    push left
                | Unary(_, inner) | Cast(inner, _)
                | Extract(_, inner) | PostgresJsonAccess(inner, _, _)
                | IsNull(inner, _) -> push inner
                | RawRegexCall(arguments, _) -> pushReverse arguments
                | FunctionCall call ->
                    pushReverse (call.AggregateOrderBy |> List.map (fun order -> order.Expression))
                    pushReverse call.Arguments
                | Windowed(value, window) ->
                    requireFilterCapability capabilityMessage proofs.WindowFunction
                    pushReverse (window.OrderBy |> List.map (fun order -> order.Expression))
                    pushReverse window.PartitionBy
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
                | InSubquery(value, _, _) ->
                    pending.Add CheckFilterSubquery
                    push value
                | Between(value, lower, upper, _) ->
                    push upper
                    push lower
                    push value
                | ScalarSubquery _ | Exists _ ->
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

    /// This pass only requires capabilities; it does not construct semantic
    /// evidence, normalize expressions or perform policy checks. If every
    /// capability checked by proveFilterNode/Predicate is already proven, the
    /// visitor cannot reject anything and its full-graph walk is redundant.
    /// Rejected proofs still use the exact original first-error walk.
    let private allFilterCapabilitiesProven (proofs: ExpressionProofs) =
        let accepted = function
            | ProvenCapability -> true
            | RejectedCapability _ -> false
        accepted proofs.ILike
        && accepted proofs.DistinctFrom
        && accepted proofs.IntervalLiteral
        && accepted proofs.AggregateFilter
        && accepted proofs.QuotedFunction
        && accepted proofs.QualifiedFunction
        && accepted proofs.FilterPredicate.OuterReference
        && accepted proofs.FilterPredicate.Subquery
        && accepted proofs.FilterPredicate.WindowFunction

    let private proveFilterDocumentWhenRequired capabilityMessage expressionProofs document =
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
            update.Ctes |> List.iter (fun cte -> proveFilterQuery capabilityMessage expressionProofs cte.Query)
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveFilterExpr capabilityMessage expressionProofs assignment.Value)
            update.From |> List.iter (proveFilterSource capabilityMessage expressionProofs)
            update.Where |> Option.iter (proveFilterExpr capabilityMessage expressionProofs)
            update.Returning |> List.iter (fun item -> proveFilterExpr capabilityMessage expressionProofs item.Expression)
        | DeleteStatement delete ->
            delete.Ctes |> List.iter (fun cte -> proveFilterQuery capabilityMessage expressionProofs cte.Query)
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

    let proveFilterDocument capabilityMessage expressionProofs document =
        if not (allFilterCapabilitiesProven expressionProofs) then
            proveFilterDocumentWhenRequired capabilityMessage expressionProofs document

    // Oracle CASE-based null-safe lowering may evaluate operands more
    // than once. Reject any unproven/volatile construct, but traverse
    // arbitrarily deep *proven* arithmetic/cast operands without CLR recursion.
    let private isRepeatableDistinctOperand root =
        let pending = ResizeArray<Expr>()
        pending.Add(root)
        let mutable repeatable = true
        while repeatable && pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let node = pending.[last]
            pending.RemoveAt last
            match node with
            | Spanned(_, inner) -> pending.Add(inner)
            | Column _ | BoundColumn _ | OrderOrdinal _ | Literal _ -> ()
            | DateAdd(_, left, right) | DateDiff(_, left, right)
            | Binary((BinaryOperator.Add
                     | BinaryOperator.Subtract
                     | BinaryOperator.Multiply
                     | BinaryOperator.Divide
                     | BinaryOperator.Modulo
                     | BinaryOperator.Concat), left, right) ->
                pending.Add(right)
                pending.Add(left)
            | Unary(_, inner) | Cast(inner, _) | Extract(_, inner) ->
                pending.Add(inner)
            | _ -> repeatable <- false
        repeatable

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
            // Validate invariant decimal bounds before optional/versioned
            // Firebird feature proofs, so a 39-digit shape reports the
            // actual 38-digit type limit even when the profile is absent.
            RewriteCastTypes.validateTarget (TargetRuntime.provider targetRuntime) targetType
            match targetRuntime, CastType.semantic targetType with
            | FirebirdRuntime, Some(SqlDecimal(Some precision, _)) when precision > 18 ->
                // Firebird 3 and older lack extended exact-numeric types.
                // Do not confuse syntactically modeled DECIMAL(20,4) with
                // a target capability proven by an explicit 4.0+ profile.
                match expressionProofs.FirebirdExtendedDecimal with
                | ProvenCapability -> ()
                | RejectedCapability _ ->
                    raise (SqlCompilationException(
                        "SQL capability 'numeric.decimal_extended' requires a Firebird target "
                        + "ServerVersion 4.0+ profile for DECIMAL precision above 18."))
            | FirebirdRuntime, Some(SqlTime(_, true))
            | FirebirdRuntime, Some(SqlTimestamp(_, true)) ->
                requireExpressionCapability expressionProofs.FirebirdTimeZoneType
            | _, Some _ -> ()
            | _, None ->
                invalidOp "Compatibility raw CAST type reached target capability validation before semantic normalization."
        | _ -> ()

    let private proveTargetGraph targetRuntime targetProfile expressionProofs root =
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
                match SqlLateralDerivedTableCapabilityRules.SyntaxEmissionValidationError(provider) with
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
            | ApplyJoin _ ->
                match SqlApplyJoinCapabilityRules.TargetSyntaxValidationError(provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | CrossJoin _ | OnJoin _ -> ()
        let onSet operator =
            match operator with
            | SetOperator.IntersectAll ->
                match SqlSetAllCapabilityRules.TargetValidationError("INTERSECT", provider, targetProfile) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SetOperator.ExceptAll ->
                match SqlSetAllCapabilityRules.TargetValidationError("EXCEPT", provider, targetProfile) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SetOperator.Union | SetOperator.UnionAll
            | SetOperator.Intersect | SetOperator.Except -> ()
        walkCapabilityGraph
            (proveTargetNode targetRuntime expressionProofs)
            onQuery onSelect onDistinct onSource onJoin onSet root

    let private proveTargetExpr targetRuntime targetProfile expressionProofs expression =
        proveTargetGraph targetRuntime targetProfile expressionProofs (VisitCapabilityExpr expression)

    let private proveTargetSource targetRuntime targetProfile expressionProofs source =
        proveTargetGraph targetRuntime targetProfile expressionProofs (VisitCapabilitySource source)

    let private proveTargetQuery targetRuntime targetProfile expressionProofs query =
        proveTargetGraph targetRuntime targetProfile expressionProofs (VisitCapabilityQuery query)

    let proveTargetDocument targetRuntime targetProfile expressionProofs document =
        match document.Statement with
        | QueryStatement query -> proveTargetQuery targetRuntime targetProfile expressionProofs query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (proveTargetExpr targetRuntime targetProfile expressionProofs))
            | QuerySource query -> proveTargetQuery targetRuntime targetProfile expressionProofs query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime targetProfile expressionProofs item.Expression)
        | UpdateStatement update ->
            update.Ctes |> List.iter (fun cte -> proveTargetQuery targetRuntime targetProfile expressionProofs cte.Query)
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveTargetExpr targetRuntime targetProfile expressionProofs assignment.Value)
            update.From |> List.iter (proveTargetSource targetRuntime targetProfile expressionProofs)
            update.Where |> Option.iter (proveTargetExpr targetRuntime targetProfile expressionProofs)
            update.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime targetProfile expressionProofs item.Expression)
        | DeleteStatement delete ->
            delete.Ctes |> List.iter (fun cte -> proveTargetQuery targetRuntime targetProfile expressionProofs cte.Query)
            delete.Using |> List.iter (proveTargetSource targetRuntime targetProfile expressionProofs)
            delete.Where |> Option.iter (proveTargetExpr targetRuntime targetProfile expressionProofs)
            delete.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime targetProfile expressionProofs item.Expression)
        | MergeStatement merge ->
            merge.Source.SourceValues |> NonEmpty.iter (proveTargetExpr targetRuntime targetProfile expressionProofs)
            proveTargetExpr targetRuntime targetProfile expressionProofs merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> proveTargetExpr targetRuntime targetProfile expressionProofs item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (proveTargetExpr targetRuntime targetProfile expressionProofs))
        document


