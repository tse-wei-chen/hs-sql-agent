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
                    let ordered = NonEmpty.toList branches |> List.toArray
                    for index = ordered.Length - 1 downto 0 do
                        push ordered.[index].Result
                        push ordered.[index].Match
                    push input
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter push
                    let ordered = NonEmpty.toList branches |> List.toArray
                    for index = ordered.Length - 1 downto 0 do
                        push ordered.[index].Result
                        push ordered.[index].Condition
                | InList(value, items, _) ->
                    pushReverse (NonEmpty.toList items)
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
