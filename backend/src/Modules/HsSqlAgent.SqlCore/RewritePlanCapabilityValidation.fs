namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Capability proofs that operate on an already canonical closed F# document.
/// Join, DML, ordering/paging, and conflict assurance checks live here so RewriteStages
/// can remain a stage orchestrator rather than a second capability registry.
module internal RewritePlanCapabilityValidation =

    let private targetCapabilityMessage =
        RewriteCapabilityProvenance.targetMessage "target capability validation"

    let private iterDistinctOn action (select: Select) =
        match select.DistinctMode with
        | SelectDistinct.DistinctOn expressions -> expressions |> NonEmpty.iter action
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()

    let private requireCapability capabilityMessage = function
        | ProvenCapability -> ()
        | RejectedCapability rejection ->
            raise (SqlCompilationException(capabilityMessage rejection))

    let private proveJoinKind capabilityMessage (proofs: JoinProofs) = function
        | JoinKind.Right -> requireCapability capabilityMessage proofs.RightJoin
        | JoinKind.Full -> requireCapability capabilityMessage proofs.FullJoin
        | JoinKind.Inner | JoinKind.Left | JoinKind.Cross -> ()

    let rec private proveJoinSource capabilityMessage proofs source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) -> proveJoinQuery capabilityMessage proofs query

    and private proveJoinSelect capabilityMessage proofs select =
        select.Ctes |> List.iter (fun cte -> proveJoinQuery capabilityMessage proofs cte.Query)
        select.From |> Option.iter (proveJoinSource capabilityMessage proofs)
        select.Joins
        |> List.iter (fun join ->
            proveJoinKind capabilityMessage proofs join.Kind
            proveJoinSource capabilityMessage proofs join.Source)

    and private proveJoinQuery capabilityMessage proofs query =
        proveJoinSelect capabilityMessage proofs query.Head
        query.SetOperations |> List.iter (fun branch -> proveJoinQuery capabilityMessage proofs branch.Query)

    let proveJoins capabilityMessage proofs document =
        match document.Statement with
        | QueryStatement query -> proveJoinQuery capabilityMessage proofs query
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query -> proveJoinQuery capabilityMessage proofs query
            | Values _ | DefaultValues -> ()
        | UpdateStatement update ->
            update.From |> List.iter (proveJoinSource capabilityMessage proofs)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveJoinSource capabilityMessage proofs)

    let private requireDmlCapability capabilityMessage = function
        | ProvenCapability -> ()
        | RejectedCapability rejection ->
            raise (SqlCompilationException(capabilityMessage rejection))

    let rec private returningNodeName = function
        | Spanned(_, inner) -> returningNodeName inner
        | Column _ -> "ColumnExpr"
        | BoundColumn _ -> "BoundColumnExpr"
        | Wildcard _ -> "WildcardExpr"
        | OrderOrdinal _ -> "OrderByOrdinalExpr"
        | Literal _ -> "LiteralExpr"
        | Interval _ -> "IntervalExpr"
        | DateAdd _ -> "DateAddExpr"
        | DateDiff _ -> "DateDiffExpr"
        | Unary _ -> "UnaryExpr"
        | Binary _ -> "BinaryExpr"
        | Like _ -> "BinaryExpr"
        | RawRegexCall _ | RegexMatch _ -> "RegexExpr"
        | FunctionCall _ -> "FunctionCallExpr"
        | FilteredAggregate _ -> "FilterExpr"
        | Windowed _ -> "WindowedExpr"
        | Cast _ -> "CastExpr"
        | Extract _ -> "ExtractExpr"
        | SimpleCase _ -> "SimpleCaseExpr"
        | SearchedCase _ -> "CaseExpr"
        | InList _ -> "InExpr"
        | InSubquery _ | ScalarSubquery _ -> "SubqueryExpr"
        | Between _ -> "BetweenExpr"
        | IsNull _ -> "IsNullExpr"
        | Exists _ -> "ExistsExpr"

    let private returningExpressionError detail =
        raise (SqlCompilationException(
            "SQL capability 'dml.returning.expression' " + detail + " remains fail-closed."))

    let rec private validateRichReturningExpression expression =
        let validateBoundColumn binding =
            match binding with
            | ColumnBinding.LocalRowSource -> ()
            | ColumnBinding.OuterRowSource ->
                returningExpressionError "does not admit correlated outer-row references"
            | ColumnBinding.ProjectionAlias ->
                returningExpressionError "does not admit projection-alias bindings"

        match expression with
        | Spanned(_, inner) -> validateRichReturningExpression inner
        | BoundColumn(_, binding) ->
            validateBoundColumn binding
        | Column _ ->
            returningExpressionError "requires every column reference to bind to a local DML row source"
        | Literal _ -> ()
        | DateAdd _
        | DateDiff _ ->
            returningExpressionError "does not admit temporal date-math expressions yet"
        | Unary((UnaryOperator.Positive | UnaryOperator.Negate), operand) ->
            validateRichReturningExpression operand
        | Binary((BinaryOperator.Add
                 | BinaryOperator.Subtract
                 | BinaryOperator.Multiply
                 | BinaryOperator.Divide
                 | BinaryOperator.Modulo
                 | BinaryOperator.Concat), left, right) ->
            validateRichReturningExpression left
            validateRichReturningExpression right
        | Cast(value, _) ->
            validateRichReturningExpression value
        | FunctionCall call ->
            let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
            if FunctionName.hasQuotedParts call.Name then
                returningExpressionError (
                    "does not infer portable scalar semantics from quoted native function identity; function '"
                    + name + "'")
            match SqlCanonicalFunctionRegistry.Find(name) |> Option.ofObj with
            | Some contract
                when contract.Kind = SqlCanonicalFunctionKind.Scalar
                     && contract.IsDirectPortable
                     && not call.IsDistinct
                     && contract.AcceptsArgumentCount(call.Arguments.Length) ->
                call.Arguments |> List.iter validateRichReturningExpression
            | _ ->
                returningExpressionError (
                    "accepts only registered direct-portable scalar functions with canonical arity and no DISTINCT; function '"
                    + name + "'")
        | SimpleCase(input, branches, fallback) ->
            validateRichReturningExpression input
            branches |> NonEmpty.iter (fun branch ->
                validateRichReturningExpression branch.Match
                validateRichReturningExpression branch.Result)
            fallback |> Option.iter validateRichReturningExpression
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                validateRichReturningPredicate branch.Condition
                validateRichReturningExpression branch.Result)
            fallback |> Option.iter validateRichReturningExpression
        | Unary(UnaryOperator.Not, _)
        | Binary((BinaryOperator.Equal
                 | BinaryOperator.NotEqual
                 | BinaryOperator.GreaterThan
                 | BinaryOperator.LessThan
                 | BinaryOperator.GreaterThanOrEqual
                 | BinaryOperator.LessThanOrEqual
                 | BinaryOperator.DistinctFrom
                 | BinaryOperator.NotDistinctFrom
                 | BinaryOperator.And
                 | BinaryOperator.Or), _, _)
        | Like _
        | IsNull _
        | Between _
        | InList _ ->
            validateRichReturningPredicate expression
        | _ ->
            returningExpressionError (
                "accepts only the proven target-row scalar/predicate subset; expression node "
                + returningNodeName expression)

    and private validateRichReturningPredicate expression =
        match expression with
        | Spanned(_, inner) -> validateRichReturningPredicate inner
        | Unary(UnaryOperator.Not, operand) ->
            validateRichReturningPredicate operand
        | Binary((BinaryOperator.And | BinaryOperator.Or), left, right) ->
            validateRichReturningPredicate left
            validateRichReturningPredicate right
        | Binary((BinaryOperator.Equal
                 | BinaryOperator.NotEqual
                 | BinaryOperator.GreaterThan
                 | BinaryOperator.LessThan
                 | BinaryOperator.GreaterThanOrEqual
                 | BinaryOperator.LessThanOrEqual
                 | BinaryOperator.DistinctFrom
                 | BinaryOperator.NotDistinctFrom), left, right) ->
            validateRichReturningExpression left
            validateRichReturningExpression right
        | Like(value, pattern, _, _, _) ->
            validateRichReturningExpression value
            validateRichReturningExpression pattern
        | IsNull(value, _) ->
            validateRichReturningExpression value
        | Between(value, lower, upper, _) ->
            validateRichReturningExpression value
            validateRichReturningExpression lower
            validateRichReturningExpression upper
        | InList(value, items, _) ->
            validateRichReturningExpression value
            items |> NonEmpty.iter validateRichReturningExpression
        | _ ->
            returningExpressionError (
                "accepts only comparison, LIKE/ILIKE, IS NULL, BETWEEN, finite IN-list, AND/OR, and NOT predicates; predicate node "
                + returningNodeName expression)

    let private proveSqlServerOutputAssurance (proofs: DmlProofs) operation target =
        match proofs.SqlServerOutput with
        | OutputAssuranceNotRequired -> ()
        | MissingSqlServerOutputAssurance ->
            raise (SqlCompilationException(
                "SQL capability 'dml.returning_output' requires metadata-backed assurance that the SQL Server target has no enabled trigger for the DML operation."))
        | AssuredNoEnabledOutputTriggers(assuredTarget, assuredOperation) ->
            let actualTarget = Identifier.text target
            if assuredOperation <> operation
               || not (StringComparer.OrdinalIgnoreCase.Equals(assuredTarget, actualTarget)) then
                raise (SqlCompilationException(
                    "SQL capability 'dml.returning_output' assurance does not match the compiled SQL Server mutation. Expected target "
                    + actualTarget
                    + " and operation "
                    + string operation
                    + "; assurance declared target "
                    + assuredTarget
                    + " and operation "
                    + string assuredOperation
                    + "."))

    let private proveReturning capabilityMessage (proofs: DmlProofs) operation target (items: ReturningItem list) =
        if not (List.isEmpty items) then
            requireDmlCapability capabilityMessage proofs.Returning
            proveSqlServerOutputAssurance proofs operation target
            let rich =
                items
                |> List.choose (function
                    | ReturningExpression(expression, _) -> Some expression
                    | ReturningColumn _ | ReturningWildcard _ -> None)
            if not rich.IsEmpty then
                requireDmlCapability capabilityMessage proofs.ReturningExpression
                rich |> List.iter validateRichReturningExpression

    let proveDml capabilityMessage (proofs: DmlProofs) document =
        match document.Statement with
        | QueryStatement _ -> ()
        | InsertStatement insert ->
            proveReturning capabilityMessage proofs DmlOperation.Insert insert.Target insert.Returning
        | UpdateStatement update ->
            if update.TargetAlias.IsSome then requireDmlCapability capabilityMessage proofs.TargetAlias
            if not update.From.IsEmpty then requireDmlCapability capabilityMessage proofs.UpdateFrom
            proveReturning capabilityMessage proofs DmlOperation.Update update.Target update.Returning
        | DeleteStatement delete ->
            if delete.TargetAlias.IsSome then requireDmlCapability capabilityMessage proofs.TargetAlias
            if not delete.Using.IsEmpty then requireDmlCapability capabilityMessage proofs.DeleteUsing
            proveReturning capabilityMessage proofs DmlOperation.Delete delete.Target delete.Returning

    let private orderingProviderName = function
        | MySqlRuntime -> "MySQL"
        | SqlServerRuntime _ -> "MsSqlServer"
        | PostgreSqlRuntime -> "Postgres"
        | SQLiteRuntime -> "Sqlite"
        | OracleRuntime -> "Oracle"
        | FirebirdRuntime -> "Firebird"

    let private nullOrderingCapabilityError targetRuntime =
        SqlCompilationException(
            "SQL capability 'ordering.nulls' is not supported by provider "
            + orderingProviderName targetRuntime
            + " for this Core plan.")

    let private targetDefaultNullOrdering (order: OrderBy) =
        match order.NullOrdering with
        | NullOrdering.Default -> true
        | NullOrdering.NullsFirst -> not order.Descending
        | NullOrdering.NullsLast -> order.Descending

    let private requireRewriteableNullOrdering targetRuntime targetOrdering isStatementTail isDistinct isSetTail (order: OrderBy) =
        match targetOrdering, order.NullOrdering with
        | NativeNullOrdering, _
        | RewriteNullOrdering, NullOrdering.Default -> ()
        | RewriteNullOrdering, _ when targetDefaultNullOrdering order -> ()
        | RewriteNullOrdering, _ ->
            if isStatementTail && (isDistinct || isSetTail) then
                raise (nullOrderingCapabilityError targetRuntime)
            match Expr.unspan order.Expression with
            | BoundColumn(_, LocalRowSource)
            | BoundColumn(_, OuterRowSource) -> ()
            | Column _
            | BoundColumn(_, ProjectionAlias)
            | _ -> raise (nullOrderingCapabilityError targetRuntime)

    let rec private proveOrderingExpr targetRuntime targetOrdering expression =
        match expression with
        | Spanned(_, inner) -> proveOrderingExpr targetRuntime targetOrdering inner
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | DateAdd(_, amount, value)
        | DateDiff(_, amount, value) ->
            proveOrderingExpr targetRuntime targetOrdering amount
            proveOrderingExpr targetRuntime targetOrdering value
        | Unary(_, value) -> proveOrderingExpr targetRuntime targetOrdering value
        | Binary(_, left, right) ->
            proveOrderingExpr targetRuntime targetOrdering left
            proveOrderingExpr targetRuntime targetOrdering right
        | Like(value, pattern, _, _, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingExpr targetRuntime targetOrdering pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (proveOrderingExpr targetRuntime targetOrdering)
        | RegexMatch(value, pattern) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingExpr targetRuntime targetOrdering pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (proveOrderingExpr targetRuntime targetOrdering)
            call.AggregateOrderBy
            |> List.iter (fun order ->
                requireRewriteableNullOrdering targetRuntime targetOrdering false false false order
                proveOrderingExpr targetRuntime targetOrdering order.Expression)
        | FilteredAggregate(value, predicate) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingExpr targetRuntime targetOrdering predicate
        | Windowed(value, window) ->
            proveOrderingExpr targetRuntime targetOrdering value
            window.PartitionBy |> List.iter (proveOrderingExpr targetRuntime targetOrdering)
            window.OrderBy
            |> List.iter (fun order ->
                requireRewriteableNullOrdering targetRuntime targetOrdering false false false order
                proveOrderingExpr targetRuntime targetOrdering order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            proveOrderingExpr targetRuntime targetOrdering value
        | SimpleCase(input, branches, fallback) ->
            proveOrderingExpr targetRuntime targetOrdering input
            branches |> NonEmpty.iter (fun branch ->
                proveOrderingExpr targetRuntime targetOrdering branch.Match
                proveOrderingExpr targetRuntime targetOrdering branch.Result)
            fallback |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveOrderingExpr targetRuntime targetOrdering branch.Condition
                proveOrderingExpr targetRuntime targetOrdering branch.Result)
            fallback |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
        | InList(value, items, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
            items |> NonEmpty.iter (proveOrderingExpr targetRuntime targetOrdering)
        | InSubquery(value, query, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingQuery targetRuntime targetOrdering query
        | Between(value, lower, upper, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
            proveOrderingExpr targetRuntime targetOrdering lower
            proveOrderingExpr targetRuntime targetOrdering upper
        | IsNull(value, _) ->
            proveOrderingExpr targetRuntime targetOrdering value
        | ScalarSubquery query | Exists(query, _) ->
            proveOrderingQuery targetRuntime targetOrdering query

    and private proveOrderingSource targetRuntime targetOrdering source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) -> proveOrderingQuery targetRuntime targetOrdering query

    and private proveOrderingSelect targetRuntime targetOrdering select =
        select.Ctes |> List.iter (fun cte -> proveOrderingQuery targetRuntime targetOrdering cte.Query)
        iterDistinctOn (proveOrderingExpr targetRuntime targetOrdering) select
        select.Projection |> List.iter (fun item -> proveOrderingExpr targetRuntime targetOrdering item.Expression)
        select.From |> Option.iter (proveOrderingSource targetRuntime targetOrdering)
        select.Joins |> List.iter (fun join ->
            proveOrderingSource targetRuntime targetOrdering join.Source
            join.Predicate |> Option.iter (proveOrderingExpr targetRuntime targetOrdering))
        select.Where |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
        select.GroupBy |> List.iter (proveOrderingExpr targetRuntime targetOrdering)
        select.Having |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)

    and private proveOrderingQuery targetRuntime targetOrdering query =
        proveOrderingSelect targetRuntime targetOrdering query.Head
        query.SetOperations |> List.iter (fun branch -> proveOrderingQuery targetRuntime targetOrdering branch.Query)
        let isSetTail = not query.SetOperations.IsEmpty
        query.OrderBy
        |> List.iter (fun order ->
            requireRewriteableNullOrdering targetRuntime targetOrdering true query.Head.Distinct isSetTail order
            proveOrderingExpr targetRuntime targetOrdering order.Expression)

    let private stableProjectionNames context (query: Query) =
        query.Head.Projection
        |> List.map (fun item ->
            match item.Alias, Expr.unspan item.Expression with
            | Some alias, _ -> alias
            | None, Column identifier
            | None, BoundColumn(identifier, _) ->
                Identifier.parts identifier |> List.last
            | _ ->
                raise (SqlCompilationException(
                    context
                    + " requires every projected output to have a stable name; use explicit aliases for wildcard or computed expressions.")))

    let private ensureUniqueOutputNames context names =
        let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        names
        |> List.iter (fun (name: IdentifierPart) ->
            if not (seen.Add name.Value) then
                raise (SqlCompilationException(
                    context + " requires unique set-result output names before the legacy ROW_NUMBER wrapper.")))

    let private projectionOrderIndex (projection: SelectItem list) (order: OrderBy) =
        match Expr.unspan order.Expression with
        | OrderOrdinal ordinal ->
            let index = PositiveRowCount.value ordinal - 1
            if index >= 0 && index < projection.Length then Some index else None
        | Column identifier
        | BoundColumn(identifier, _)
            when Identifier.parts identifier |> List.length = 1 ->
            let reference = Identifier.parts identifier |> List.head |> fun part -> part.Value
            let aliasMatches =
                projection
                |> List.indexed
                |> List.choose (fun (index, item) ->
                    item.Alias
                    |> Option.bind (fun alias ->
                        if StringComparer.OrdinalIgnoreCase.Equals(alias.Value, reference) then Some index else None))
            match aliasMatches with
            | [ index ] -> Some index
            | _ :: _ :: _ ->
                raise (SqlCompilationException(
                    "SQL Server OFFSET pagination ORDER BY alias '" + reference + "' is ambiguous."))
            | [] ->
                projection |> List.tryFindIndex (fun item -> Expr.equivalent item.Expression order.Expression)
        | _ ->
            projection |> List.tryFindIndex (fun item -> Expr.equivalent item.Expression order.Expression)

    let private proveSqlServerSelectPaging (query: Query) =
        let context = "SQL Server OFFSET pagination"
        stableProjectionNames context query |> ignore
        let projection = query.Head.Projection
        for order in query.OrderBy do
            match projectionOrderIndex projection order with
            | Some _ -> ()
            | None when query.Head.Distinct ->
                raise (SqlCompilationException(
                    "SQL Server DISTINCT OFFSET pagination requires every ORDER BY expression to resolve to a projected output."))
            | None -> ()

    let private proveSqlServerSetPaging (query: Query) =
        let context = "SQL Server set-operation OFFSET pagination"
        let names = stableProjectionNames context query
        ensureUniqueOutputNames context names
        for order in query.OrderBy do
            match Expr.unspan order.Expression with
            | OrderOrdinal ordinal ->
                let index = PositiveRowCount.value ordinal - 1
                if index < 0 || index >= names.Length then
                    raise (SqlCompilationException(
                        "SQL Server set-operation OFFSET pagination ORDER BY position is outside the projected output width."))
            | Column identifier
            | BoundColumn(identifier, _)
                when Identifier.parts identifier |> List.length = 1 ->
                let reference = Identifier.parts identifier |> List.head |> fun part -> part.Value
                let matches =
                    names
                    |> List.filter (fun name -> StringComparer.OrdinalIgnoreCase.Equals(name.Value, reference))
                if matches.Length <> 1 then
                    raise (SqlCompilationException(
                        "SQL Server set-operation OFFSET pagination ORDER BY reference '"
                        + reference
                        + "' is not a unique combined output name."))
            | _ ->
                raise (SqlCompilationException(
                    "SQL Server set-operation OFFSET pagination supports ORDER BY output names or ordinals only."))

    let rec private proveSqlServerPagingQuery query =
        query.Head.Ctes |> List.iter (fun cte -> proveSqlServerPagingQuery cte.Query)
        query.Head.From |> Option.iter (function DerivedTable(q, _) | LateralDerivedTable(q, _) -> proveSqlServerPagingQuery q | _ -> ())
        query.Head.Joins |> List.iter (fun join ->
            match join.Source with DerivedTable(q, _) | LateralDerivedTable(q, _) -> proveSqlServerPagingQuery q | _ -> ())
        query.SetOperations |> List.iter (fun branch -> proveSqlServerPagingQuery branch.Query)
        match query.Offset with
        | Some offset when NonNegativeRowCount.value offset > 0 ->
            if query.SetOperations.IsEmpty then proveSqlServerSelectPaging query
            else proveSqlServerSetPaging query
        | _ -> ()

    let proveOrderingAndPaging targetRuntime targetOrdering document =
        match document.Statement with
        | QueryStatement query ->
            proveOrderingQuery targetRuntime targetOrdering query
            match targetRuntime with
            | SqlServerRuntime _ -> proveSqlServerPagingQuery query
            | _ -> ()
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query ->
                proveOrderingQuery targetRuntime targetOrdering query
                match targetRuntime with
                | SqlServerRuntime _ -> proveSqlServerPagingQuery query
                | _ -> ()
            | Values _ | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveOrderingExpr targetRuntime targetOrdering item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveOrderingExpr targetRuntime targetOrdering assignment.Value)
            update.From |> List.iter (proveOrderingSource targetRuntime targetOrdering)
            update.Where |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
            update.Returning |> List.iter (fun item -> proveOrderingExpr targetRuntime targetOrdering item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveOrderingSource targetRuntime targetOrdering)
            delete.Where |> Option.iter (proveOrderingExpr targetRuntime targetOrdering)
            delete.Returning |> List.iter (fun item -> proveOrderingExpr targetRuntime targetOrdering item.Expression)

    let private exactColumnSetMatch (left: string list) (right: string list) =
        let leftSet = HashSet<string>(left, StringComparer.OrdinalIgnoreCase)
        let rightSet = HashSet<string>(right, StringComparer.OrdinalIgnoreCase)
        leftSet.Count = List.length left
        && rightSet.Count = List.length right
        && leftSet.SetEquals(rightSet)

    let private assuredColumns label = function
        | AssuredColumns columns -> columns
        | MissingAssurance -> raise (SqlCompilationException(label))

    let private requireExplicitConflictTarget label (conflict: InsertConflict) =
        match conflict.TargetColumns with
        | Some columns -> columns
        | None -> raise (SqlCompilationException(label))

    let private validateConflictTargetColumns (insert: Insert) (conflict: InsertConflict) =
        match conflict.TargetColumns with
        | None ->
            match conflict.Action with
            | DoNothing -> ()
            | UpdateProposedValues _ ->
                raise (SqlCompilationException(
                    "ON CONFLICT DO UPDATE requires an explicit conflict target in the modeled Core contract."))
        | Some targets ->
            let insertColumns =
                HashSet<string>(
                    insert.Columns |> List.map (fun column -> column.Value),
                    StringComparer.OrdinalIgnoreCase)
            let seen = HashSet<string>(StringComparer.OrdinalIgnoreCase)
            for target in targets |> NonEmpty.toList do
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
            conflict
            |> requireExplicitConflictTarget "INSERT ... SELECT conflict DO UPDATE requires an explicit conflict target."
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

        match conflict.TargetColumns, conflict.Action with
        | None, DoNothing ->
            let target = TargetRuntime.provider targetRuntime
            if proofs.SourceProvider <> target then
                raise (SqlCompilationException(
                    "SQL capability 'dml.conflict_do_nothing_any' is native-only because an omitted conflict target depends on the provider's complete native conflict domain. Source provider "
                    + string proofs.SourceProvider + ", target provider " + string target + "."))
            match targetRuntime with
            | PostgreSqlRuntime | SQLiteRuntime -> ()
            | _ ->
                raise (SqlCompilationException(
                    "SQL capability 'dml.conflict_do_nothing_any' is supported only for PostgreSQL and SQLite native targets."))
        | None, UpdateProposedValues _ ->
            raise (SqlCompilationException(
                "ON CONFLICT DO UPDATE requires an explicit conflict target in the modeled Core contract."))
        | Some _, _ -> ()

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

    let private requireConflictCapability = function
        | ProvenCapability -> ()
        | RejectedCapability rejection ->
            raise (SqlCompilationException(targetCapabilityMessage rejection))

    let private validateMySqlConflict (proofs: ConflictProofs) (conflict: InsertConflict) =
        match conflict.Action with
        | DoNothing ->
            raise (SqlCompilationException(
                "MySQL INSERT IGNORE is not a portable ON CONFLICT DO NOTHING equivalent because it can suppress errors beyond the explicit conflict target; MySQL DO NOTHING therefore remains fail-closed."))
        | UpdateProposedValues _ ->
            let matchedColumns =
                match proofs.MySqlUniqueKey with
                | MissingMySqlUniqueKeyAssurance ->
                    raise (SqlCompilationException(
                        "MySQL ON DUPLICATE KEY UPDATE requires metadata-backed statement assurance proving the explicit conflict target matches a complete enforced unique key and is the sole enforced native conflict source."))
                | AssuredMySqlUniqueKey(_, false) ->
                    raise (SqlCompilationException(
                        "MySQL ON DUPLICATE KEY UPDATE can react to any UNIQUE or PRIMARY KEY conflict. Core requires the matched conflict target to be the sole enforced native unique-conflict source, including no additional richer expression, prefix, partial, or otherwise unsupported enforced unique keys."))
                | AssuredMySqlUniqueKey(columns, true) -> columns
            let target =
                conflict
                |> requireExplicitConflictTarget "MySQL conflict lowering requires an explicit canonical conflict target."
                |> NonEmpty.toList
                |> List.map Identifier.text
            if not (exactColumnSetMatch target matchedColumns) then
                raise (SqlCompilationException(
                    "MySQL conflict lowering requires the canonical explicit conflict target to match the complete metadata-resolved unique key exactly."))
            requireConflictCapability proofs.MySqlConditionalTarget

    let private validateDirectConflict (proofs: ConflictProofs) =
        requireConflictCapability proofs.DirectTarget

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
                conflict
                |> requireExplicitConflictTarget "Firebird conflict lowering requires an explicit canonical conflict target."
                |> NonEmpty.toList
                |> List.map Identifier.text
            if not (exactColumnSetMatch target primaryKey) then
                raise (SqlCompilationException(
                    "Firebird UPDATE OR INSERT requires the canonical conflict target to match the complete resolved primary key exactly; general UNIQUE-key and non-unique MATCHING metadata are not represented yet."))
            validateFirebirdFullProposedRowUpdate insert assignments

    let proveConflicts targetRuntime (proofs: ConflictProofs) document =
        match document.Statement with
        | InsertStatement insert ->
            match insert.Conflict with
            | None -> ()
            | Some conflict ->
                validatePortableConflict targetRuntime proofs insert conflict
                match targetRuntime with
                | PostgreSqlRuntime | SQLiteRuntime | SqlServerRuntime _ | OracleRuntime ->
                    validateDirectConflict proofs
                | MySqlRuntime ->
                    validateMySqlConflict proofs conflict
                | FirebirdRuntime ->
                    validateFirebirdConflict proofs insert conflict
        | QueryStatement _ | UpdateStatement _ | DeleteStatement _ -> ()


