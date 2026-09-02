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

    let rec private proveFilterExpr capabilityMessage (expressionProofs: ExpressionProofs) expression =
        match expression with
        | Spanned(_, inner) -> proveFilterExpr capabilityMessage expressionProofs inner
        | Column _
        | BoundColumn _
        | Wildcard _
        | OrderOrdinal _
        | Literal _ -> ()
        | Interval _ ->
            requireFilterCapability capabilityMessage expressionProofs.IntervalLiteral
        | DateAdd(_, amount, value)
        | DateDiff(_, amount, value) ->
            proveFilterExpr capabilityMessage expressionProofs amount
            proveFilterExpr capabilityMessage expressionProofs value
        | Unary(_, operand) ->
            proveFilterExpr capabilityMessage expressionProofs operand
        | Binary((BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom), left, right) ->
            requireFilterCapability capabilityMessage expressionProofs.DistinctFrom
            proveFilterExpr capabilityMessage expressionProofs left
            proveFilterExpr capabilityMessage expressionProofs right
        | Binary(_, left, right) ->
            proveFilterExpr capabilityMessage expressionProofs left
            proveFilterExpr capabilityMessage expressionProofs right
        | Like(value, pattern, _, _, caseInsensitive) ->
            if caseInsensitive then requireFilterCapability capabilityMessage expressionProofs.ILike
            proveFilterExpr capabilityMessage expressionProofs value
            proveFilterExpr capabilityMessage expressionProofs pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (proveFilterExpr capabilityMessage expressionProofs)
        | RegexMatch(value, pattern) ->
            proveFilterExpr capabilityMessage expressionProofs value
            proveFilterExpr capabilityMessage expressionProofs pattern
        | PostgresJsonAccess(value, _, _) ->
            proveFilterExpr capabilityMessage expressionProofs value
        | FunctionCall call ->
            if FunctionName.hasQuotedParts call.Name then
                requireFilterCapability capabilityMessage expressionProofs.QuotedFunction
            if FunctionName.isQualified call.Name then
                requireFilterCapability capabilityMessage expressionProofs.QualifiedFunction
            call.Arguments |> List.iter (proveFilterExpr capabilityMessage expressionProofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveFilterExpr capabilityMessage expressionProofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            requireFilterCapability capabilityMessage expressionProofs.AggregateFilter
            proveFilterPredicate capabilityMessage expressionProofs.FilterPredicate predicate
            proveFilterExpr capabilityMessage expressionProofs value
            proveFilterExpr capabilityMessage expressionProofs predicate
        | Windowed(value, window) ->
            proveFilterExpr capabilityMessage expressionProofs value
            window.PartitionBy |> List.iter (proveFilterExpr capabilityMessage expressionProofs)
            window.OrderBy |> List.iter (fun order -> proveFilterExpr capabilityMessage expressionProofs order.Expression)
        | Cast(value, _)
        | Extract(_, value) ->
            proveFilterExpr capabilityMessage expressionProofs value
        | SimpleCase(input, branches, fallback) ->
            proveFilterExpr capabilityMessage expressionProofs input
            branches |> NonEmpty.iter (fun branch ->
                proveFilterExpr capabilityMessage expressionProofs branch.Match
                proveFilterExpr capabilityMessage expressionProofs branch.Result)
            fallback |> Option.iter (proveFilterExpr capabilityMessage expressionProofs)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveFilterExpr capabilityMessage expressionProofs branch.Condition
                proveFilterExpr capabilityMessage expressionProofs branch.Result)
            fallback |> Option.iter (proveFilterExpr capabilityMessage expressionProofs)
        | InList(value, items, _) ->
            proveFilterExpr capabilityMessage expressionProofs value
            items |> NonEmpty.iter (proveFilterExpr capabilityMessage expressionProofs)
        | InSubquery(value, query, _) ->
            proveFilterExpr capabilityMessage expressionProofs value
            proveFilterQuery capabilityMessage expressionProofs query
        | Between(value, lower, upper, _) ->
            proveFilterExpr capabilityMessage expressionProofs value
            proveFilterExpr capabilityMessage expressionProofs lower
            proveFilterExpr capabilityMessage expressionProofs upper
        | IsNull(value, _) ->
            proveFilterExpr capabilityMessage expressionProofs value
        | ScalarSubquery query
        | Exists(query, _) ->
            proveFilterQuery capabilityMessage expressionProofs query

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
        proveFilterSelect capabilityMessage expressionProofs query.Head
        query.SetOperations |> List.iter (fun branch -> proveFilterQuery capabilityMessage expressionProofs branch.Query)
        query.OrderBy |> List.iter (fun order -> proveFilterExpr capabilityMessage expressionProofs order.Expression)

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
            merge.Source.Values |> NonEmpty.iter (proveFilterExpr capabilityMessage expressionProofs)
            proveFilterExpr capabilityMessage expressionProofs merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> proveFilterExpr capabilityMessage expressionProofs item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.SourceValues |> NonEmpty.iter (proveFilterExpr capabilityMessage expressionProofs))

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
        match expression with
        | Spanned(_, inner) -> proveTargetExpr targetRuntime expressionProofs inner
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ -> ()
        | Literal value -> proveTargetLiteral targetRuntime expressionProofs value
        | Interval _ -> requireExpressionCapability expressionProofs.IntervalLiteral
        | DateAdd(unit, amount, value) ->
            match SqlDateMathCapabilityRules.TargetValidationError(
                      unit,
                      TargetRuntime.provider targetRuntime,
                      "CORE_DATE_ADD") with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            proveTargetExpr targetRuntime expressionProofs amount
            proveTargetExpr targetRuntime expressionProofs value
        | DateDiff(unit, startValue, finishValue) ->
            match SqlDateMathCapabilityRules.TargetValidationError(
                      unit,
                      TargetRuntime.provider targetRuntime,
                      "CORE_DATE_DIFF") with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            proveTargetExpr targetRuntime expressionProofs startValue
            proveTargetExpr targetRuntime expressionProofs finishValue
        | Unary(_, operand) -> proveTargetExpr targetRuntime expressionProofs operand
        | Binary(BinaryOperator.Concat, left, right) ->
            proveSqlServerConcat targetRuntime
            proveTargetExpr targetRuntime expressionProofs left
            proveTargetExpr targetRuntime expressionProofs right
        | Binary((BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom), left, right) ->
            requireExpressionCapability expressionProofs.DistinctFrom
            if TargetRuntime.provider targetRuntime = SqlAgentToolType.Oracle
               && (not (isRepeatableDistinctOperand left) || not (isRepeatableDistinctOperand right)) then
                raise (SqlCompilationException(
                    "Oracle null-safe distinct lowering requires repeatable scalar operands because the proven CASE lowering may reference each operand more than once; volatile functions, windows, and subqueries remain fail-closed."))
            proveTargetExpr targetRuntime expressionProofs left
            proveTargetExpr targetRuntime expressionProofs right
        | Binary(_, left, right) ->
            proveTargetExpr targetRuntime expressionProofs left
            proveTargetExpr targetRuntime expressionProofs right
        | Like(value, pattern, _, _, caseInsensitive) ->
            if caseInsensitive then requireExpressionCapability expressionProofs.ILike
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs pattern
        | RawRegexCall _ ->
            invalidOp "Raw REGEXP_LIKE reached target validation before canonicalization."
        | RegexMatch(value, pattern) ->
            requireExpressionCapability expressionProofs.RegexMatch
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs pattern
        | PostgresJsonAccess(value, _, _) ->
            proveTargetExpr targetRuntime expressionProofs value
        | FunctionCall call ->
            if FunctionName.hasQuotedParts call.Name then
                requireExpressionCapability expressionProofs.QuotedFunction
            if FunctionName.isQualified call.Name then
                requireExpressionCapability expressionProofs.QualifiedFunction
            call.Arguments |> List.iter (proveTargetExpr targetRuntime expressionProofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs predicate
        | Windowed(value, window) ->
            proveTargetExpr targetRuntime expressionProofs value
            window.PartitionBy |> List.iter (proveTargetExpr targetRuntime expressionProofs)
            window.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)
        | Cast(value, targetType) ->
            match targetRuntime, CastType.semantic targetType with
            | FirebirdRuntime, Some(SqlTime(_, true))
            | FirebirdRuntime, Some(SqlTimestamp(_, true)) ->
                requireExpressionCapability expressionProofs.FirebirdTimeZoneType
            | _, Some _ -> ()
            | _, None ->
                invalidOp "Compatibility raw CAST type reached target capability validation before semantic normalization."
            RewriteCastTypes.validateTarget (TargetRuntime.provider targetRuntime) targetType
            proveTargetExpr targetRuntime expressionProofs value
        | Extract(_, value) ->
            proveTargetExpr targetRuntime expressionProofs value
        | SimpleCase(input, branches, fallback) ->
            proveTargetExpr targetRuntime expressionProofs input
            branches |> NonEmpty.iter (fun branch ->
                proveTargetExpr targetRuntime expressionProofs branch.Match
                proveTargetExpr targetRuntime expressionProofs branch.Result)
            fallback |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveTargetExpr targetRuntime expressionProofs branch.Condition
                proveTargetExpr targetRuntime expressionProofs branch.Result)
            fallback |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
        | InList(value, items, _) ->
            proveTargetExpr targetRuntime expressionProofs value
            items |> NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs)
        | InSubquery(value, query, _) ->
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetQuery targetRuntime expressionProofs query
        | Between(value, lower, upper, _) ->
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs lower
            proveTargetExpr targetRuntime expressionProofs upper
        | IsNull(value, _) ->
            proveTargetExpr targetRuntime expressionProofs value
        | ScalarSubquery query ->
            proveTargetQuery targetRuntime expressionProofs query
        | Exists(query, _) ->
            proveTargetQuery targetRuntime expressionProofs query

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
        if query.FetchPercent.IsSome then
            match SqlFetchPercentCapabilityRules.TargetValidationError(
                      TargetRuntime.provider targetRuntime,
                      null) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
        if query.FetchWithTies then
            match SqlFetchWithTiesCapabilityRules.TargetValidationError(
                      TargetRuntime.provider targetRuntime,
                      null) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
        proveTargetSelect targetRuntime expressionProofs query.Head
        query.SetOperations
        |> List.iter (fun branch ->
            match branch.Operator with
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
            proveTargetQuery targetRuntime expressionProofs branch.Query)
        query.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)

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
            merge.Source.Values |> NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs)
            proveTargetExpr targetRuntime expressionProofs merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.SourceValues |> NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs))
        document


