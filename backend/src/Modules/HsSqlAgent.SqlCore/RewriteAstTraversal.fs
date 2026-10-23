namespace HsSqlAgent.SqlCore.Rewrite

open System.Collections.Generic
open HsSqlAgent.SqlCore.Rewrite.CoreModel

/// Explicit, depth-independent (of the CLR call stack) pre-order traversal
/// of scalar expression trees. The logical SQL AST stays a closed DU.
module internal RewriteAstTraversal =

    /// Narrow, semantics-free Query/Expr/Query bridge. Clauses that can
    /// change diagnostic order, binding scope, or output shape do not enter.
    let trySimpleScalarChain (query: Query) : (SelectItem * Query) option =
        let select = query.Head
        if select.Ctes.IsEmpty
           && select.DistinctMode = SelectDistinct.AllRows
           && select.From.IsNone && select.Joins.IsEmpty
           && select.Where.IsNone && select.GroupBy.IsEmpty
           && select.Having.IsNone
           && query.SetOperations.IsEmpty && query.OrderBy.IsEmpty
           && query.Limit.IsNone && query.Offset.IsNone
           && query.FetchPercent.IsNone && not query.FetchWithTies then
            match NonEmpty.toList select.ProjectionItems with
            | [ item ] ->
                match Expr.unspan item.Expression with
                | ScalarSubquery child -> Some(item, child)
                | _ -> None
            | _ -> None
        else None

    /// Preserve the original projection alias and outermost SourceSpan, as
    /// Expr.withSpan does when a rebuilt expression is already spanned.
    let rebuildSimpleScalarChain (original: Query) (item: SelectItem) (child: Query) =
        let expression =
            match item.Expression with
            | Spanned(span, _) -> Expr.withSpan span (ScalarSubquery child)
            | _ -> ScalarSubquery child
        { original with
            Head =
                { original.Head with
                    ProjectionItems = NonEmpty.create { item with Expression = expression } [] } }

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
            RewriteResourceBudget.checkpoint ()
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
            RewriteResourceBudget.checkpoint ()
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
                    NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                        push (ExprNode branch.Result)
                        push (ExprNode branch.Match)) branches
                    push (ExprNode input)
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter (fun value -> push (ExprNode value))
                    NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
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

    // General scalar-expression post-order reconstruction shared by Binder
    // and Normalizer. Explicit enter/exit frames and a result stack replace
    // recursive map calls for all closed Expr DU constructors. Query bodies
    // remain explicit semantic boundaries; the onQuery callback retains the
    // caller's current CTE/correlation scope.
    [<Struct>]
    type private RebuildFrame =
        | RebuildEnter of node: Expr
        | RebuildExit of originalNode: Expr

    let mapPostOrder
        (onEnter: Expr -> unit)
        (onRebuilt: Expr -> Expr)
        (onQuery: Query -> Query)
        (root: Expr) : Expr =
        // Most scalar-subquery wrappers and leaf projections have no
        // scalar children. Avoid allocating two work buffers and per-node
        // continuation frames for these common cases, while still invoking
        // each visitor callback in exactly the same order as the full walk.
        let isTerminal = function
            | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
            | Literal _ | Interval _ | ScalarSubquery _ | Exists _ -> true
            | _ -> false
        let finishTerminal node =
            let mapped =
                match node with
                | ScalarSubquery query -> ScalarSubquery(onQuery query)
                | Exists(query, negated) -> Exists(onQuery query, negated)
                | _ -> node
            onRebuilt mapped
        match root with
        | Spanned(span, inner) when isTerminal inner ->
            onEnter root
            onEnter inner
            onRebuilt (Expr.withSpan span (finishTerminal inner))
        | terminal when isTerminal terminal ->
            onEnter terminal
            finishTerminal terminal
        | _ ->
            let pending = ResizeArray<RebuildFrame>()
            let results = ResizeArray<Expr>()
            let inline enter node = pending.Add(RebuildEnter node)
            let pop () =
                let last = results.Count - 1
                if last < 0 then
                    invalidOp "Expression post-order result stack underflow."
                let result = results.[last]
                results.RemoveAt last
                result
            let popList count =
                let mutable rebuilt = []
                for _ = 1 to count do
                    rebuilt <- pop () :: rebuilt
                rebuilt
            let popOptional (value: Expr option) =
                if value.IsSome then Some(pop ()) else None
            let enterReverse (values: Expr list) =
                let items = List.toArray values
                for index = items.Length - 1 downto 0 do
                    enter items.[index]
            let enterOrders (values: OrderBy list) =
                let items: OrderBy array = List.toArray values
                for index = items.Length - 1 downto 0 do
                    enter items.[index].Expression
            let rebuildOrders (values: OrderBy list) : OrderBy list =
                let mapped = popList (List.length values)
                List.map2 (fun (order: OrderBy) (expr: Expr) ->
                    { order with Expression = expr }) values mapped

            enter root
            while pending.Count > 0 do
                RewriteResourceBudget.checkpoint ()
                let last = pending.Count - 1
                let frame = pending.[last]
                pending.RemoveAt last
                match frame with
                | RebuildEnter node ->
                    onEnter node
                    pending.Add(RebuildExit node)
                    // Push rightmost child first; each child is rebuilt before
                    // its parent's exit frame, preserving left-to-right binding.
                    match node with
                    | Spanned(_, inner)
                    | Unary(_, inner)
                    | Cast(inner, _)
                    | Extract(_, inner)
                    | PostgresJsonAccess(inner, _, _)
                    | IsNull(inner, _) -> enter inner
                    | DateAdd(_, left, right)
                    | DateDiff(_, left, right)
                    | Binary(_, left, right)
                    | Like(left, right, _, _, _)
                    | RegexMatch(left, right)
                    | FilteredAggregate(left, right) ->
                        enter right
                        enter left
                    | RawRegexCall(args, _) -> enterReverse args
                    | FunctionCall call ->
                        enterOrders call.AggregateOrderBy
                        enterReverse call.Arguments
                    | Windowed(value, window) ->
                        enterOrders window.OrderBy
                        enterReverse window.PartitionBy
                        enter value
                    | SimpleCase(input, branches, fallback) ->
                        fallback |> Option.iter enter
                        NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                            enter branch.Result
                            enter branch.Match) branches
                        enter input
                    | SearchedCase(branches, fallback) ->
                        fallback |> Option.iter enter
                        NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                            enter branch.Result
                            enter branch.Condition) branches
                    | InList(value, items, _) ->
                        NonEmpty.iterReverse enter items
                        enter value
                    | InSubquery(value, _, _) -> enter value
                    | Between(value, lower, upper, _) ->
                        enter upper
                        enter lower
                        enter value
                    | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                    | Literal _ | Interval _ | ScalarSubquery _ | Exists _ -> ()
                | RebuildExit node ->
                    let rebuilt =
                        match node with
                        | Spanned(span, _) -> Expr.withSpan span (pop ())
                        | Unary(op, _) -> Unary(op, pop ())
                        | Cast(_, targetType) -> Cast(pop (), targetType)
                        | Extract(field, _) -> Extract(field, pop ())
                        | PostgresJsonAccess(_, selector, kind) ->
                            PostgresJsonAccess(pop (), selector, kind)
                        | IsNull(_, negated) -> IsNull(pop (), negated)
                        | DateAdd(unit, _, _) ->
                            let value = pop ()
                            let amount = pop ()
                            DateAdd(unit, amount, value)
                        | DateDiff(unit, _, _) ->
                            let finish = pop ()
                            let start = pop ()
                            DateDiff(unit, start, finish)
                        | Binary(op, _, _) ->
                            let right = pop ()
                            let left = pop ()
                            Binary(op, left, right)
                        | Like(_, _, escape, negated, insensitive) ->
                            let pattern = pop ()
                            let value = pop ()
                            Like(value, pattern, escape, negated, insensitive)
                        | RegexMatch _ ->
                            let pattern = pop ()
                            let value = pop ()
                            RegexMatch(value, pattern)
                        | FilteredAggregate _ ->
                            let predicate = pop ()
                            let value = pop ()
                            FilteredAggregate(value, predicate)
                        | RawRegexCall(args, distinct) ->
                            RawRegexCall(popList (List.length args), distinct)
                        | FunctionCall call ->
                            // The result stack has ORDER BY items on top of args.
                            let orders = rebuildOrders call.AggregateOrderBy
                            let args = popList (List.length call.Arguments)
                            FunctionCall { call with Arguments = args; AggregateOrderBy = orders }
                        | Windowed(_, window) ->
                            let orders = rebuildOrders window.OrderBy
                            let partition = popList (List.length window.PartitionBy)
                            let value = pop ()
                            Windowed(value, { window with PartitionBy = partition; OrderBy = orders })
                        | SimpleCase(_, branches, fallback) ->
                            let other = popOptional fallback
                            let original = NonEmpty.toList branches |> List.toArray
                            let mapped = Array.zeroCreate<SimpleCaseBranch> original.Length
                            for index = original.Length - 1 downto 0 do
                                let result = pop ()
                                let matchExpr = pop ()
                                mapped.[index] <- { Match = matchExpr; Result = result }
                            let input = pop ()
                            SimpleCase(input, NonEmpty.ofList "SimpleCase" (Array.toList mapped), other)
                        | SearchedCase(branches, fallback) ->
                            let other = popOptional fallback
                            let original = NonEmpty.toList branches |> List.toArray
                            let mapped = Array.zeroCreate<SearchedCaseBranch> original.Length
                            for index = original.Length - 1 downto 0 do
                                let result = pop ()
                                let condition = pop ()
                                mapped.[index] <- { Condition = condition; Result = result }
                            SearchedCase(NonEmpty.ofList "SearchedCase" (Array.toList mapped), other)
                        | InList(_, items, negated) ->
                            let mapped = popList (NonEmpty.length items)
                            InList(pop (), NonEmpty.ofList "InList" mapped, negated)
                        | InSubquery(_, query, negated) ->
                            let value = pop ()
                            InSubquery(value, onQuery query, negated)
                        | Between(_, _, _, negated) ->
                            let upper = pop ()
                            let lower = pop ()
                            let value = pop ()
                            Between(value, lower, upper, negated)
                        | ScalarSubquery query -> ScalarSubquery(onQuery query)
                        | Exists(query, negated) -> Exists(onQuery query, negated)
                        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                        | Literal _ | Interval _ -> node
                    results.Add(onRebuilt rebuilt)
            if results.Count <> 1 then
                invalidOp "Expression post-order frames did not reconstruct one root."
            results.[0]

    // A single stack reconstructs the mixed Query -> Select -> Expr ->
    // Query / Source -> Query graph. Unlike scalar mapPostOrder, it never
    // calls back into a Query recursively. All children are visited in the
    // historical Normalizer source/diagnostic order.
    [<Struct>]
    type private GraphRebuildFrame =
        | EnterGraphExpr of enteringExpr: Expr
        | ExitGraphExpr of leavingExpr: Expr
        | EnterGraphQuery of enteringQuery: Query
        | ExitGraphQuery of leavingQuery: Query
        | EnterGraphSelect of enteringSelect: Select
        | ExitGraphSelect of leavingSelect: Select
        | EnterGraphCte of enteringCte: Cte
        | ExitGraphCte of leavingCte: Cte
        | EnterGraphSource of enteringSource: TableSource
        | ExitGraphSource of leavingSource: TableSource
        | EnterGraphJoin of enteringJoin: Join
        | ExitGraphJoin of leavingJoin: Join

    type private GraphRebuildRoot =
        | QueryGraphRoot of rootQuery: Query
        | SourceGraphRoot of rootSource: TableSource

    let private mapGraphPostOrder
        (onEnterExpr: Expr -> unit)
        (onRebuiltExpr: Expr -> Expr)
        (onEnterQuery: Query -> unit)
        (onRebuiltCte: Cte -> Cte)
        (root: GraphRebuildRoot) =
        let pending = ResizeArray<GraphRebuildFrame>()
        let expressions = ResizeArray<Expr>()
        let queries = ResizeArray<Query>()
        let selects = ResizeArray<Select>()
        let sources = ResizeArray<TableSource>()
        let joins = ResizeArray<Join>()
        let ctes = ResizeArray<Cte>()

        let popResult (results: ResizeArray<'T>) =
            let last = results.Count - 1
            if last < 0 then invalidOp "Mixed AST post-order result stack underflow."
            let value = results.[last]
            results.RemoveAt(last)
            value
        let popExpr () = popResult expressions
        let popQuery () = popResult queries
        let popSelect () = popResult selects
        let popSource () = popResult sources
        let popJoin () = popResult joins
        let popCte () = popResult ctes
        let popList count pop =
            let mutable mapped = []
            for _ = 1 to count do
                mapped <- pop () :: mapped
            mapped
        let popOptional option pop =
            if Option.isSome option then Some(pop ()) else None
        let pushExpr value = pending.Add(EnterGraphExpr value)
        let pushQuery value = pending.Add(EnterGraphQuery value)
        let pushSource value = pending.Add(EnterGraphSource value)
        let pushReverse (values: 'T list) push =
            let entries = List.toArray values
            for index = entries.Length - 1 downto 0 do
                push entries.[index]
        let pushExprs values = pushReverse values pushExpr
        let pushOrders (orders: OrderBy list) =
            pushReverse orders (fun order -> pushExpr order.Expression)
        let popOrders (orders: OrderBy list) =
            let mapped = popList orders.Length popExpr
            List.map2 (fun (order: OrderBy) expression ->
                { order with Expression = expression }) orders mapped

        match root with
        | QueryGraphRoot query -> pushQuery query
        | SourceGraphRoot source -> pushSource source

        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last
            match frame with
            | EnterGraphExpr expression ->
                onEnterExpr expression
                pending.Add(ExitGraphExpr expression)
                match expression with
                | Spanned(_, inner) | Unary(_, inner) | Cast(inner, _)
                | Extract(_, inner) | PostgresJsonAccess(inner, _, _)
                | IsNull(inner, _) -> pushExpr inner
                | DateAdd(_, left, right) | DateDiff(_, left, right)
                | Binary(_, left, right) | Like(left, right, _, _, _)
                | RegexMatch(left, right) | FilteredAggregate(left, right) ->
                    pushExpr right
                    pushExpr left
                | RawRegexCall(arguments, _) -> pushExprs arguments
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
                    pushQuery query
                    pushExpr value
                | Between(value, lower, upper, _) ->
                    pushExpr upper
                    pushExpr lower
                    pushExpr value
                | ScalarSubquery query | Exists(query, _) -> pushQuery query
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ -> ()
            | ExitGraphExpr expression ->
                let rebuilt =
                    match expression with
                    | Spanned(span, _) -> Expr.withSpan span (popExpr ())
                    | Unary(op, _) -> Unary(op, popExpr ())
                    | Cast(_, targetType) -> Cast(popExpr (), targetType)
                    | Extract(field, _) -> Extract(field, popExpr ())
                    | PostgresJsonAccess(_, selector, kind) ->
                        PostgresJsonAccess(popExpr (), selector, kind)
                    | IsNull(_, negated) -> IsNull(popExpr (), negated)
                    | DateAdd(unit, _, _) ->
                        let value = popExpr ()
                        let amount = popExpr ()
                        DateAdd(unit, amount, value)
                    | DateDiff(unit, _, _) ->
                        let finish = popExpr ()
                        let start = popExpr ()
                        DateDiff(unit, start, finish)
                    | Binary(op, _, _) ->
                        let right = popExpr ()
                        let left = popExpr ()
                        Binary(op, left, right)
                    | Like(_, _, escape, negated, insensitive) ->
                        let pattern = popExpr ()
                        let value = popExpr ()
                        Like(value, pattern, escape, negated, insensitive)
                    | RegexMatch _ ->
                        let pattern = popExpr ()
                        let value = popExpr ()
                        RegexMatch(value, pattern)
                    | FilteredAggregate _ ->
                        let predicate = popExpr ()
                        let value = popExpr ()
                        FilteredAggregate(value, predicate)
                    | RawRegexCall(args, distinct) ->
                        RawRegexCall(popList args.Length popExpr, distinct)
                    | FunctionCall call ->
                        let orders = popOrders call.AggregateOrderBy
                        let arguments = popList call.Arguments.Length popExpr
                        FunctionCall { call with Arguments = arguments; AggregateOrderBy = orders }
                    | Windowed(_, window) ->
                        let orders = popOrders window.OrderBy
                        let partition = popList window.PartitionBy.Length popExpr
                        let value = popExpr ()
                        Windowed(value, { window with PartitionBy = partition; OrderBy = orders })
                    | SimpleCase(_, branches, fallback) ->
                        let other = popOptional fallback popExpr
                        let original = NonEmpty.toList branches |> List.toArray
                        let mapped = Array.zeroCreate<SimpleCaseBranch> original.Length
                        for index = original.Length - 1 downto 0 do
                            let result = popExpr ()
                            let matchExpr = popExpr ()
                            mapped.[index] <- { Match = matchExpr; Result = result }
                        let input = popExpr ()
                        SimpleCase(input, NonEmpty.ofList "SimpleCase" (Array.toList mapped), other)
                    | SearchedCase(branches, fallback) ->
                        let other = popOptional fallback popExpr
                        let original = NonEmpty.toList branches |> List.toArray
                        let mapped = Array.zeroCreate<SearchedCaseBranch> original.Length
                        for index = original.Length - 1 downto 0 do
                            let result = popExpr ()
                            let condition = popExpr ()
                            mapped.[index] <- { Condition = condition; Result = result }
                        SearchedCase(NonEmpty.ofList "SearchedCase" (Array.toList mapped), other)
                    | InList(_, items, negated) ->
                        let mapped = popList (NonEmpty.length items) popExpr
                        InList(popExpr (), NonEmpty.ofList "InList" mapped, negated)
                    | InSubquery(_, _, negated) ->
                        let query = popQuery ()
                        InSubquery(popExpr (), query, negated)
                    | Between(_, _, _, negated) ->
                        let upper = popExpr ()
                        let lower = popExpr ()
                        let value = popExpr ()
                        Between(value, lower, upper, negated)
                    | ScalarSubquery _ -> ScalarSubquery(popQuery ())
                    | Exists(_, negated) -> Exists(popQuery (), negated)
                    | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                    | Literal _ | Interval _ -> expression
                expressions.Add(onRebuiltExpr rebuilt)
            | EnterGraphQuery query ->
                onEnterQuery query
                pending.Add(ExitGraphQuery query)
                pushOrders query.OrderBy
                pushReverse query.SetOperations (fun branch -> pushQuery branch.Query)
                pending.Add(EnterGraphSelect query.Head)
            | ExitGraphQuery query ->
                let orders = popOrders query.OrderBy
                let branchQueries = popList query.SetOperations.Length popQuery
                let branches =
                    List.map2 (fun (branch: SetBranch) rebuilt ->
                        { branch with Query = rebuilt }) query.SetOperations branchQueries
                queries.Add
                    { query with
                        Head = popSelect ()
                        SetOperations = branches
                        OrderBy = orders }
            | EnterGraphSelect select ->
                pending.Add(ExitGraphSelect select)
                select.Having |> Option.iter pushExpr
                pushExprs select.GroupBy
                select.Where |> Option.iter pushExpr
                pushReverse select.Joins (fun join -> pending.Add(EnterGraphJoin join))
                select.From |> Option.iter pushSource
                pushReverse (NonEmpty.toList select.ProjectionItems)
                    (fun item -> pushExpr item.Expression)
                match select.DistinctMode with
                | SelectDistinct.DistinctOn values ->
                    NonEmpty.iterReverse pushExpr values
                | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> ()
                pushReverse select.Ctes (fun cte -> pending.Add(EnterGraphCte cte))
            | ExitGraphSelect select ->
                let having = popOptional select.Having popExpr
                let groupBy = popList select.GroupBy.Length popExpr
                let where = popOptional select.Where popExpr
                let mappedJoins = popList select.Joins.Length popJoin
                let from = popOptional select.From popSource
                let originalItems = NonEmpty.toList select.ProjectionItems
                let items = popList originalItems.Length popExpr
                let projections =
                    List.map2 (fun (item: SelectItem) value ->
                        { item with Expression = value }) originalItems items
                    |> NonEmpty.ofList "Select projection"
                let distinctMode =
                    match select.DistinctMode with
                    | SelectDistinct.DistinctOn values ->
                        SelectDistinct.DistinctOn(
                            NonEmpty.ofList "DISTINCT ON"
                                (popList (NonEmpty.length values) popExpr))
                    | mode -> mode
                let mappedCtes = popList select.Ctes.Length popCte
                selects.Add
                    { select with
                        Ctes = mappedCtes
                        DistinctMode = distinctMode
                        ProjectionItems = projections
                        From = from
                        Joins = mappedJoins
                        Where = where
                        GroupBy = groupBy
                        Having = having }
            | EnterGraphCte cte ->
                pending.Add(ExitGraphCte cte)
                pushQuery cte.Query
            | ExitGraphCte cte ->
                ctes.Add(onRebuiltCte { cte with Query = popQuery () })
            | EnterGraphSource source ->
                pending.Add(ExitGraphSource source)
                match source with
                | DerivedTable(query, _) | LateralDerivedTable(query, _) -> pushQuery query
                | NamedTable _ | CteTable _ -> ()
            | ExitGraphSource source ->
                let mapped =
                    match source with
                    | DerivedTable(_, alias) -> DerivedTable(popQuery (), alias)
                    | LateralDerivedTable(_, alias) -> LateralDerivedTable(popQuery (), alias)
                    | NamedTable _ | CteTable _ -> source
                sources.Add mapped
            | EnterGraphJoin join ->
                pending.Add(ExitGraphJoin join)
                match join with
                | OnJoin(_, source, predicate) ->
                    pushExpr predicate
                    pushSource source
                | CrossJoin source | NaturalJoin(_, source)
                | UsingJoin(_, source, _) -> pushSource source
            | ExitGraphJoin join ->
                let mapped =
                    match join with
                    | OnJoin(kind, _, _) ->
                        let predicate = popExpr ()
                        OnJoin(kind, popSource (), predicate)
                    | CrossJoin _ -> CrossJoin(popSource ())
                    | NaturalJoin(kind, _) -> NaturalJoin(kind, popSource ())
                    | UsingJoin(kind, _, columns) -> UsingJoin(kind, popSource (), columns)
                joins.Add mapped

        match root with
        | QueryGraphRoot _ when queries.Count = 1 -> Choice1Of2 queries.[0]
        | SourceGraphRoot _ when sources.Count = 1 -> Choice2Of2 sources.[0]
        | _ -> invalidOp "Mixed AST post-order frames did not reconstruct one root."

    let mapQueryGraphPostOrder onEnterExpr onRebuiltExpr onEnterQuery onRebuiltCte query =
        match mapGraphPostOrder
                  onEnterExpr onRebuiltExpr onEnterQuery onRebuiltCte
                  (QueryGraphRoot query) with
        | Choice1Of2 result -> result
        | Choice2Of2 _ -> invalidOp "Query graph mapper returned a source."

    let mapSourceGraphPostOrder onEnterExpr onRebuiltExpr onEnterQuery onRebuiltCte source =
        match mapGraphPostOrder
                  onEnterExpr onRebuiltExpr onEnterQuery onRebuiltCte
                  (SourceGraphRoot source) with
        | Choice2Of2 result -> result
        | Choice1Of2 _ -> invalidOp "Source graph mapper returned a query."
