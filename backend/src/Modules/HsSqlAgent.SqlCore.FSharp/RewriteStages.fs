namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
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
        | Column _ | Wildcard _ | OrderOrdinal _ | Literal _ -> ()
        | Interval _ -> requireExpressionCapability expressionProofs.IntervalLiteral
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

    let private requireExpressionCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> invalidOp message

    let rec private proveTargetExpr targetRuntime (expressionProofs: ExpressionProofs) expression =
        match expression with
        | Column _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) -> proveTargetExpr targetRuntime expressionProofs operand
        | Binary(BinaryOperator.Concat, left, right) ->
            proveSqlServerConcat targetRuntime
            proveTargetExpr targetRuntime expressionProofs left
            proveTargetExpr targetRuntime expressionProofs right
        | Binary(_, left, right) ->
            proveTargetExpr targetRuntime expressionProofs left
            proveTargetExpr targetRuntime expressionProofs right
        | Like(value, pattern, _, _, caseInsensitive) ->
            if caseInsensitive then requireExpressionCapability expressionProofs.ILike
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (proveTargetExpr targetRuntime expressionProofs)
        | FilteredAggregate(value, predicate) ->
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs predicate
        | Windowed(value, window) ->
            proveTargetExpr targetRuntime expressionProofs value
            window.PartitionBy |> List.iter (proveTargetExpr targetRuntime expressionProofs)
            window.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)
        | Cast(value, _) | Extract(_, value) ->
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

    and private proveTargetSelect targetRuntime expressionProofs select =
        select.Ctes |> List.iter (fun cte -> proveTargetQuery targetRuntime expressionProofs cte.Query)
        select.ProjectionItems |> NonEmpty.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        select.From |> Option.iter (proveTargetSource targetRuntime expressionProofs)
        select.Joins
        |> List.iter (function
            | CrossJoin source -> proveTargetSource targetRuntime expressionProofs source
            | OnJoin(_, source, predicate) ->
                proveTargetSource targetRuntime expressionProofs source
                proveTargetExpr targetRuntime expressionProofs predicate)
        select.Where |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
        select.GroupBy |> List.iter (proveTargetExpr targetRuntime expressionProofs)
        select.Having |> Option.iter (proveTargetExpr targetRuntime expressionProofs)

    and private proveTargetQuery targetRuntime expressionProofs query =
        proveTargetSelect targetRuntime expressionProofs query.Head
        query.SetOperations |> List.iter (fun branch -> proveTargetQuery targetRuntime expressionProofs branch.Query)
        query.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)

    let private proveTargetDocument targetRuntime expressionProofs document =
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
        document

    let private requireCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> invalidOp message

    let private proveJoinKind (proofs: JoinProofs) = function
        | JoinKind.Right -> requireCapability proofs.RightJoin
        | JoinKind.Full -> requireCapability proofs.FullJoin
        | JoinKind.Inner | JoinKind.Left | JoinKind.Cross -> ()

    let rec private proveTargetJoinSource proofs source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _) -> proveTargetJoinQuery proofs query

    and private proveTargetJoinSelect proofs select =
        select.Ctes |> List.iter (fun cte -> proveTargetJoinQuery proofs cte.Query)
        select.From |> Option.iter (proveTargetJoinSource proofs)
        select.Joins
        |> List.iter (fun join ->
            proveJoinKind proofs join.Kind
            proveTargetJoinSource proofs join.Source)

    and private proveTargetJoinQuery proofs query =
        proveTargetJoinSelect proofs query.Head
        query.SetOperations |> List.iter (fun branch -> proveTargetJoinQuery proofs branch.Query)

    let private proveTargetJoins proofs document =
        match document.Statement with
        | QueryStatement query -> proveTargetJoinQuery proofs query
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query -> proveTargetJoinQuery proofs query
            | Values _ | DefaultValues -> ()
        | UpdateStatement update ->
            update.From |> List.iter (proveTargetJoinSource proofs)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveTargetJoinSource proofs)

    let private isRichReturningItem (item: SelectItem) =
        match item.Expression with
        | Column identifier when Identifier.parts identifier |> List.length = 1 -> false
        | Wildcard None -> false
        | _ -> true

    let private proveReturning (proofs: DmlProofs) items =
        if not (List.isEmpty items) then
            requireCapability proofs.Returning
            if items |> List.exists isRichReturningItem then
                requireCapability proofs.ReturningExpression

    let private proveTargetDml (proofs: DmlProofs) document =
        match document.Statement with
        | QueryStatement _ -> ()
        | InsertStatement insert ->
            proveReturning proofs insert.Returning
        | UpdateStatement update ->
            if not update.From.IsEmpty then requireCapability proofs.UpdateFrom
            proveReturning proofs update.Returning
        | DeleteStatement delete ->
            if not delete.Using.IsEmpty then requireCapability proofs.DeleteUsing
            proveReturning proofs delete.Returning

    let private exactColumnSetMatch (left: string list) (right: string list) =
        let leftSet = HashSet<string>(left, StringComparer.OrdinalIgnoreCase)
        let rightSet = HashSet<string>(right, StringComparer.OrdinalIgnoreCase)
        leftSet.Count = List.length left
        && rightSet.Count = List.length right
        && leftSet.SetEquals(rightSet)

    let private assuredColumns label = function
        | AssuredColumns columns -> columns
        | MissingAssurance -> raise (SqlCompilationException(label))

    let private validateConflictTargetColumns (insert: Insert) (conflict: InsertConflict) =
        let insertColumns =
            HashSet<string>(
                insert.Columns |> List.map (fun column -> column.Value),
                StringComparer.OrdinalIgnoreCase)
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for target in conflict.TargetColumns |> NonEmpty.toList do
            let name = Identifier.text target
            if not (seen.Add name) then
                raise (SqlCompilationException(
                    "INSERT conflict target column '" + name + "' is declared more than once."))
            if not (insertColumns.Contains name) then
                raise (SqlCompilationException(
                    "INSERT conflict target column '" + name + "' must be explicitly present in the INSERT column list so Core does not depend on provider-default conflict-key values."))

    let private validateInsertSelectConflictAssurance (conflict: InsertConflict) (proofs: ConflictProofs) =
        let proven =
            assuredColumns
                "PostgreSQL INSERT ... SELECT ON CONFLICT DO UPDATE remains fail-closed without explicit source-row uniqueness/cardinality assurance for the complete conflict target."
                proofs.SourceRowsUniqueByInsertColumns
        let target =
            conflict.TargetColumns
            |> NonEmpty.toList
            |> List.map Identifier.text
        if not (exactColumnSetMatch target proven) then
            raise (SqlCompilationException(
                "INSERT ... SELECT conflict DO UPDATE requires source-row uniqueness assurance to match the complete explicit conflict target exactly."))

    let private validateConflictAssignments (insert: Insert) (assignments: NonEmpty<ConflictAssignment>) =
        let insertColumns =
            HashSet<string>(
                insert.Columns |> List.map (fun column -> column.Value),
                StringComparer.OrdinalIgnoreCase)
        let assigned = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        assignments
        |> NonEmpty.iter (fun (assignment: ConflictAssignment) ->
            let target = Identifier.text assignment.Target
            let proposed = Identifier.text assignment.Proposed
            if not (assigned.Add target) then
                raise (SqlCompilationException(
                    "INSERT conflict DO UPDATE assigns column '" + target + "' more than once."))
            if not (insertColumns.Contains proposed) then
                raise (SqlCompilationException(
                    "Proposed-row column '" + proposed + "' must be explicitly present in the INSERT column list; portable upsert does not depend on target-provider default values.")))

    let private validatePortableConflict targetRuntime (proofs: ConflictProofs) (insert: Insert) (conflict: InsertConflict) =
        match insert.Input with
        | DefaultValues ->
            raise (SqlCompilationException("Unsupported INSERT source for conflict handling."))
        | QuerySource _ ->
            match targetRuntime with
            | PostgreSqlRuntime -> ()
            | _ ->
                raise (SqlCompilationException(
                    "INSERT ... SELECT conflict handling is currently proven only for PostgreSQL targets; other targets remain fail-closed."))
        | Values _ -> ()

        validateConflictTargetColumns insert conflict

        match conflict.Action with
        | DoNothing -> ()
        | UpdateProposedValues assignments ->
            match insert.Input with
            | QuerySource _ -> validateInsertSelectConflictAssurance conflict proofs
            | Values rows when NonEmpty.length rows <> 1 ->
                raise (SqlCompilationException(
                    "Portable INSERT conflict DO UPDATE currently requires exactly one proposed VALUES row. Multi-row proposed values require explicit source-row uniqueness/cardinality assurance."))
            | Values _ -> ()
            | DefaultValues -> ()
            validateConflictAssignments insert assignments

    let private validateFirebirdFullProposedRowUpdate (insert: Insert) (assignments: NonEmpty<ConflictAssignment>) =
        let assignmentList = NonEmpty.toList assignments
        if assignmentList.Length <> insert.Columns.Length then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT updates every supplied INSERT column on a match. Core therefore requires one same-column proposed-row assignment for every INSERT column so partial-update semantics cannot drift."))

        let insertColumns =
            HashSet<string>(
                insert.Columns |> List.map (fun column -> column.Value),
                StringComparer.OrdinalIgnoreCase)
        let assigned = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for (assignment: ConflictAssignment) in assignmentList do
            let target = Identifier.text assignment.Target
            let proposed = Identifier.text assignment.Proposed
            if not (StringComparer.OrdinalIgnoreCase.Equals(target, proposed)) then
                raise (SqlCompilationException(
                    "Firebird UPDATE OR INSERT can mirror the portable conflict contract only when each assignment is target = proposed-row target for the same column."))
            if not (assigned.Add target) || not (insertColumns.Contains target) then
                raise (SqlCompilationException(
                    "Firebird UPDATE OR INSERT assignment column '" + target + "' must occur exactly once in the INSERT column list."))

        if not (assigned.SetEquals insertColumns) then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT requires conflict assignments to cover the complete INSERT column set."))

    let private validateFirebirdConflict (proofs: ConflictProofs) (insert: Insert) (conflict: InsertConflict) =
        match conflict.Action with
        | DoNothing ->
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT has update-or-insert semantics and cannot represent portable ON CONFLICT DO NOTHING; a separate MERGE no-match contract is required."))
        | UpdateProposedValues assignments ->
            let primaryKey =
                assuredColumns
                    "Firebird UPDATE OR INSERT requires metadata-backed conflict-target assurance proving MATCHING equals the resolved primary key; absent assurance remains fail-closed because non-unique MATCHING can update multiple rows."
                    proofs.FirebirdPrimaryKey
            let target =
                conflict.TargetColumns
                |> NonEmpty.toList
                |> List.map Identifier.text
            if not (exactColumnSetMatch target primaryKey) then
                raise (SqlCompilationException(
                    "Firebird UPDATE OR INSERT requires the canonical conflict target to match the complete resolved primary key exactly; general UNIQUE-key and non-unique MATCHING metadata are not represented yet."))
            validateFirebirdFullProposedRowUpdate insert assignments

    let private proveConflicts targetRuntime (proofs: ConflictProofs) document =
        match document.Statement with
        | InsertStatement insert ->
            match insert.Conflict with
            | None -> ()
            | Some conflict ->
                validatePortableConflict targetRuntime proofs insert conflict
                match targetRuntime with
                | FirebirdRuntime -> validateFirebirdConflict proofs insert conflict
                | _ -> ()
        | QueryStatement _ | UpdateStatement _ | DeleteStatement _ -> ()

    let validate allowedTables targetRuntime targetExpressions targetJoins targetDml conflictProofs canonical =
        Transition.validate targetRuntime (fun document ->
            let validated = validateDocument allowedTables document
            proveTargetDocument targetRuntime targetExpressions validated |> ignore
            proveTargetJoins targetJoins validated
            proveTargetDml targetDml validated
            proveConflicts targetRuntime conflictProofs validated
            validated) canonical
