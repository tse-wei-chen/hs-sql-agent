namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy

/// Mutation-only policy: no query plan can enter this authorization boundary.
module internal RewriteMutationPolicy =

    let private deny code span message : 'T = RewritePolicy.deny code span message

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

    // Compare only same-kind exact SQL numeric literals. Never infer outcomes
    // through implicit provider casts, floating-point NaN, text collation or
    // nullable operands: those remain UnknownTruth.
    let private literalOrdering left right =
        match Expr.unspan left, Expr.unspan right with
        | Literal(ScalarValue.Integer x), Literal(ScalarValue.Integer y) -> Some(compare x y)
        | Literal(ScalarValue.Decimal x), Literal(ScalarValue.Decimal y) -> Some(compare x y)
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
                | Binary(BinaryOperator.LessThan, left, right) ->
                    values.Push(literalOrdering left right |> Option.map (fun c -> truth (c < 0)) |> Option.defaultValue UnknownTruth)
                | Binary(BinaryOperator.LessThanOrEqual, left, right) ->
                    values.Push(literalOrdering left right |> Option.map (fun c -> truth (c <= 0)) |> Option.defaultValue UnknownTruth)
                | Binary(BinaryOperator.GreaterThan, left, right) ->
                    values.Push(literalOrdering left right |> Option.map (fun c -> truth (c > 0)) |> Option.defaultValue UnknownTruth)
                | Binary(BinaryOperator.GreaterThanOrEqual, left, right) ->
                    values.Push(literalOrdering left right |> Option.map (fun c -> truth (c >= 0)) |> Option.defaultValue UnknownTruth)
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


    let apply (updateSafety: MutationSafety) (deleteSafety: MutationSafety) (validated: RewriteStages.ValidatedSql) =
        RewriteResourceBudget.checkpoint ()
        let document = RewriteStages.Validated.value validated
        let statement =
            match document.Statement with
            | QueryStatement _ -> invalidOp "Mutation policy cannot authorize a query statement."
            | UpdateStatement update when updateSafety = RequirePredicate ->
                match update.Where with
                | None ->
                    deny "SQL_POLICY_UPDATE_REQUIRES_WHERE" document.Span "Security policy denies UPDATE without WHERE."
                | Some expression when constantPredicate expression = AlwaysTrue ->
                    denyUnboundedPredicate "SQL_POLICY_UPDATE_UNBOUNDED_PREDICATE" document expression
                | Some _ -> UpdateStatement update
            | DeleteStatement delete when deleteSafety = RequirePredicate ->
                match delete.Where with
                | None ->
                    deny "SQL_POLICY_DELETE_REQUIRES_WHERE" document.Span "Security policy denies DELETE without WHERE."
                | Some expression when constantPredicate expression = AlwaysTrue ->
                    denyUnboundedPredicate "SQL_POLICY_DELETE_UNBOUNDED_PREDICATE" document expression
                | Some _ -> DeleteStatement delete
            | InsertStatement insert -> InsertStatement insert
            | UpdateStatement update -> UpdateStatement update
            | DeleteStatement delete -> DeleteStatement delete
            | MergeStatement merge -> MergeStatement merge
        RewritePolicy.Executable.create validated statement
