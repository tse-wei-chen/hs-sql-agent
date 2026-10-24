namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewritePolicy =

    type ExecutableSql = private ExecutableSql of Document * TargetRuntime

    module Executable =
        let internal value (ExecutableSql(document, _)) = document
        let internal targetRuntime (ExecutableSql(_, targetRuntime)) = targetRuntime

    type MutationSafety = RequirePredicate | AllowAllRows
    type RowCap = Unlimited | MaxRows of PositiveRowCount

    type ExecutionPolicy =
        { UpdateSafety: MutationSafety
          DeleteSafety: MutationSafety
          QueryRows: RowCap }
        member this.AllowUnboundedUpdate = this.UpdateSafety = AllowAllRows
        member this.AllowUnboundedDelete = this.DeleteSafety = AllowAllRows
        member this.QueryMaxRows = match this.QueryRows with Unlimited -> 0 | MaxRows count -> PositiveRowCount.value count

    let safeDefaults = { UpdateSafety = RequirePredicate; DeleteSafety = RequirePredicate; QueryRows = Unlimited }

    let private diagnosticDataKey = "HsSqlAgent.SqlCore.Diagnostic"

    let private deny code (span: Span) message : 'T =
        let diagnosticSpan =
            if span.Start < 0 || span.Length < 0 then null
            else SqlDiagnosticSpan(span.Start, span.Length)
        let diagnostic =
            SqlDiagnostic(
                code,
                SqlDiagnosticStage.Policy,
                SqlDiagnosticCategory.Policy,
                message,
                diagnosticSpan)
        let error = UnauthorizedAccessException(message)
        error.Data[diagnosticDataKey] <- diagnostic
        raise error


    // A conservative three-valued proof: only expressions that are TRUE for every row
    // are considered unbounded. Never simplify unknown column comparisons, NULL
    // semantics, collation-sensitive string comparisons or provider functions here.
    type private PredicateTruth = AlwaysTrue | AlwaysFalse | UnknownTruth

    let private truth value = if value then AlwaysTrue else AlwaysFalse

    let private literalEquality left right =
        match Expr.unspan left, Expr.unspan right with
        | Literal(ScalarValue.Integer x), Literal(ScalarValue.Integer y) -> Some(x = y)
        | Literal(ScalarValue.Decimal x), Literal(ScalarValue.Decimal y) -> Some(x = y)
        | Literal(ScalarValue.Boolean x), Literal(ScalarValue.Boolean y) -> Some(x = y)
        | _ -> None

    type private PredicateFrame = Visit of Expr | Negate | CombineAnd | CombineOr

    let private constantPredicate expression =
        let pending = Collections.Generic.Stack<PredicateFrame>()
        let values = Collections.Generic.Stack<PredicateTruth>()
        pending.Push(Visit expression)
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            match pending.Pop() with
            | Negate ->
                let result =
                    match values.Pop() with
                    | AlwaysTrue -> AlwaysFalse
                    | AlwaysFalse -> AlwaysTrue
                    | UnknownTruth -> UnknownTruth
                values.Push result
            | CombineAnd | CombineOr as operation ->
                let right = values.Pop()
                let left = values.Pop()
                let result =
                    match operation, left, right with
                    | CombineAnd, AlwaysFalse, _ | CombineAnd, _, AlwaysFalse -> AlwaysFalse
                    | CombineAnd, AlwaysTrue, AlwaysTrue -> AlwaysTrue
                    | CombineOr, AlwaysTrue, _ | CombineOr, _, AlwaysTrue -> AlwaysTrue
                    | CombineOr, AlwaysFalse, AlwaysFalse -> AlwaysFalse
                    | _ -> UnknownTruth
                values.Push result
            | Visit value ->
                match value with
                | Spanned(_, inner) -> pending.Push(Visit inner)
                | Literal(ScalarValue.Boolean value) -> values.Push(truth value)
                | Unary(UnaryOperator.Not, inner) ->
                    pending.Push Negate
                    pending.Push(Visit inner)
                | Binary(BinaryOperator.And, left, right)
                | Binary(BinaryOperator.Or, left, right) ->
                    pending.Push(match value with Binary(BinaryOperator.And, _, _) -> CombineAnd | _ -> CombineOr)
                    pending.Push(Visit right)
                    pending.Push(Visit left)
                | Binary(BinaryOperator.Equal, left, right) ->
                    values.Push(literalEquality left right |> Option.map truth |> Option.defaultValue UnknownTruth)
                | Binary(BinaryOperator.NotEqual, left, right) ->
                    values.Push(literalEquality left right |> Option.map (not >> truth) |> Option.defaultValue UnknownTruth)
                | IsNull(inner, negated) ->
                    let result =
                        match Expr.unspan inner with
                        | Literal ScalarValue.Null -> truth (not negated)
                        | Literal _ -> truth negated
                        | _ -> UnknownTruth
                    values.Push result
                | _ -> values.Push UnknownTruth
        values.Pop()

    let private denyUnboundedPredicate code (document: Document) (expression: Expr) =
        let sourceSpan = Expr.span expression
        let span = if Span.isKnown sourceSpan then sourceSpan else document.Span
        deny code span "Security policy requires a selective mutation predicate; this WHERE expression is provably TRUE for every row. Explicit full-table authorization is required."

    let private clampQueryLimit span (rowCap: RowCap) (query: Query) =
        match rowCap with
        | Unlimited -> query
        | MaxRows _ when query.FetchPercent.IsSome ->
            deny
                "SQL_POLICY_QUERY_MAX_ROWS_UNPROVABLE"
                span
                ("Security policy QueryMaxRows is a hard row cap, but FETCH ... PERCENT is relative to the query result cardinality. "
                 + "SQL capability 'select.fetch_percent' therefore remains policy-denied when QueryMaxRows is enabled.")
        | MaxRows _ when query.FetchWithTies ->
            deny
                "SQL_POLICY_QUERY_MAX_ROWS_UNPROVABLE"
                span
                ("Security policy QueryMaxRows is a hard row cap, but FETCH ... WITH TIES can return more rows than its FETCH count. "
                 + "SQL capability 'select.fetch_with_ties' therefore remains policy-denied when QueryMaxRows is enabled.")
        | MaxRows maxRows ->
            let maxValue = PositiveRowCount.value maxRows
            let limit =
                match query.Limit with
                | None -> Some(NonNegativeRowCount.create maxValue)
                | Some value -> Some(NonNegativeRowCount.create (min (NonNegativeRowCount.value value) maxValue))
            { query with Limit = limit }

    let private authorizeDocument policy document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(clampQueryLimit document.Span policy.QueryRows query)
            | UpdateStatement update when policy.UpdateSafety = RequirePredicate ->
                match update.Where with
                | None ->
                    deny "SQL_POLICY_UPDATE_REQUIRES_WHERE" document.Span "Security policy denies UPDATE without WHERE."
                | Some expression when constantPredicate expression = AlwaysTrue ->
                    denyUnboundedPredicate "SQL_POLICY_UPDATE_UNBOUNDED_PREDICATE" document expression
                | Some _ -> UpdateStatement update
            | DeleteStatement delete when policy.DeleteSafety = RequirePredicate ->
                match delete.Where with
                | None ->
                    deny "SQL_POLICY_DELETE_REQUIRES_WHERE" document.Span "Security policy denies DELETE without WHERE."
                | Some expression when constantPredicate expression = AlwaysTrue ->
                    denyUnboundedPredicate "SQL_POLICY_DELETE_UNBOUNDED_PREDICATE" document expression
                | Some _ -> DeleteStatement delete
            | statement -> statement
        { document with Statement = statement }

    let authorize policy (validated: RewriteStages.ValidatedSql) =
        RewriteResourceBudget.checkpoint ()
        let targetRuntime = RewriteStages.Validated.targetRuntime validated
        let document = RewriteStages.Validated.value validated
        ExecutableSql(authorizeDocument policy document, targetRuntime)
