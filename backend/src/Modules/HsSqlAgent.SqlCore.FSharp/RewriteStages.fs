namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteStages =

    let rec private normalizeExpr expression =
        match expression with
        | Column _ | Literal _ | Interval _ -> expression
        | Unary(Positive, operand) -> normalizeExpr operand
        | Unary(op, operand) -> Unary(op, normalizeExpr operand)
        | Binary(op, left, right) -> Binary(op, normalizeExpr left, normalizeExpr right)
        | FunctionCall call -> FunctionCall { call with Arguments = call.Arguments |> List.map normalizeExpr }
        | FilteredAggregate(value, predicate) -> FilteredAggregate(normalizeExpr value, normalizeExpr predicate)
        | Windowed(value, window) -> Windowed(normalizeExpr value, normalizeWindow window)
        | Cast(value, targetType) -> Cast(normalizeExpr value, targetType)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(normalizeExpr input, branches |> NonEmpty.map (fun (branch: SimpleCaseBranch) -> { Match = normalizeExpr branch.Match; Result = normalizeExpr branch.Result }), fallback |> Option.map normalizeExpr)
        | SearchedCase(branches, fallback) ->
            SearchedCase(branches |> NonEmpty.map (fun (branch: SearchedCaseBranch) -> { Condition = normalizeExpr branch.Condition; Result = normalizeExpr branch.Result }), fallback |> Option.map normalizeExpr)
        | InList(value, items, negated) -> InList(normalizeExpr value, items |> NonEmpty.map normalizeExpr, negated)
        | Between(value, lower, upper, negated) -> Between(normalizeExpr value, normalizeExpr lower, normalizeExpr upper, negated)
        | IsNull(value, negated) -> IsNull(normalizeExpr value, negated)
        | ScalarSubquery query -> ScalarSubquery(normalizeQuery query)
        | Exists(query, negated) -> Exists(normalizeQuery query, negated)

    and private normalizeWindow window =
        { window with PartitionBy = window.PartitionBy |> List.map normalizeExpr; OrderBy = window.OrderBy |> List.map normalizeOrderBy }

    and private normalizeOrderBy orderBy = { orderBy with Expression = normalizeExpr orderBy.Expression }
    and private normalizeSource source = match source with NamedTable _ -> source | DerivedTable(query, alias) -> DerivedTable(normalizeQuery query, alias)
    and private normalizeJoin join = match join with CrossJoin source -> CrossJoin(normalizeSource source) | OnJoin(kind, source, predicate) -> OnJoin(kind, normalizeSource source, normalizeExpr predicate)

    and private normalizeSelect select =
        { select with
            ProjectionItems = select.ProjectionItems |> NonEmpty.map (fun (item: SelectItem) -> { item with Expression = normalizeExpr item.Expression })
            From = select.From |> Option.map normalizeSource
            Joins = select.Joins |> List.map normalizeJoin
            Where = select.Where |> Option.map normalizeExpr
            GroupBy = select.GroupBy |> List.map normalizeExpr
            Having = select.Having |> Option.map normalizeExpr }

    and private normalizeQuery query =
        { query with Head = normalizeSelect query.Head; SetOperations = query.SetOperations |> List.map (fun (branch: SetBranch) -> { branch with Query = normalizeQuery branch.Query }); OrderBy = query.OrderBy |> List.map normalizeOrderBy }

    let private normalizeDocument document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(normalizeQuery query)
            | InsertStatement insert ->
                let input = match insert.Input with Values rows -> Values(rows |> NonEmpty.map (NonEmpty.map normalizeExpr)) | QuerySource query -> QuerySource(normalizeQuery query) | DefaultValues -> DefaultValues
                InsertStatement { insert with Input = input; Returning = insert.Returning |> List.map (fun (item: SelectItem) -> { item with Expression = normalizeExpr item.Expression }) }
            | UpdateStatement update ->
                UpdateStatement { update with AssignmentItems = update.AssignmentItems |> NonEmpty.map (fun (assignment: Assignment) -> { assignment with Value = normalizeExpr assignment.Value }); Where = update.Where |> Option.map normalizeExpr; Returning = update.Returning |> List.map (fun (item: SelectItem) -> { item with Expression = normalizeExpr item.Expression }) }
            | DeleteStatement delete ->
                DeleteStatement { delete with Where = delete.Where |> Option.map normalizeExpr; Returning = delete.Returning |> List.map (fun (item: SelectItem) -> { item with Expression = normalizeExpr item.Expression }) }
        { document with Statement = statement }

    let normalize bound = Transition.normalize normalizeDocument bound

    let private identifierText (identifier: Identifier) =
        identifier |> Identifier.parts |> List.map (fun part -> part.Value) |> String.concat "."

    let private ensureTableAllowed (allowedTables: string list option) (identifier: Identifier) =
        match allowedTables with
        | None | Some [] -> ()
        | Some allowed ->
            let table = identifierText identifier
            let authorized = allowed |> List.exists (fun value -> StringComparer.OrdinalIgnoreCase.Equals(value, table))
            if not authorized then
                raise (UnauthorizedAccessException("SQL plan is not authorized to access table(s): " + table))

    let rec private validateExpr allowedTables expression =
        match expression with
        | Column _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) -> validateExpr allowedTables operand
        | Binary(_, left, right) -> validateExpr allowedTables left; validateExpr allowedTables right
        | FunctionCall call -> call.Arguments |> List.iter (validateExpr allowedTables)
        | FilteredAggregate(value, predicate) -> validateExpr allowedTables value; validateExpr allowedTables predicate
        | Windowed(value, window) -> validateExpr allowedTables value; window.PartitionBy |> List.iter (validateExpr allowedTables); window.OrderBy |> List.iter (fun order -> validateExpr allowedTables order.Expression)
        | Cast(value, _) -> validateExpr allowedTables value
        | SimpleCase(input, branches, fallback) -> validateExpr allowedTables input; branches |> NonEmpty.iter (fun branch -> validateExpr allowedTables branch.Match; validateExpr allowedTables branch.Result); fallback |> Option.iter (validateExpr allowedTables)
        | SearchedCase(branches, fallback) -> branches |> NonEmpty.iter (fun branch -> validateExpr allowedTables branch.Condition; validateExpr allowedTables branch.Result); fallback |> Option.iter (validateExpr allowedTables)
        | InList(value, items, _) -> validateExpr allowedTables value; items |> NonEmpty.iter (validateExpr allowedTables)
        | Between(value, lower, upper, _) -> validateExpr allowedTables value; validateExpr allowedTables lower; validateExpr allowedTables upper
        | IsNull(value, _) -> validateExpr allowedTables value
        | ScalarSubquery query -> validateQuery allowedTables query
        | Exists(query, _) -> validateQuery allowedTables query

    and private validateSource allowedTables source =
        match source with
        | NamedTable(identifier, _) -> ensureTableAllowed allowedTables identifier
        | DerivedTable(query, _) -> validateQuery allowedTables query

    and private validateSelect allowedTables select =
        select.From |> Option.iter (validateSource allowedTables)
        select.ProjectionItems |> NonEmpty.iter (fun item -> validateExpr allowedTables item.Expression)
        select.Where |> Option.iter (validateExpr allowedTables)
        select.GroupBy |> List.iter (validateExpr allowedTables)
        select.Having |> Option.iter (validateExpr allowedTables)
        select.Joins
        |> List.iter (function
            | CrossJoin source -> validateSource allowedTables source
            | OnJoin(_, source, predicate) -> validateSource allowedTables source; validateExpr allowedTables predicate)

    and private validateQuery allowedTables query =
        validateSelect allowedTables query.Head
        query.SetOperations |> List.iter (fun branch -> validateQuery allowedTables branch.Query)
        query.OrderBy |> List.iter (fun order -> validateExpr allowedTables order.Expression)

    let private validateReturning allowedTables items =
        items |> List.iter (fun item -> validateExpr allowedTables item.Expression)

    let private validateDocument allowedTables document =
        match document.Statement with
        | QueryStatement query -> validateQuery allowedTables query
        | InsertStatement insert ->
            ensureTableAllowed allowedTables insert.Target
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (validateExpr allowedTables))
            | QuerySource query -> validateQuery allowedTables query
            | DefaultValues -> ()
            validateReturning allowedTables insert.Returning
        | UpdateStatement update ->
            ensureTableAllowed allowedTables update.Target
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> validateExpr allowedTables assignment.Value)
            update.Where |> Option.iter (validateExpr allowedTables)
            validateReturning allowedTables update.Returning
        | DeleteStatement delete ->
            ensureTableAllowed allowedTables delete.Target
            delete.Where |> Option.iter (validateExpr allowedTables)
            validateReturning allowedTables delete.Returning
        document

    let validate allowedTables canonical = Transition.validate (validateDocument allowedTables) canonical
