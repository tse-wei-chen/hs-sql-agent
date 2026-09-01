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

    let rec private verifySourceRegexExpr regexProof expression =
        match expression with
        | Spanned(_, inner) -> verifySourceRegexExpr regexProof inner
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (verifySourceRegexExpr regexProof)
            requireSourceRegexCapability regexProof
        | RegexMatch(value, pattern) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexExpr regexProof pattern
        | Unary(_, operand) -> verifySourceRegexExpr regexProof operand
        | Binary(_, left, right) ->
            verifySourceRegexExpr regexProof left
            verifySourceRegexExpr regexProof right
        | Like(value, pattern, _, _, _) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexExpr regexProof pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (verifySourceRegexExpr regexProof)
        | FilteredAggregate(value, predicate) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexExpr regexProof predicate
        | Windowed(value, window) ->
            verifySourceRegexExpr regexProof value
            window.PartitionBy |> List.iter (verifySourceRegexExpr regexProof)
            window.OrderBy |> List.iter (fun order -> verifySourceRegexExpr regexProof order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            verifySourceRegexExpr regexProof value
        | SimpleCase(input, branches, fallback) ->
            verifySourceRegexExpr regexProof input
            branches |> NonEmpty.iter (fun branch ->
                verifySourceRegexExpr regexProof branch.Match
                verifySourceRegexExpr regexProof branch.Result)
            fallback |> Option.iter (verifySourceRegexExpr regexProof)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                verifySourceRegexExpr regexProof branch.Condition
                verifySourceRegexExpr regexProof branch.Result)
            fallback |> Option.iter (verifySourceRegexExpr regexProof)
        | InList(value, items, _) ->
            verifySourceRegexExpr regexProof value
            items |> NonEmpty.iter (verifySourceRegexExpr regexProof)
        | InSubquery(value, query, _) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexQuery regexProof query
        | Between(value, lower, upper, _) ->
            verifySourceRegexExpr regexProof value
            verifySourceRegexExpr regexProof lower
            verifySourceRegexExpr regexProof upper
        | IsNull(value, _) ->
            verifySourceRegexExpr regexProof value
        | ScalarSubquery query | Exists(query, _) ->
            verifySourceRegexQuery regexProof query

    and private verifySourceRegexSource regexProof source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) -> verifySourceRegexQuery regexProof query

    and private verifySourceRegexSelect regexProof select =
        select.Ctes |> List.iter (fun cte -> verifySourceRegexQuery regexProof cte.Query)
        iterDistinctOn (verifySourceRegexExpr regexProof) select
        select.Projection |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)
        select.From |> Option.iter (verifySourceRegexSource regexProof)
        select.Joins |> List.iter (fun join ->
            verifySourceRegexSource regexProof join.Source
            join.Predicate |> Option.iter (verifySourceRegexExpr regexProof))
        select.Where |> Option.iter (verifySourceRegexExpr regexProof)
        select.GroupBy |> List.iter (verifySourceRegexExpr regexProof)
        select.Having |> Option.iter (verifySourceRegexExpr regexProof)

    and private verifySourceRegexQuery regexProof query =
        verifySourceRegexSelect regexProof query.Head
        query.SetOperations |> List.iter (fun branch -> verifySourceRegexQuery regexProof branch.Query)
        query.OrderBy |> List.iter (fun order -> verifySourceRegexExpr regexProof order.Expression)

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
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> verifySourceRegexExpr regexProof assignment.Value)
            update.From |> List.iter (verifySourceRegexSource regexProof)
            update.Where |> Option.iter (verifySourceRegexExpr regexProof)
            update.Returning |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (verifySourceRegexSource regexProof)
            delete.Where |> Option.iter (verifySourceRegexExpr regexProof)
            delete.Returning |> List.iter (fun item -> verifySourceRegexExpr regexProof item.Expression)

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

    let rec private validateRawSourceExpr source orderingProofs mySqlPipes expression =
        validateRawSourceFunction source expression
        match expression with
        | Spanned(_, inner) -> validateRawSourceExpr source orderingProofs mySqlPipes inner
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) -> validateRawSourceExpr source orderingProofs mySqlPipes operand
        | Binary(BinaryOperator.Concat, left, right) ->
            validateRawConcat source mySqlPipes
            validateRawSourceExpr source orderingProofs mySqlPipes left
            validateRawSourceExpr source orderingProofs mySqlPipes right
        | Binary(BinaryOperator.Modulo, left, right) ->
            match SqlModuloCapabilityRules.SourceValidationError(source) with
            | null -> ()
            | message -> compilationError message
            validateRawSourceExpr source orderingProofs mySqlPipes left
            validateRawSourceExpr source orderingProofs mySqlPipes right
        | Binary(_, left, right) ->
            validateRawSourceExpr source orderingProofs mySqlPipes left
            validateRawSourceExpr source orderingProofs mySqlPipes right
        | Like(value, pattern, _, _, _) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
            validateRawSourceExpr source orderingProofs mySqlPipes pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
        | RegexMatch(value, pattern) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
            validateRawSourceExpr source orderingProofs mySqlPipes pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
            call.AggregateOrderBy
            |> List.iter (fun order ->
                validateRawSourceOrder orderingProofs order
                validateRawSourceExpr source orderingProofs mySqlPipes order.Expression)
        | FilteredAggregate(value, predicate) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
            validateRawSourceExpr source orderingProofs mySqlPipes predicate
        | Windowed(value, window) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
            window.PartitionBy |> List.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
            window.OrderBy
            |> List.iter (fun order ->
                validateRawSourceOrder orderingProofs order
                validateRawSourceExpr source orderingProofs mySqlPipes order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
        | SimpleCase(input, branches, fallback) ->
            validateRawSourceExpr source orderingProofs mySqlPipes input
            branches |> NonEmpty.iter (fun branch ->
                validateRawSourceExpr source orderingProofs mySqlPipes branch.Match
                validateRawSourceExpr source orderingProofs mySqlPipes branch.Result)
            fallback |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                validateRawSourceExpr source orderingProofs mySqlPipes branch.Condition
                validateRawSourceExpr source orderingProofs mySqlPipes branch.Result)
            fallback |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
        | InList(value, items, _) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
            items |> NonEmpty.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
        | InSubquery(value, query, _) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
            validateRawSourceQuery source orderingProofs mySqlPipes query
        | Between(value, lower, upper, _) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
            validateRawSourceExpr source orderingProofs mySqlPipes lower
            validateRawSourceExpr source orderingProofs mySqlPipes upper
        | IsNull(value, _) ->
            validateRawSourceExpr source orderingProofs mySqlPipes value
        | ScalarSubquery query | Exists(query, _) ->
            validateRawSourceQuery source orderingProofs mySqlPipes query

    and private validateRawSourceTable source orderingProofs mySqlPipes table =
        match table with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) -> validateRawSourceQuery source orderingProofs mySqlPipes query

    and private validateRawSourceSelect source orderingProofs mySqlPipes select =
        select.Ctes |> List.iter (fun cte -> validateRawSourceQuery source orderingProofs mySqlPipes cte.Query)
        match select.DistinctMode with
        | SelectDistinct.DistinctOn _ ->
            match SqlDistinctOnCapabilityRules.SourceValidationError(source) with
            | null -> ()
            | message -> compilationError message
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()
        iterDistinctOn (validateRawSourceExpr source orderingProofs mySqlPipes) select
        select.Projection |> List.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Expression)
        select.From |> Option.iter (validateRawSourceTable source orderingProofs mySqlPipes)
        select.Joins |> List.iter (fun join ->
            validateRawSourceTable source orderingProofs mySqlPipes join.Source
            join.Predicate |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes))
        select.Where |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
        select.GroupBy |> List.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
        select.Having |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes)

    and private validateRawSourceQuery source orderingProofs mySqlPipes query =
        validateRawSourceSelect source orderingProofs mySqlPipes query.Head
        query.SetOperations |> List.iter (fun branch -> validateRawSourceQuery source orderingProofs mySqlPipes branch.Query)
        query.OrderBy
        |> List.iter (fun order ->
            validateRawSourceOrder orderingProofs order
            validateRawSourceExpr source orderingProofs mySqlPipes order.Expression)

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
            update.AssignmentItems |> NonEmpty.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Value)
            update.From |> List.iter (validateRawSourceTable source orderingProofs mySqlPipes)
            update.Where |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
            update.Returning |> List.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (validateRawSourceTable source orderingProofs mySqlPipes)
            delete.Where |> Option.iter (validateRawSourceExpr source orderingProofs mySqlPipes)
            delete.Returning |> List.iter (fun item -> validateRawSourceExpr source orderingProofs mySqlPipes item.Expression)


