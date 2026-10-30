namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Source-dialect semantic validation before normalization.
/// This module owns raw-source capability checks only; it does not lower or render SQL.
module internal RewriteSourceValidation =

    let private compilationError message =
        raise (SqlCompilationException(message))

    let private iterDistinctOn action (select: Select) =
        match select.DistinctMode with
        | SelectDistinct.DistinctOn expressions -> expressions |> NonEmpty.iter action
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()

    let private sourceCapabilityMessage =
        RewriteCapabilityProvenance.sourceMessage "source semantic validation"

    let private requireSourceRegexCapability = function
        | ProvenCapability -> ()
        | RejectedCapability rejection ->
            raise (SqlCompilationException(sourceCapabilityMessage rejection))

    // Source capability proof is not an AST rewrite. A shared explicit
    // visitation stack keeps the SQL source's check order without using
    // CLR call frames proportional to expression/query depth.
    [<Struct>]
    type private WalkFrame =
        | WalkExpr of expression: Expr
        | WalkQuery of query: Query
        | WalkSelect of select: Select
        | WalkSource of source: TableSource
        | WalkOrder of ordering: OrderBy
        | WalkAfterCtes of select: Select
        | WalkAfterRegexArguments

    // Regex validation historically ignores aggregate-local ORDER BY on
    // FunctionCall, whereas raw-source validation explicitly inspects it.
    // This flag preserves that legacy distinction, rather than silently
    // widening a security proof during a stack-safety refactor.
    let private walkSource includeAggregateOrder onExpr onRawRegexDone onSelect onOrder initial =
        let pending = ResizeArray<WalkFrame>()
        let inline push frame = pending.Add frame
        let pushExprs (expressions: Expr list) =
            let items = List.toArray expressions
            for index = items.Length - 1 downto 0 do
                push (WalkExpr items.[index])
        let pushOrders (orders: OrderBy list) =
            let items = List.toArray orders
            for index = items.Length - 1 downto 0 do
                push (WalkOrder items.[index])
        push initial
        while pending.Count > 0 do
            let index = pending.Count - 1
            let frame = pending.[index]
            pending.RemoveAt index
            match frame with
            | WalkExpr expr ->
                onExpr expr
                match expr with
                | Spanned(_, value) -> push (WalkExpr value)
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ -> ()
                | DateAdd(_, left, right) | DateDiff(_, left, right)
                | Binary(_, left, right) | RegexMatch(left, right)
                | FilteredAggregate(left, right) | Like(left, right, _, _, _) ->
                    push (WalkExpr right)
                    push (WalkExpr left)
                | Unary(_, value) | Cast(value, _) | Extract(_, value)
                | PostgresJsonAccess(value, _, _) | IsNull(value, _) ->
                    push (WalkExpr value)
                | RawRegexCall(args, _) ->
                    push WalkAfterRegexArguments
                    pushExprs args
                | FunctionCall call ->
                    if includeAggregateOrder then pushOrders call.AggregateOrderBy
                    pushExprs call.Arguments
                | Windowed(value, window) ->
                    pushOrders window.OrderBy
                    pushExprs window.PartitionBy
                    push (WalkExpr value)
                | SimpleCase(input, branches, fallback) ->
                    fallback |> Option.iter (fun value -> push (WalkExpr value))
                    NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                        push (WalkExpr branch.Result)
                        push (WalkExpr branch.Match)) branches
                    push (WalkExpr input)
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter (fun value -> push (WalkExpr value))
                    NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                        push (WalkExpr branch.Result)
                        push (WalkExpr branch.Condition)) branches
                | InList(value, items, _) ->
                    NonEmpty.iterReverse (fun item -> push (WalkExpr item)) items
                    push (WalkExpr value)
                | InSubquery(value, query, _) ->
                    push (WalkQuery query)
                    push (WalkExpr value)
                | Between(value, lower, upper, _) ->
                    push (WalkExpr upper)
                    push (WalkExpr lower)
                    push (WalkExpr value)
                | ScalarSubquery query | Exists(query, _) -> push (WalkQuery query)
            | WalkAfterRegexArguments -> onRawRegexDone ()
            | WalkOrder order ->
                onOrder order
                push (WalkExpr order.Expression)
            | WalkSource source ->
                match source with
                | NamedTable _ | CteTable _ -> ()
                | DerivedTable(query, _) | LateralDerivedTable(query, _) ->
                    push (WalkQuery query)
            | WalkQuery query ->
                pushOrders query.OrderBy
                let branches = List.toArray query.SetOperations
                for index = branches.Length - 1 downto 0 do
                    push (WalkQuery branches.[index].Query)
                push (WalkSelect query.Head)
            | WalkSelect select ->
                push (WalkAfterCtes select)
                let ctes = List.toArray select.Ctes
                for index = ctes.Length - 1 downto 0 do
                    push (WalkQuery ctes.[index].Query)
            | WalkAfterCtes select ->
                onSelect select
                select.Having |> Option.iter (fun value -> push (WalkExpr value))
                pushExprs select.GroupBy
                select.Where |> Option.iter (fun value -> push (WalkExpr value))
                let joins = List.toArray select.Joins
                for index = joins.Length - 1 downto 0 do
                    match joins.[index].Predicate with
                    | Some predicate -> push (WalkExpr predicate)
                    | None -> ()
                    push (WalkSource joins.[index].Source)
                select.From |> Option.iter (fun value -> push (WalkSource value))
                NonEmpty.iterReverse (fun item ->
                    push (WalkExpr item.Expression)) select.ProjectionItems
                match select.DistinctMode with
                | SelectDistinct.DistinctOn values ->
                    NonEmpty.iterReverse (fun value -> push (WalkExpr value)) values
                | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> ()

    let private verifyRegexWalk regexProof frame =
        walkSource false ignore
            (fun () -> requireSourceRegexCapability regexProof)
            ignore ignore frame

    let private verifySourceRegexExpr regexProof expression =
        verifyRegexWalk regexProof (WalkExpr expression)

    let private verifySourceRegexSource regexProof source =
        verifyRegexWalk regexProof (WalkSource source)

    let private verifySourceRegexQuery regexProof query =
        verifyRegexWalk regexProof (WalkQuery query)

    let verifyRegexDocument regexProof document =
        match document.Statement with
        | QueryStatement query -> verifySourceRegexQuery regexProof query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (verifySourceRegexExpr regexProof))
            | QuerySource query -> verifySourceRegexQuery regexProof query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)
        | UpdateStatement update ->
            update.Ctes |> List.iter (fun cte -> verifySourceRegexQuery regexProof cte.Query)
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> verifySourceRegexExpr regexProof assignment.Value)
            update.From |> List.iter (verifySourceRegexSource regexProof)
            update.Where |> Option.iter (verifySourceRegexExpr regexProof)
            update.Returning |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (verifySourceRegexSource regexProof)
            delete.Where |> Option.iter (verifySourceRegexExpr regexProof)
            delete.Returning |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)
        | MergeStatement merge ->
            merge.Source.SourceValues |> NonEmpty.iter (verifySourceRegexExpr regexProof)
            verifySourceRegexExpr regexProof merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> verifySourceRegexExpr regexProof item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (verifySourceRegexExpr regexProof))

    let private validateRawSourceFunction source expression =
        match Expr.unspan expression with
        | FunctionCall call when FunctionName.hasQuotedParts call.Name ->
            // Quoted PostgreSQL function identifiers are native opaque identities. Do not
            // reinterpret a case-sensitive custom function such as "Sum" as built-in SUM.
            ()
        | FunctionCall call ->
            let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
            match SqlDateOnlyCapabilityRules.SourceValidationError(source, name, call.Arguments.Length) with
            | null -> ()
            | message -> compilationError message
            match SqlSourceFunctionRegistry.Find(name) |> Option.ofObj with
            | Some contract ->
                match contract.ValidationError(source, call.Arguments.Length) with
                | null -> ()
                | message -> compilationError message
            | None ->
                let currentKind =
                    match name with
                    | "CURRENT_DATE" -> Some SqlCurrentTemporalKind.Date
                    | "CURRENT_TIME" -> Some SqlCurrentTemporalKind.Time
                    | "CURRENT_TIMESTAMP" -> Some SqlCurrentTemporalKind.Timestamp
                    | _ -> None
                match currentKind with
                | Some kind ->
                    match SqlCurrentTemporalCapabilityRules.SourceValidationError(kind, source) with
                    | null -> ()
                    | message -> compilationError message
                | None -> ()
        | _ -> ()

    let private requireSourceOrderingCapability = function
        | ProvenCapability -> ()
        | RejectedCapability rejection ->
            raise (SqlCompilationException(sourceCapabilityMessage rejection))

    let private validateRawSourceOrder orderingProofs (order: OrderBy) =
        match order.NullOrdering with
        | NullOrdering.Default -> ()
        | NullOrdering.NullsFirst -> requireSourceOrderingCapability orderingProofs.NullsFirst
        | NullOrdering.NullsLast -> requireSourceOrderingCapability orderingProofs.NullsLast

    let private validateRawConcat source mySqlPipes =
        if source = SqlAgentToolType.MySQL
           && mySqlPipes <> RewriteParser.MySqlPipesSemantics.PipesAsConcat then
            match SqlConcatCapabilityRules.SourceSemanticValidationError(source) with
            | null -> ()
            | message -> compilationError message
        elif source = SqlAgentToolType.MsSqlServer then
            match SqlConcatCapabilityRules.RawSourceSyntaxError(source) with
            | null -> ()
            | message -> compilationError message

    let private validateRawWalk source orderingProofs mySqlPipes frame =
        let onExpr expression =
            validateRawSourceFunction source expression
            match expression with
            | Binary(BinaryOperator.Concat, _, _) -> validateRawConcat source mySqlPipes
            | Binary(BinaryOperator.Modulo, _, _) ->
                match SqlModuloCapabilityRules.SourceValidationError(source) with
                | null -> ()
                | message -> compilationError message
            | Extract _ ->
                match SqlExtractSourceCapabilityRules.SourceValidationError(source) with
                | null -> ()
                | message -> compilationError message
            | _ -> ()
        let onSelect select =
            match select.DistinctMode with
            | SelectDistinct.DistinctOn _ ->
                match SqlDistinctOnCapabilityRules.SourceValidationError(source) with
                | null -> ()
                | message -> compilationError message
            | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> ()
        walkSource true onExpr ignore onSelect
            (validateRawSourceOrder orderingProofs) frame

    let private validateRawSourceExpr source orderingProofs mySqlPipes expression =
        validateRawWalk source orderingProofs mySqlPipes (WalkExpr expression)

    let private validateRawSourceTable source orderingProofs mySqlPipes table =
        validateRawWalk source orderingProofs mySqlPipes (WalkSource table)

    let private validateRawSourceQuery source orderingProofs mySqlPipes query =
        validateRawWalk source orderingProofs mySqlPipes (WalkQuery query)

    let validateRawDocument source orderingProofs mySqlPipes document =
        match document.Statement with
        | QueryStatement query -> validateRawSourceQuery source orderingProofs mySqlPipes query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (validateRawSourceExpr source orderingProofs mySqlPipes))
            | QuerySource query -> validateRawSourceQuery source orderingProofs mySqlPipes query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Expression)
        | UpdateStatement update ->
            update.Ctes |> List.iter (fun cte -> validateRawSourceQuery source orderingProofs mySqlPipes cte.Query)
            update.AssignmentItems |> NonEmpty.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Value)
            update.From |> List.iter (validateRawSourceTable source orderingProofs mySqlPipes)
            update.Where |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
            update.Returning |> List.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (validateRawSourceTable source orderingProofs mySqlPipes)
            delete.Where |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
            delete.Returning |> List.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Expression)
        | MergeStatement merge ->
            merge.Source.SourceValues |> NonEmpty.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
            validateRawSourceExpr source orderingProofs mySqlPipes merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (validateRawSourceExpr source orderingProofs mySqlPipes))


