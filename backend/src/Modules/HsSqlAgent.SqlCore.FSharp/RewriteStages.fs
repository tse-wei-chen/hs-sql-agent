namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Canonicalization and structural validation for the rewrite pipeline.
module internal RewriteStages =

    let rec private normalizeExpr expression =
        match expression with
        | Column _
        | Literal _
        | Interval _ -> expression
        | Unary(Positive, operand) -> normalizeExpr operand
        | Unary(op, operand) -> Unary(op, normalizeExpr operand)
        | Binary(op, left, right) -> Binary(op, normalizeExpr left, normalizeExpr right)
        | FunctionCall call ->
            FunctionCall { call with Arguments = call.Arguments |> List.map normalizeExpr }
        | FilteredAggregate(value, predicate) ->
            FilteredAggregate(normalizeExpr value, normalizeExpr predicate)
        | Windowed(value, window) ->
            Windowed(normalizeExpr value, normalizeWindow window)
        | Cast(value, targetType) -> Cast(normalizeExpr value, targetType)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(
                normalizeExpr input,
                branches |> List.map (fun branch -> { Match = normalizeExpr branch.Match; Result = normalizeExpr branch.Result }),
                fallback |> Option.map normalizeExpr)
        | SearchedCase(branches, fallback) ->
            SearchedCase(
                branches |> List.map (fun branch -> { Condition = normalizeExpr branch.Condition; Result = normalizeExpr branch.Result }),
                fallback |> Option.map normalizeExpr)
        | InList(value, items, negated) ->
            InList(normalizeExpr value, items |> List.map normalizeExpr, negated)
        | Between(value, lower, upper, negated) ->
            Between(normalizeExpr value, normalizeExpr lower, normalizeExpr upper, negated)
        | IsNull(value, negated) -> IsNull(normalizeExpr value, negated)
        | ScalarSubquery query -> ScalarSubquery(normalizeQuery query)
        | Exists(query, negated) -> Exists(normalizeQuery query, negated)

    and private normalizeWindow window =
        { window with
            PartitionBy = window.PartitionBy |> List.map normalizeExpr
            OrderBy = window.OrderBy |> List.map normalizeOrderBy }

    and private normalizeOrderBy orderBy =
        { orderBy with Expression = normalizeExpr orderBy.Expression }

    and private normalizeSource source =
        match source with
        | NamedTable _ -> source
        | DerivedTable(query, alias) -> DerivedTable(normalizeQuery query, alias)

    and private normalizeSelect select =
        { select with
            Projection = select.Projection |> List.map (fun item -> { item with Expression = normalizeExpr item.Expression })
            From = select.From |> Option.map normalizeSource
            Joins =
                select.Joins
                |> List.map (fun join ->
                    { join with
                        Source = normalizeSource join.Source
                        Predicate = join.Predicate |> Option.map normalizeExpr })
            Where = select.Where |> Option.map normalizeExpr
            GroupBy = select.GroupBy |> List.map normalizeExpr
            Having = select.Having |> Option.map normalizeExpr }

    and private normalizeQuery query =
        { query with
            Head = normalizeSelect query.Head
            SetOperations = query.SetOperations |> List.map (fun branch -> { branch with Query = normalizeQuery branch.Query })
            OrderBy = query.OrderBy |> List.map normalizeOrderBy }

    let private normalizeDocument document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(normalizeQuery query)
            | InsertStatement insert ->
                InsertStatement
                    { insert with
                        Rows = insert.Rows |> List.map (List.map normalizeExpr)
                        Source = insert.Source |> Option.map normalizeQuery
                        Returning = insert.Returning |> List.map (fun item -> { item with Expression = normalizeExpr item.Expression }) }
            | UpdateStatement update ->
                UpdateStatement
                    { update with
                        Assignments = update.Assignments |> List.map (fun assignment -> { assignment with Value = normalizeExpr assignment.Value })
                        Where = update.Where |> Option.map normalizeExpr
                        Returning = update.Returning |> List.map (fun item -> { item with Expression = normalizeExpr item.Expression }) }
            | DeleteStatement delete ->
                DeleteStatement
                    { delete with
                        Where = delete.Where |> Option.map normalizeExpr
                        Returning = delete.Returning |> List.map (fun item -> { item with Expression = normalizeExpr item.Expression }) }
        { document with Statement = statement }

    let normalize bound =
        Transition.normalize normalizeDocument bound

    let private require condition message =
        if not condition then invalidOp message

    let rec private validateExpr expression =
        match expression with
        | Column _
        | Literal _
        | Interval _ -> ()
        | Unary(_, operand) -> validateExpr operand
        | Binary(_, left, right) -> validateExpr left; validateExpr right
        | FunctionCall call -> call.Arguments |> List.iter validateExpr
        | FilteredAggregate(value, predicate) -> validateExpr value; validateExpr predicate
        | Windowed(value, window) ->
            validateExpr value
            window.PartitionBy |> List.iter validateExpr
            window.OrderBy |> List.iter (fun order -> validateExpr order.Expression)
        | Cast(value, _) -> validateExpr value
        | SimpleCase(input, branches, fallback) ->
            require (not branches.IsEmpty) "Simple CASE requires at least one WHEN branch."
            validateExpr input
            branches |> List.iter (fun branch -> validateExpr branch.Match; validateExpr branch.Result)
            fallback |> Option.iter validateExpr
        | SearchedCase(branches, fallback) ->
            require (not branches.IsEmpty) "CASE requires at least one WHEN branch."
            branches |> List.iter (fun branch -> validateExpr branch.Condition; validateExpr branch.Result)
            fallback |> Option.iter validateExpr
        | InList(value, items, _) ->
            require (not items.IsEmpty) "IN list cannot be empty."
            validateExpr value
            items |> List.iter validateExpr
        | Between(value, lower, upper, _) -> validateExpr value; validateExpr lower; validateExpr upper
        | IsNull(value, _) -> validateExpr value
        | ScalarSubquery query -> validateQuery query
        | Exists(query, _) -> validateQuery query

    and private validateSelect select =
        require (not select.Projection.IsEmpty) "SELECT projection cannot be empty."
        select.Projection |> List.iter (fun item -> validateExpr item.Expression)
        select.Where |> Option.iter validateExpr
        select.GroupBy |> List.iter validateExpr
        select.Having |> Option.iter validateExpr
        select.Joins
        |> List.iter (fun join ->
            match join.Kind, join.Predicate with
            | Cross, Some _ -> invalidOp "CROSS JOIN cannot have an ON predicate."
            | Cross, None -> ()
            | _, None -> invalidOp "Non-CROSS JOIN requires an ON predicate."
            | _, Some predicate -> validateExpr predicate)

    and private validateQuery query =
        validateSelect query.Head
        require (query.Limit |> Option.forall (fun value -> value >= 0)) "LIMIT cannot be negative."
        require (query.Offset |> Option.forall (fun value -> value >= 0)) "OFFSET cannot be negative."
        query.SetOperations |> List.iter (fun branch -> validateQuery branch.Query)
        query.OrderBy |> List.iter (fun order -> validateExpr order.Expression)

    let private validateDocument document =
        match document.Statement with
        | QueryStatement query -> validateQuery query
        | InsertStatement insert ->
            require (not insert.Columns.IsEmpty || not insert.Rows.IsEmpty || insert.Source.IsSome) "INSERT has no values or source."
            insert.Rows |> List.iter (List.iter validateExpr)
            insert.Source |> Option.iter validateQuery
        | UpdateStatement update ->
            require (not update.Assignments.IsEmpty) "UPDATE requires at least one assignment."
            update.Assignments |> List.iter (fun assignment -> validateExpr assignment.Value)
            update.Where |> Option.iter validateExpr
        | DeleteStatement delete -> delete.Where |> Option.iter validateExpr
        document

    let validate canonical =
        Transition.validate validateDocument canonical
