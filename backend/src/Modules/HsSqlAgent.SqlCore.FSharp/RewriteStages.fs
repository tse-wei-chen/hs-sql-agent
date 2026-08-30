namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteStages =

    let rec private normalizeExpr expression =
        match expression with
        | Column _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> expression
        | Unary(Positive, operand) -> normalizeExpr operand
        | Unary(op, operand) -> Unary(op, normalizeExpr operand)
        | Binary(op, left, right) -> Binary(op, normalizeExpr left, normalizeExpr right)
        | Like(value, pattern, escape, negated, insensitive) -> Like(normalizeExpr value, normalizeExpr pattern, escape, negated, insensitive)
        | FunctionCall call -> FunctionCall { call with Arguments = call.Arguments |> List.map normalizeExpr }
        | FilteredAggregate(value, predicate) -> FilteredAggregate(normalizeExpr value, normalizeExpr predicate)
        | Windowed(value, window) -> Windowed(normalizeExpr value, normalizeWindow window)
        | Cast(value, targetType) -> Cast(normalizeExpr value, targetType)
        | Extract(field, value) -> Extract(field, normalizeExpr value)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(normalizeExpr input, branches |> NonEmpty.map (fun (branch: SimpleCaseBranch) -> { Match = normalizeExpr branch.Match; Result = normalizeExpr branch.Result }), fallback |> Option.map normalizeExpr)
        | SearchedCase(branches, fallback) ->
            SearchedCase(branches |> NonEmpty.map (fun (branch: SearchedCaseBranch) -> { Condition = normalizeExpr branch.Condition; Result = normalizeExpr branch.Result }), fallback |> Option.map normalizeExpr)
        | InList(value, items, negated) -> InList(normalizeExpr value, items |> NonEmpty.map normalizeExpr, negated)
        | InSubquery(value, query, negated) -> InSubquery(normalizeExpr value, normalizeQuery query, negated)
        | Between(value, lower, upper, negated) -> Between(normalizeExpr value, normalizeExpr lower, normalizeExpr upper, negated)
        | IsNull(value, negated) -> IsNull(normalizeExpr value, negated)
        | ScalarSubquery query -> ScalarSubquery(normalizeQuery query)
        | Exists(query, negated) -> Exists(normalizeQuery query, negated)

    and private normalizeWindow (window: WindowSpec) =
        { window with PartitionBy = window.PartitionBy |> List.map normalizeExpr; OrderBy = window.OrderBy |> List.map normalizeOrderBy }

    and private normalizeOrderBy (orderBy: OrderBy) = { orderBy with Expression = normalizeExpr orderBy.Expression }

    and private normalizeSource (source: TableSource) =
        match source with
        | NamedTable _ | CteTable _ -> source
        | DerivedTable(query, alias) -> DerivedTable(normalizeQuery query, alias)

    and private normalizeJoin (join: Join) =
        match join with
        | CrossJoin source -> CrossJoin(normalizeSource source)
        | OnJoin(kind, source, predicate) -> OnJoin(kind, normalizeSource source, normalizeExpr predicate)

    and private normalizeCte (cte: Cte) =
        let query = normalizeQuery cte.Query
        if cte.ColumnAliases.IsEmpty then { cte with Query = query }
        else
            let projection = query.Head.Projection
            if projection |> List.exists (fun item -> match item.Expression with Wildcard _ -> true | _ -> false) then
                invalidOp ("CTE '" + cte.Name.Value + "' column aliases cannot be lowered safely when the CTE projection contains a wildcard.")
            if projection.Length <> cte.ColumnAliases.Length then
                invalidOp ("CTE '" + cte.Name.Value + "' declares " + string cte.ColumnAliases.Length + " column alias(es) but its statically modeled projection has " + string projection.Length + " column(s).")
            let rewritten =
                (projection, cte.ColumnAliases)
                ||> List.map2 (fun item alias -> { item with Alias = Some alias })
                |> NonEmpty.ofList "CTE projection"
            { cte with ColumnAliases = []; Query = { query with Head = { query.Head with ProjectionItems = rewritten } } }

    and private normalizeSelect (select: Select) =
        { select with
            Ctes = select.Ctes |> List.map normalizeCte
            ProjectionItems = select.ProjectionItems |> NonEmpty.map (fun (item: SelectItem) -> { item with Expression = normalizeExpr item.Expression })
            From = select.From |> Option.map normalizeSource
            Joins = select.Joins |> List.map normalizeJoin
            Where = select.Where |> Option.map normalizeExpr
            GroupBy = select.GroupBy |> List.map normalizeExpr
            Having = select.Having |> Option.map normalizeExpr }

    and private normalizeQuery (query: Query) =
        { query with
            Head = normalizeSelect query.Head
            SetOperations = query.SetOperations |> List.map (fun (branch: SetBranch) -> { branch with Query = normalizeQuery branch.Query })
            OrderBy = query.OrderBy |> List.map normalizeOrderBy }

    let private normalizeReturning items = items |> List.map (fun (item: SelectItem) -> { item with Expression = normalizeExpr item.Expression })

    let private normalizeDocument document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(normalizeQuery query)
            | InsertStatement insert ->
                let input =
                    match insert.Input with
                    | Values rows -> Values(rows |> NonEmpty.map (NonEmpty.map normalizeExpr))
                    | QuerySource query -> QuerySource(normalizeQuery query)
                    | DefaultValues -> DefaultValues
                InsertStatement { insert with Input = input; Returning = normalizeReturning insert.Returning }
            | UpdateStatement update ->
                UpdateStatement
                    { update with
                        AssignmentItems = update.AssignmentItems |> NonEmpty.map (fun assignment -> { assignment with Value = normalizeExpr assignment.Value })
                        From = update.From |> List.map normalizeSource
                        Where = update.Where |> Option.map normalizeExpr
                        Returning = normalizeReturning update.Returning }
            | DeleteStatement delete ->
                DeleteStatement
                    { delete with
                        Using = delete.Using |> List.map normalizeSource
                        Where = delete.Where |> Option.map normalizeExpr
                        Returning = normalizeReturning delete.Returning }
        { document with Statement = statement }

    let normalize bound = Transition.normalize normalizeDocument bound

    let private identifierText = Identifier.text

    let private ensureTableAllowed allowedTables identifier =
        match allowedTables with
        | None | Some [] -> ()
        | Some allowed ->
            let table = identifierText identifier
            if not (allowed |> List.exists (fun value -> StringComparer.OrdinalIgnoreCase.Equals(value, table))) then
                raise (UnauthorizedAccessException("SQL plan is not authorized to access table(s): " + table))

    let private isWildcard = function Wildcard _ -> true | _ -> false

    let private ensureNoDistinctWildcard (call: FunctionCall) =
        if call.IsDistinct && call.Arguments |> List.exists isWildcard then
            invalidOp "COUNT(DISTINCT *) is not a valid Core aggregate shape."

    let rec private validateExpr allowedTables expression =
        match expression with
        | Column _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) -> validateExpr allowedTables operand
        | Binary(_, left, right) -> validateExpr allowedTables left; validateExpr allowedTables right
        | Like(value, pattern, _, _, _) ->
            validateExpr allowedTables value
            validateExpr allowedTables pattern
        | FunctionCall call ->
            ensureNoDistinctWildcard call
            call.Arguments |> List.iter (validateExpr allowedTables)
        | FilteredAggregate(value, predicate) -> validateExpr allowedTables value; validateExpr allowedTables predicate
        | Windowed(value, window) ->
            validateExpr allowedTables value
            window.PartitionBy |> List.iter (validateExpr allowedTables)
            window.OrderBy |> List.iter (fun order -> validateExpr allowedTables order.Expression)
        | Cast(value, _) -> validateExpr allowedTables value
        | Extract(_, value) -> validateExpr allowedTables value
        | SimpleCase(input, branches, fallback) ->
            validateExpr allowedTables input
            branches |> NonEmpty.iter (fun branch -> validateExpr allowedTables branch.Match; validateExpr allowedTables branch.Result)
            fallback |> Option.iter (validateExpr allowedTables)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch -> validateExpr allowedTables branch.Condition; validateExpr allowedTables branch.Result)
            fallback |> Option.iter (validateExpr allowedTables)
        | InList(value, items, _) -> validateExpr allowedTables value; items |> NonEmpty.iter (validateExpr allowedTables)
        | InSubquery(value, query, _) -> validateExpr allowedTables value; validateQuery allowedTables query
        | Between(value, lower, upper, _) -> validateExpr allowedTables value; validateExpr allowedTables lower; validateExpr allowedTables upper
        | IsNull(value, _) -> validateExpr allowedTables value
        | ScalarSubquery query -> validateQuery allowedTables query
        | Exists(query, _) -> validateQuery allowedTables query

    and private validateSource allowedTables source =
        match source with
        | NamedTable(identifier, _) -> ensureTableAllowed allowedTables identifier
        | CteTable _ -> ()
        | DerivedTable(query, _) -> validateQuery allowedTables query

    and private validateSelect allowedTables select =
        for cte in select.Ctes do validateQuery allowedTables cte.Query
        if select.From.IsNone && select.Joins.IsEmpty && select.Projection |> List.exists (fun item -> isWildcard item.Expression) then
            invalidOp "Column reference '*' requires a FROM source in the portable Core query model."
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
        let duplicateAliases = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let seenAliases = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for item in query.Head.Projection do
            match item.Alias with
            | Some alias when not (seenAliases.Add alias.Value) -> duplicateAliases.Add alias.Value |> ignore
            | _ -> ()
        if duplicateAliases.Count > 0 then
            for order in query.OrderBy do
                match order.Expression with
                | Column identifier when Identifier.parts identifier |> List.length = 1 ->
                    let name = identifierText identifier
                    if duplicateAliases.Contains name then
                        if query.Head.From.IsNone && query.Head.Joins.IsEmpty then
                            invalidOp ("ORDER BY projection alias '" + name + "' is ambiguous in a no-FROM query.")
                        else
                            invalidOp ("ORDER BY alias '" + name + "' is ambiguous.")
                | _ -> ()
        query.SetOperations |> List.iter (fun branch -> validateQuery allowedTables branch.Query)
        query.OrderBy |> List.iter (fun order -> validateExpr allowedTables order.Expression)

    let rec private validateInsertValueScope expression =
        match expression with
        | Literal _ | Interval _ -> ()
        | ScalarSubquery _ | Exists _ -> ()
        | Column identifier ->
            invalidOp ("INSERT VALUES scalar expression cannot reference column '" + identifierText identifier + "' outside a scalar subquery; use INSERT ... SELECT when the inserted value depends on a source row.")
        | Wildcard _ | OrderOrdinal _ -> invalidOp "INSERT VALUES scalar expression cannot contain a wildcard or ORDER BY ordinal."
        | Unary(_, operand) -> validateInsertValueScope operand
        | Binary(_, left, right) -> validateInsertValueScope left; validateInsertValueScope right
        | Like(value, pattern, _, _, _) -> validateInsertValueScope value; validateInsertValueScope pattern
        | FunctionCall call -> call.Arguments |> List.iter validateInsertValueScope
        | FilteredAggregate(value, predicate) -> validateInsertValueScope value; validateInsertValueScope predicate
        | Windowed(value, window) ->
            validateInsertValueScope value
            window.PartitionBy |> List.iter validateInsertValueScope
            window.OrderBy |> List.iter (fun order -> validateInsertValueScope order.Expression)
        | Cast(value, _) | Extract(_, value) -> validateInsertValueScope value
        | SimpleCase(input, branches, fallback) ->
            validateInsertValueScope input
            branches |> NonEmpty.iter (fun branch -> validateInsertValueScope branch.Match; validateInsertValueScope branch.Result)
            fallback |> Option.iter validateInsertValueScope
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch -> validateInsertValueScope branch.Condition; validateInsertValueScope branch.Result)
            fallback |> Option.iter validateInsertValueScope
        | InList(value, items, _) -> validateInsertValueScope value; items |> NonEmpty.iter validateInsertValueScope
        | InSubquery(value, _, _) -> validateInsertValueScope value
        | Between(value, lower, upper, _) -> validateInsertValueScope value; validateInsertValueScope lower; validateInsertValueScope upper
        | IsNull(value, _) -> validateInsertValueScope value

    let private projectionWidth query =
        if query.Head.Projection |> List.exists (fun item -> isWildcard item.Expression) then None
        else Some query.Head.Projection.Length

    let private validateInsertShape insert =
        match insert.Input with
        | DefaultValues -> ()
        | Values rows ->
            if insert.Columns.IsEmpty then invalidOp "INSERT VALUES requires explicit target columns."
            rows
            |> NonEmpty.iter (fun row ->
                if NonEmpty.length row <> insert.Columns.Length then invalidOp "INSERT VALUES row width does not match target column count."
                row |> NonEmpty.iter validateInsertValueScope)
        | QuerySource query ->
            if insert.Columns.IsEmpty then invalidOp "INSERT ... SELECT requires explicit target columns."
            match projectionWidth query with
            | None -> invalidOp "INSERT ... SELECT requires a statically known source projection width; wildcard projections are rejected at the Core validation boundary."
            | Some width when width <> insert.Columns.Length ->
                invalidOp ("INSERT ... SELECT projection width " + string width + " does not match target column count " + string insert.Columns.Length + ".")
            | _ -> ()

    let private validateReturning allowedTables items = items |> List.iter (fun item -> validateExpr allowedTables item.Expression)

    let private validateDocument allowedTables document =
        match document.Statement with
        | QueryStatement query -> validateQuery allowedTables query
        | InsertStatement insert ->
            ensureTableAllowed allowedTables insert.Target
            validateInsertShape insert
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (validateExpr allowedTables))
            | QuerySource query -> validateQuery allowedTables query
            | DefaultValues -> ()
            validateReturning allowedTables insert.Returning
        | UpdateStatement update ->
            ensureTableAllowed allowedTables update.Target
            update.From |> List.iter (validateSource allowedTables)
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> validateExpr allowedTables assignment.Value)
            update.Where |> Option.iter (validateExpr allowedTables)
            validateReturning allowedTables update.Returning
        | DeleteStatement delete ->
            ensureTableAllowed allowedTables delete.Target
            delete.Using |> List.iter (validateSource allowedTables)
            delete.Where |> Option.iter (validateExpr allowedTables)
            validateReturning allowedTables delete.Returning
        document

    let private proveSqlServerConcat targetRuntime =
        match targetRuntime with
        | SqlServerRuntime(Proven _) -> ()
        | SqlServerRuntime(Unproven message) -> invalidOp message
        | _ -> ()

    let rec private proveTargetExpr targetRuntime expression =
        match expression with
        | Column _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) -> proveTargetExpr targetRuntime operand
        | Binary(BinaryOperator.Concat, left, right) ->
            proveSqlServerConcat targetRuntime
            proveTargetExpr targetRuntime left
            proveTargetExpr targetRuntime right
        | Binary(_, left, right) ->
            proveTargetExpr targetRuntime left
            proveTargetExpr targetRuntime right
        | Like(value, pattern, _, _, _) ->
            proveTargetExpr targetRuntime value
            proveTargetExpr targetRuntime pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (proveTargetExpr targetRuntime)
        | FilteredAggregate(value, predicate) ->
            proveTargetExpr targetRuntime value
            proveTargetExpr targetRuntime predicate
        | Windowed(value, window) ->
            proveTargetExpr targetRuntime value
            window.PartitionBy |> List.iter (proveTargetExpr targetRuntime)
            window.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            proveTargetExpr targetRuntime value
        | SimpleCase(input, branches, fallback) ->
            proveTargetExpr targetRuntime input
            branches |> NonEmpty.iter (fun branch ->
                proveTargetExpr targetRuntime branch.Match
                proveTargetExpr targetRuntime branch.Result)
            fallback |> Option.iter (proveTargetExpr targetRuntime)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveTargetExpr targetRuntime branch.Condition
                proveTargetExpr targetRuntime branch.Result)
            fallback |> Option.iter (proveTargetExpr targetRuntime)
        | InList(value, items, _) ->
            proveTargetExpr targetRuntime value
            items |> NonEmpty.iter (proveTargetExpr targetRuntime)
        | InSubquery(value, query, _) ->
            proveTargetExpr targetRuntime value
            proveTargetQuery targetRuntime query
        | Between(value, lower, upper, _) ->
            proveTargetExpr targetRuntime value
            proveTargetExpr targetRuntime lower
            proveTargetExpr targetRuntime upper
        | IsNull(value, _) ->
            proveTargetExpr targetRuntime value
        | ScalarSubquery query ->
            proveTargetQuery targetRuntime query
        | Exists(query, _) ->
            proveTargetQuery targetRuntime query

    and private proveTargetSource targetRuntime source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _) -> proveTargetQuery targetRuntime query

    and private proveTargetSelect targetRuntime select =
        select.Ctes |> List.iter (fun cte -> proveTargetQuery targetRuntime cte.Query)
        select.ProjectionItems |> NonEmpty.iter (fun item -> proveTargetExpr targetRuntime item.Expression)
        select.From |> Option.iter (proveTargetSource targetRuntime)
        select.Joins
        |> List.iter (function
            | CrossJoin source -> proveTargetSource targetRuntime source
            | OnJoin(_, source, predicate) ->
                proveTargetSource targetRuntime source
                proveTargetExpr targetRuntime predicate)
        select.Where |> Option.iter (proveTargetExpr targetRuntime)
        select.GroupBy |> List.iter (proveTargetExpr targetRuntime)
        select.Having |> Option.iter (proveTargetExpr targetRuntime)

    and private proveTargetQuery targetRuntime query =
        proveTargetSelect targetRuntime query.Head
        query.SetOperations |> List.iter (fun branch -> proveTargetQuery targetRuntime branch.Query)
        query.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime order.Expression)

    let private proveTargetDocument targetRuntime document =
        match document.Statement with
        | QueryStatement query -> proveTargetQuery targetRuntime query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (proveTargetExpr targetRuntime))
            | QuerySource query -> proveTargetQuery targetRuntime query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveTargetExpr targetRuntime assignment.Value)
            update.From |> List.iter (proveTargetSource targetRuntime)
            update.Where |> Option.iter (proveTargetExpr targetRuntime)
            update.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveTargetSource targetRuntime)
            delete.Where |> Option.iter (proveTargetExpr targetRuntime)
            delete.Returning |> List.iter (fun item -> proveTargetExpr targetRuntime item.Expression)
        document

    let validate allowedTables targetRuntime canonical =
        Transition.validate targetRuntime (fun document ->
            let validated = validateDocument allowedTables document
            proveTargetDocument targetRuntime validated) canonical
