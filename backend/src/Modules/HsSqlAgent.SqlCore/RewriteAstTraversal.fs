namespace HsSqlAgent.SqlCore.Rewrite

open System.Collections.Generic
open HsSqlAgent.SqlCore.Rewrite.CoreModel

/// Explicit, depth-independent (of the CLR call stack) pre-order traversal
/// of scalar expression trees. The logical SQL AST stays a closed DU.
module internal RewriteAstTraversal =
    /// Returns true when a matching node is encountered; immediately stops
    /// walking so left-to-right short-circuit semantics are preserved.
    ///
    /// Subquery bodies are deliberately excluded: these callers inspect only
    /// their enclosing scalar expression, exactly as before this refactor.
    /// InSubquery still visits its left-hand scalar value. The aggregate
    /// ordering column-reference rule historically checks only the BETWEEN
    /// value (not its bounds); all other callers traverse all three operands.
    let exists (visitBetweenBounds: bool) (matches: Expr -> bool) (root: Expr) =
        let pending = ResizeArray<Expr>()
        pending.Add root
        let inline push item = pending.Add item

        // Reverse push into the explicit LIFO work stack. This preserves
        // the previous recursive walk's source order and first-error behavior.
        let pushReverse (items: Expr list) =
            let buffer = List.toArray items
            for index = buffer.Length - 1 downto 0 do
                push buffer.[index]

        let mutable found = false
        while pending.Count > 0 && not found do
            let last = pending.Count - 1
            let expression = pending.[last]
            pending.RemoveAt(last)
            if matches expression then
                found <- true
            else
                match expression with
                | Spanned(_, inner) -> push inner
                | Unary(_, value)
                | Cast(value, _)
                | Extract(_, value)
                | PostgresJsonAccess(value, _, _)
                | IsNull(value, _) -> push value
                | DateAdd(_, left, right)
                | DateDiff(_, left, right)
                | Binary(_, left, right)
                | RegexMatch(left, right)
                | FilteredAggregate(left, right)
                | Like(left, right, _, _, _) ->
                    push right
                    push left
                | RawRegexCall(arguments, _) -> pushReverse arguments
                | FunctionCall call ->
                    pushReverse (call.AggregateOrderBy |> List.map (fun order -> order.Expression))
                    pushReverse call.Arguments
                | Windowed(value, window) ->
                    pushReverse (window.OrderBy |> List.map (fun order -> order.Expression))
                    pushReverse window.PartitionBy
                    push value
                | SimpleCase(input, branches, fallback) ->
                    fallback |> Option.iter push
                    NonEmpty.iterReverse (fun branch ->
                        push branch.Result
                        push branch.Match) branches
                    push input
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter push
                    NonEmpty.iterReverse (fun branch ->
                        push branch.Result
                        push branch.Condition) branches
                | InList(value, items, _) ->
                    NonEmpty.iterReverse push items
                    push value
                | InSubquery(value, _, _) -> push value
                | Between(value, lower, upper, _) ->
                    if visitBetweenBounds then
                        push upper
                        push lower
                    push value
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ | ScalarSubquery _ | Exists _ -> ()
        found

    /// Validates every visited node without recursive CLR calls.
    /// Visitor exceptions propagate unmodified, preserving SQL diagnostics.
    let iter (visit: Expr -> unit) (root: Expr) =
        exists true (fun node -> visit node; false) root |> ignore

    /// Struct DU frames avoid one managed object per nested SQL node.
    /// Explicit work capacity grows with nesting, not the CLR call stack.
    /// Explicit continuation frames for policy traversal across queries,
    /// sources and expressions. The two After* phases retain the previous
    /// recursive validator's precise ordering of policy errors.
    [<Struct>]
    type Frame =
        | ExprNode of expression: Expr
        | QueryNode of query: Query
        | SelectNode of selection: Select
        | SourceNode of source: TableSource
        | AfterCtes of selectBody: Select
        | AfterQueryHead of queryTail: Query

    /// Whole-query pre-order traversal with explicit continuation phases.
    /// No recursive F# function call is needed for nested queries or
    /// arbitrarily long expression chains.
    let iterIncludingQueries (visit: Frame -> unit) (root: Frame) =
        let pending = ResizeArray<Frame>()
        let inline push frame = pending.Add frame
        let pushExprs (expressions: Expr list) =
            let buffer = List.toArray expressions
            for index = buffer.Length - 1 downto 0 do
                push (ExprNode buffer.[index])
        let pushOrderBy (items: OrderBy list) =
            let buffer = List.toArray items
            for index = buffer.Length - 1 downto 0 do
                push (ExprNode buffer.[index].Expression)

        push root
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            visit frame
            match frame with
            | ExprNode expression ->
                match expression with
                | Spanned(_, inner) -> push (ExprNode inner)
                | Unary(_, value)
                | Cast(value, _)
                | Extract(_, value)
                | PostgresJsonAccess(value, _, _)
                | IsNull(value, _) -> push (ExprNode value)
                | DateAdd(_, left, right)
                | DateDiff(_, left, right)
                | Binary(_, left, right)
                | RegexMatch(left, right)
                | FilteredAggregate(left, right)
                | Like(left, right, _, _, _) ->
                    push (ExprNode right)
                    push (ExprNode left)
                | RawRegexCall(arguments, _) -> pushExprs arguments
                | FunctionCall call ->
                    pushOrderBy call.AggregateOrderBy
                    pushExprs call.Arguments
                | Windowed(value, window) ->
                    pushOrderBy window.OrderBy
                    pushExprs window.PartitionBy
                    push (ExprNode value)
                | SimpleCase(input, branches, fallback) ->
                    fallback |> Option.iter (fun value -> push (ExprNode value))
                    NonEmpty.iterReverse (fun branch ->
                        push (ExprNode branch.Result)
                        push (ExprNode branch.Match)) branches
                    push (ExprNode input)
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter (fun value -> push (ExprNode value))
                    NonEmpty.iterReverse (fun branch ->
                        push (ExprNode branch.Result)
                        push (ExprNode branch.Condition)) branches
                | InList(value, items, _) ->
                    NonEmpty.iterReverse (fun item -> push (ExprNode item)) items
                    push (ExprNode value)
                | InSubquery(value, query, _) ->
                    push (QueryNode query)
                    push (ExprNode value)
                | Between(value, lower, upper, _) ->
                    push (ExprNode upper)
                    push (ExprNode lower)
                    push (ExprNode value)
                | ScalarSubquery query | Exists(query, _) -> push (QueryNode query)
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ -> ()
            | QueryNode query ->
                push (AfterQueryHead query)
                push (SelectNode query.Head)
            | SelectNode select ->
                push (AfterCtes select)
                let ctes = List.toArray select.Ctes
                for index = ctes.Length - 1 downto 0 do
                    push (QueryNode ctes.[index].Query)
            | AfterCtes select ->
                // Previous validator: FROM, DISTINCT ON, projections, WHERE,
                // GROUP BY, HAVING, then JOIN sources and ON predicates.
                let joins = List.toArray select.Joins
                for index = joins.Length - 1 downto 0 do
                    match joins.[index] with
                    | OnJoin(_, source, predicate) ->
                        push (ExprNode predicate)
                        push (SourceNode source)
                    | CrossJoin source
                    | NaturalJoin(_, source)
                    | UsingJoin(_, source, _) -> push (SourceNode source)
                select.Having |> Option.iter (fun value -> push (ExprNode value))
                pushExprs select.GroupBy
                select.Where |> Option.iter (fun value -> push (ExprNode value))
                NonEmpty.iterReverse (fun item ->
                    push (ExprNode item.Expression)) select.ProjectionItems
                match select.DistinctMode with
                | SelectDistinct.DistinctOn values ->
                    NonEmpty.iterReverse (fun value -> push (ExprNode value)) values
                | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> ()
                select.From |> Option.iter (fun value -> push (SourceNode value))
            | SourceNode source ->
                match source with
                | NamedTable _ | CteTable _ -> ()
                | DerivedTable(query, _) | LateralDerivedTable(query, _) ->
                    push (QueryNode query)
            | AfterQueryHead query ->
                pushOrderBy query.OrderBy
                let branches = List.toArray query.SetOperations
                for index = branches.Length - 1 downto 0 do
                    push (QueryNode branches.[index].Query)
