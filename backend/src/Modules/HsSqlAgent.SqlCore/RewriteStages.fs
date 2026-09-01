namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlTranslation.DateFormats
open HsSqlAgent.SqlCore.SqlTranslation.Functions
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteStages =

    type ValidatedSql = private ValidatedSql of Document * TargetRuntime

    module Validated =
        let internal value (ValidatedSql(document, _)) = document
        let internal targetRuntime (ValidatedSql(_, targetRuntime)) = targetRuntime

    let private functionRegistry : IFunctionRegistry =
        FunctionRegistry(FunctionDefinitionLoader.LoadEmbedded()) :> IFunctionRegistry

    let private dateFormats = DateFormatTranslator()

    let private sourceProvider = function
        | RewriteParser.SourceDialect.PostgreSql -> SqlAgentToolType.Postgres
        | RewriteParser.SourceDialect.MySql -> SqlAgentToolType.MySQL
        | RewriteParser.SourceDialect.SqlServer -> SqlAgentToolType.MsSqlServer
        | RewriteParser.SourceDialect.SQLite -> SqlAgentToolType.Sqlite
        | RewriteParser.SourceDialect.Oracle -> SqlAgentToolType.Oracle
        | RewriteParser.SourceDialect.Firebird -> SqlAgentToolType.Firebird

    let private targetProvider = function
        | TargetRuntime.PostgreSqlRuntime -> SqlAgentToolType.Postgres
        | TargetRuntime.MySqlRuntime -> SqlAgentToolType.MySQL
        | TargetRuntime.SqlServerRuntime _ -> SqlAgentToolType.MsSqlServer
        | TargetRuntime.SQLiteRuntime -> SqlAgentToolType.Sqlite
        | TargetRuntime.OracleRuntime -> SqlAgentToolType.Oracle
        | TargetRuntime.FirebirdRuntime -> SqlAgentToolType.Firebird

    let private compilationError message =
        raise (SqlCompilationException(message))

    let private diagnosticDataKey = "HsSqlAgent.SqlCore.Diagnostic"

    let private stageDiagnostic code stage category (span: Span) message =
        let diagnosticSpan =
            if span.Start < 0 || span.Length < 0 then null
            else SqlDiagnosticSpan(span.Start, span.Length)
        SqlDiagnostic(code, stage, category, message, diagnosticSpan)

    let private withCompilationDiagnostic code stage category (span: Span) (action: unit -> 'T) =
        try
            action()
        with
        | :? SqlCompilationException as ex when isNull ex.Diagnostic ->
            let diagnostic = stageDiagnostic code stage category span ex.Message
            raise (SqlCompilationException(ex.Message, ex, diagnostic))
        | :? SqlCompilationException ->
            reraise()
        | :? InvalidOperationException as ex ->
            ex.Data[diagnosticDataKey] <- stageDiagnostic code stage category span ex.Message
            reraise()

    let private iterDistinctOn action (select: Select) =
        match select.DistinctMode with
        | SelectDistinct.DistinctOn expressions -> expressions |> NonEmpty.iter action
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()

    let private mapDistinctOn action = function
        | SelectDistinct.DistinctOn expressions ->
            expressions |> NonEmpty.map action |> SelectDistinct.DistinctOn
        | SelectDistinct.AllRows -> SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> SelectDistinct.DistinctRows

    let private sourceCapabilityMessage rejection =
        match CapabilityRejection.side rejection with
        | CapabilitySide.SourceCapability -> CapabilityRejection.message rejection
        | CapabilitySide.TargetCapability ->
            invalidOp "Target capability proof reached source semantic validation."

    let private requireSourceRegexCapability = function
        | ProvenCapability -> ()
        | RejectedCapability rejection ->
            raise (SqlCompilationException(sourceCapabilityMessage rejection))

    let rec private verifySourceRegexExpr regexProof expression =
        match expression with
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

    let private verifySourceRegexDocument regexProof document =
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
        match expression with
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

    let private validateRawSourceDocument source orderingProofs mySqlPipes document =
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

    let private profileVersion (profile: SqlProviderCapabilityProfile | null) =
        match profile with
        | null -> None
        | value -> Option.ofObj value.ServerVersion

    let private profileCompatibility (profile: SqlProviderCapabilityProfile | null) =
        match profile with
        | null -> None
        | value when value.CompatibilityLevel.HasValue -> Some value.CompatibilityLevel.Value
        | _ -> None

    let private versionCapabilityError profile minimum providerName side =
        match profileVersion profile with
        | None ->
            Some(
                "SQL capability 'aggregate.string.ordering' requires a declared "
                + providerName + " " + side
                + " capability profile with ServerVersion " + string minimum + "+.")
        | Some declared when declared.CompareTo(minimum) < 0 ->
            Some(
                "SQL capability 'aggregate.string.ordering' requires "
                + providerName + " " + side + " ServerVersion " + string minimum
                + "+; declared version is " + string declared + ".")
        | Some _ -> None

    let private sqlServerOrderingProfileError profile side =
        match versionCapabilityError profile (Version(14, 0)) "SQL Server" side with
        | Some message -> Some message
        | None ->
            match profileCompatibility profile with
            | None ->
                Some(
                    "SQL capability 'aggregate.string.ordering' requires a declared SQL Server "
                    + side + " capability profile with CompatibilityLevel 110+.")
            | Some level when level < 110 ->
                Some(
                    "SQL capability 'aggregate.string.ordering' requires SQL Server "
                    + side + " CompatibilityLevel 110+; declared level is " + string level + ".")
            | Some _ -> None

    let private orderingTargetError target targetProfile =
        match target with
        | SqlAgentToolType.Postgres | SqlAgentToolType.MySQL -> None
        | SqlAgentToolType.Sqlite ->
            versionCapabilityError targetProfile (Version(3, 44)) "SQLite" "target"
        | SqlAgentToolType.MsSqlServer ->
            sqlServerOrderingProfileError targetProfile "target"
        | SqlAgentToolType.Oracle ->
            versionCapabilityError targetProfile (Version(11, 2)) "Oracle" "target"
        | provider ->
            Some(
                "SQL capability 'aggregate.string.ordering' is not supported by provider "
                + string provider + " for this Core plan; aggregate-local ORDER BY remains fail-closed.")

    let private orderingSourceError source sourceProfile functionName syntax =
        let syntaxError expected =
            Some(
                string source + " raw " + functionName
                + " aggregate ordering must use " + expected + ".")
        match source, functionName, syntax with
        | SqlAgentToolType.Postgres, "STRING_AGG", InlineAggregateOrder -> None
        | SqlAgentToolType.Postgres, "STRING_AGG", _ ->
            syntaxError "inline ORDER BY inside the function call"
        | SqlAgentToolType.Sqlite, "GROUP_CONCAT", InlineAggregateOrder ->
            versionCapabilityError sourceProfile (Version(3, 44)) "SQLite" "source"
        | SqlAgentToolType.Sqlite, "GROUP_CONCAT", _ ->
            syntaxError "inline ORDER BY inside the function call"
        | SqlAgentToolType.MsSqlServer, "STRING_AGG", WithinGroupAggregateOrder ->
            sqlServerOrderingProfileError sourceProfile "source"
        | SqlAgentToolType.MsSqlServer, "STRING_AGG", _ ->
            syntaxError "WITHIN GROUP (ORDER BY ...)"
        | SqlAgentToolType.Oracle, "LISTAGG", WithinGroupAggregateOrder ->
            versionCapabilityError sourceProfile (Version(11, 2)) "Oracle" "source"
        | SqlAgentToolType.Oracle, "LISTAGG", _ ->
            syntaxError "WITHIN GROUP (ORDER BY ...)"
        | SqlAgentToolType.MySQL, "GROUP_CONCAT", InlineAggregateOrder -> None
        | SqlAgentToolType.MySQL, "GROUP_CONCAT", _ ->
            syntaxError "inline ORDER BY inside the function call"
        | _ ->
            Some(
                "SQL capability 'aggregate.string.ordering' rejects this raw aggregate-local ORDER BY source shape; "
                + "provider-specific aggregate ordering remains fail-closed.")

    let rec private expressionReferencesColumn expression =
        match expression with
        | Column _ | BoundColumn _ -> true
        | Unary(_, value) | Cast(value, _) | Extract(_, value) | IsNull(value, _) ->
            expressionReferencesColumn value
        | Binary(_, left, right) | RegexMatch(left, right) ->
            expressionReferencesColumn left || expressionReferencesColumn right
        | Like(value, pattern, _, _, _) ->
            expressionReferencesColumn value || expressionReferencesColumn pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.exists expressionReferencesColumn
        | FunctionCall call ->
            call.Arguments |> List.exists expressionReferencesColumn
        | FilteredAggregate(value, predicate) ->
            expressionReferencesColumn value || expressionReferencesColumn predicate
        | Windowed(value, window) ->
            expressionReferencesColumn value
            || (window.PartitionBy |> List.exists expressionReferencesColumn)
            || (window.OrderBy |> List.exists (fun order -> expressionReferencesColumn order.Expression))
        | SimpleCase(input, branches, fallback) ->
            expressionReferencesColumn input
            || (branches |> NonEmpty.toList |> List.exists (fun branch ->
                expressionReferencesColumn branch.Match || expressionReferencesColumn branch.Result))
            || (fallback |> Option.exists expressionReferencesColumn)
        | SearchedCase(branches, fallback) ->
            (branches |> NonEmpty.toList |> List.exists (fun branch ->
                expressionReferencesColumn branch.Condition || expressionReferencesColumn branch.Result))
            || (fallback |> Option.exists expressionReferencesColumn)
        | InList(value, items, _) ->
            expressionReferencesColumn value || (items |> NonEmpty.toList |> List.exists expressionReferencesColumn)
        | InSubquery(value, _, _) | Between(value, _, _, _) ->
            expressionReferencesColumn value
        | ScalarSubquery _ | Exists _ -> true
        | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> false

    let private validateAggregateCall enforceSource source sourceProfile target targetProfile (call: FunctionCall) =
        let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
        match call.AggregateSeparator with
        | Some _ when enforceSource && not (source = SqlAgentToolType.MySQL && name = "GROUP_CONCAT") ->
            raise (SqlCompilationException(
                "SEPARATOR clause is source syntax owned by MySQL GROUP_CONCAT; this source dialect remains fail-closed."))
        | _ -> ()

        if not call.AggregateOrderBy.IsEmpty then
            match orderingTargetError target targetProfile with
            | Some message -> raise (SqlCompilationException(message))
            | None -> ()

            if enforceSource then
                match orderingSourceError source sourceProfile name call.AggregateOrderSyntax with
                | Some message -> raise (SqlCompilationException(message))
                | None -> ()

            if call.IsDistinct then
                raise (SqlCompilationException(
                    "String aggregation DISTINCT with aggregate-local ORDER BY remains fail-closed until provider-specific restrictions are modeled explicitly."))

            if target = SqlAgentToolType.MsSqlServer
               && call.AggregateOrderBy
                  |> List.exists (fun order -> not (expressionReferencesColumn order.Expression)) then
                raise (SqlCompilationException(
                    "SQL Server STRING_AGG WITHIN GROUP ordering requires non-constant expressions; Core requires each ordering expression to reference a column."))

    let rec private validateAggregateExpr enforceSource source sourceProfile target targetProfile expression =
        match expression with
        | FunctionCall call ->
            validateAggregateCall enforceSource source sourceProfile target targetProfile call
            call.Arguments |> List.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
            call.AggregateOrderBy
            |> List.iter (fun order -> validateAggregateExpr enforceSource source sourceProfile target targetProfile order.Expression)
        | Unary(_, value) | Cast(value, _) | Extract(_, value) | IsNull(value, _) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile value
        | Binary(_, left, right) | RegexMatch(left, right) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile left
            validateAggregateExpr enforceSource source sourceProfile target targetProfile right
        | Like(value, pattern, _, _, _) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile value
            validateAggregateExpr enforceSource source sourceProfile target targetProfile pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
        | FilteredAggregate(value, predicate) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile value
            validateAggregateExpr enforceSource source sourceProfile target targetProfile predicate
        | Windowed(value, window) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile value
            window.PartitionBy |> List.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
            window.OrderBy |> List.iter (fun order -> validateAggregateExpr enforceSource source sourceProfile target targetProfile order.Expression)
        | SimpleCase(input, branches, fallback) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile input
            branches |> NonEmpty.iter (fun branch ->
                validateAggregateExpr enforceSource source sourceProfile target targetProfile branch.Match
                validateAggregateExpr enforceSource source sourceProfile target targetProfile branch.Result)
            fallback |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                validateAggregateExpr enforceSource source sourceProfile target targetProfile branch.Condition
                validateAggregateExpr enforceSource source sourceProfile target targetProfile branch.Result)
            fallback |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
        | InList(value, items, _) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile value
            items |> NonEmpty.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
        | InSubquery(value, query, _) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile value
            validateAggregateQuery enforceSource source sourceProfile target targetProfile query
        | Between(value, lower, upper, _) ->
            validateAggregateExpr enforceSource source sourceProfile target targetProfile value
            validateAggregateExpr enforceSource source sourceProfile target targetProfile lower
            validateAggregateExpr enforceSource source sourceProfile target targetProfile upper
        | ScalarSubquery query | Exists(query, _) ->
            validateAggregateQuery enforceSource source sourceProfile target targetProfile query
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()

    and private validateAggregateSource enforceSource source sourceProfile target targetProfile table =
        match table with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _) ->
            validateAggregateQuery enforceSource source sourceProfile target targetProfile query
        | LateralDerivedTable(query, _) ->
            if enforceSource then
                match SqlLateralDerivedTableCapabilityRules.SourceValidationError(source, sourceProfile) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            match SqlLateralDerivedTableCapabilityRules.TargetValidationError(target, targetProfile) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            validateAggregateQuery enforceSource source sourceProfile target targetProfile query

    and private validateAggregateSelect enforceSource source sourceProfile target targetProfile select =
        if select.Ctes |> List.exists (fun cte -> cte.RecursiveScope) then
            if enforceSource then
                match SqlRecursiveCteCapabilityRules.SourceValidationError(source, sourceProfile) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            if source <> target then
                raise (SqlCompilationException(
                    "SQL capability 'select.recursive_cte' is currently native-only; cross-provider recursive evaluation semantics are not proven. Source provider "
                    + string source + ", target provider " + string target + "."))
            match SqlRecursiveCteCapabilityRules.TargetValidationError(target, targetProfile) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
        select.Ctes |> List.iter (fun cte -> validateAggregateQuery enforceSource source sourceProfile target targetProfile cte.Query)
        iterDistinctOn (validateAggregateExpr enforceSource source sourceProfile target targetProfile) select
        select.Projection |> List.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Expression)
        select.From |> Option.iter (validateAggregateSource enforceSource source sourceProfile target targetProfile)
        select.Joins |> List.iter (fun join ->
            validateAggregateSource enforceSource source sourceProfile target targetProfile join.Source
            join.Predicate |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile))
        select.Where |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
        select.GroupBy |> List.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
        select.Having |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)

    and private validateAggregateQuery enforceSource source sourceProfile target targetProfile query =
        if query.FetchPercent.IsSome then
            if query.Limit.IsSome then
                raise (SqlCompilationException(
                    "FETCH row count and FETCH percentage cannot coexist in one canonical Query."))
            if enforceSource then
                match SqlFetchPercentCapabilityRules.SourceValidationError(source, sourceProfile) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            match SqlFetchPercentCapabilityRules.TargetValidationError(target, targetProfile) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
        if query.FetchWithTies then
            if query.Limit.IsNone && query.FetchPercent.IsNone then
                raise (SqlCompilationException(
                    "SQL capability 'select.fetch_with_ties' requires a FETCH row count or percentage."))
            if query.OrderBy.IsEmpty then
                raise (SqlCompilationException(
                    "SQL capability 'select.fetch_with_ties' requires ORDER BY so tie equality has a defined sort key."))
            if enforceSource then
                match SqlFetchWithTiesCapabilityRules.SourceValidationError(source, sourceProfile) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            match SqlFetchWithTiesCapabilityRules.TargetValidationError(target, targetProfile) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
        validateAggregateSelect enforceSource source sourceProfile target targetProfile query.Head
        query.SetOperations |> List.iter (fun branch -> validateAggregateQuery enforceSource source sourceProfile target targetProfile branch.Query)
        query.OrderBy |> List.iter (fun order -> validateAggregateExpr enforceSource source sourceProfile target targetProfile order.Expression)

    let private validateAggregateDocument enforceSource source sourceProfile target targetProfile document =
        match document.Statement with
        | QueryStatement query ->
            validateAggregateQuery enforceSource source sourceProfile target targetProfile query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows ->
                if insert.Columns.IsEmpty && source <> target then
                    raise (SqlCompilationException(
                        "SQL capability 'dml.insert_implicit_columns' is native-only because omitted INSERT target columns depend on provider table-column order. Source provider "
                        + string source + ", target provider " + string target + "."))
                rows |> NonEmpty.iter (NonEmpty.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile))
            | QuerySource query ->
                if insert.Columns.IsEmpty && source <> target then
                    raise (SqlCompilationException(
                        "SQL capability 'dml.insert_implicit_columns' is native-only because omitted INSERT target columns depend on provider table-column order. Source provider "
                        + string source + ", target provider " + string target + "."))
                validateAggregateQuery enforceSource source sourceProfile target targetProfile query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Expression)
        | UpdateStatement update ->
            if not update.From.IsEmpty
               && source <> target
               && (source = SqlAgentToolType.MsSqlServer || target = SqlAgentToolType.MsSqlServer) then
                raise (SqlCompilationException(
                    "SQL capability 'dml.update.from' is native-only when SQL Server participates because duplicate-match and target-row selection semantics are not proven equivalent across providers. Source provider "
                    + string source + ", target provider " + string target + "."))
            update.AssignmentItems |> NonEmpty.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Value)
            update.From |> List.iter (validateAggregateSource enforceSource source sourceProfile target targetProfile)
            update.Where |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
            update.Returning |> List.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (validateAggregateSource enforceSource source sourceProfile target targetProfile)
            delete.Where |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
            delete.Returning |> List.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Expression)

    let private emptyFunction name arguments =
        { FunctionCall.Name = FunctionName.create name
          Arguments = arguments
          IsDistinct = false
          AggregateOrderBy = []
          AggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
          AggregateSeparator = None }

    let private textLiteral label = function
        | Literal(ScalarValue.Text value) -> value
        | _ -> compilationError (label + " must be a string literal.")

    let private keywordValue label = function
        | Column identifier
        | BoundColumn(identifier, _) ->
            let parts = Identifier.parts identifier
            if parts.Length <> 1 then compilationError (label + " must be an unquoted SQL keyword.")
            parts.Head.Value
        | Literal(ScalarValue.Text value) -> value
        | _ -> compilationError (label + " must be an unquoted SQL keyword.")

    let private dateOnlyOperand target expression =
        match target with
        | SqlAgentToolType.Oracle ->
            FunctionCall(emptyFunction "TRUNC" [ Cast(expression, CastType.create "DATE") ])
        | SqlAgentToolType.MySQL
        | SqlAgentToolType.Sqlite ->
            FunctionCall(emptyFunction "DATE" [ expression ])
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.MsSqlServer
        | SqlAgentToolType.Firebird ->
            Cast(expression, CastType.create "DATE")
        | value ->
            compilationError ("Unsupported target provider '" + string value + "' for portable DATEDIFF DAY normalization.")

    let private canonicalCall (call: FunctionCall) name arguments =
        FunctionCall
            { call with
                Name = FunctionName.create name
                Arguments = arguments
                AggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
                AggregateSeparator = None }

    let rec private normalizeExpr source target expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> expression
        | Unary(Positive, operand) -> normalizeExpr source target operand
        | Unary(op, operand) -> Unary(op, normalizeExpr source target operand)
        | Binary(op, left, right) -> Binary(op, normalizeExpr source target left, normalizeExpr source target right)
        | Like(value, pattern, escape, negated, insensitive) ->
            Like(normalizeExpr source target value, normalizeExpr source target pattern, escape, negated, insensitive)
        | RawRegexCall(arguments, _) ->
            let normalized = arguments |> List.map (normalizeExpr source target)
            match normalized with
            | [ value; pattern ] -> RegexMatch(value, pattern)
            | values ->
                compilationError (
                    "Function 'CORE_REGEX_MATCH' requires 2 argument(s); received "
                    + string values.Length
                    + ".")
        | RegexMatch(value, pattern) ->
            RegexMatch(normalizeExpr source target value, normalizeExpr source target pattern)
        | FunctionCall call ->
            normalizeFunction source target call
        | FilteredAggregate(value, predicate) ->
            FilteredAggregate(normalizeExpr source target value, normalizeExpr source target predicate)
        | Windowed(value, window) ->
            Windowed(normalizeExpr source target value, normalizeWindow source target window)
        | Cast(value, targetType) ->
            Cast(
                normalizeExpr source target value,
                RewriteCastTypes.normalize (sourceProvider source) (targetProvider target) targetType)
        | Extract(field, value) -> Extract(field, normalizeExpr source target value)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(
                normalizeExpr source target input,
                branches
                |> NonEmpty.map (fun (branch: SimpleCaseBranch) ->
                    { Match = normalizeExpr source target branch.Match
                      Result = normalizeExpr source target branch.Result }),
                fallback |> Option.map (normalizeExpr source target))
        | SearchedCase(branches, fallback) ->
            SearchedCase(
                branches
                |> NonEmpty.map (fun (branch: SearchedCaseBranch) ->
                    { Condition = normalizeExpr source target branch.Condition
                      Result = normalizeExpr source target branch.Result }),
                fallback |> Option.map (normalizeExpr source target))
        | InList(value, items, negated) ->
            InList(
                normalizeExpr source target value,
                items |> NonEmpty.map (normalizeExpr source target),
                negated)
        | InSubquery(value, query, negated) ->
            InSubquery(
                normalizeExpr source target value,
                normalizeQuery source target query,
                negated)
        | Between(value, lower, upper, negated) ->
            Between(
                normalizeExpr source target value,
                normalizeExpr source target lower,
                normalizeExpr source target upper,
                negated)
        | IsNull(value, negated) -> IsNull(normalizeExpr source target value, negated)
        | ScalarSubquery query -> ScalarSubquery(normalizeQuery source target query)
        | Exists(query, negated) -> Exists(normalizeQuery source target query, negated)

    and private normalizeFunction source target (call: FunctionCall) =
        let sourceTool = sourceProvider source
        let targetTool = targetProvider target
        let arguments = call.Arguments |> List.map (normalizeExpr source target)
        let orderBy = call.AggregateOrderBy |> List.map (normalizeOrderBy source target)
        let call = { call with Arguments = arguments; AggregateOrderBy = orderBy }
        let sourceName = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
        let sourceRegistryName =
            let postgresCatalogPrefix = "PG_CATALOG."
            if sourceTool = SqlAgentToolType.Postgres
               && sourceName.StartsWith(postgresCatalogPrefix, StringComparison.Ordinal) then
                sourceName.Substring(postgresCatalogPrefix.Length)
            else
                sourceName

        let sourceContract = SqlSourceFunctionRegistry.Find(sourceName)
        let requireSourceContract () =
            match Option.ofObj sourceContract with
            | Some contract -> contract
            | None -> invalidOp ("Source function contract '" + sourceName + "' was unexpectedly absent.")

        let currentKind =
            match sourceName with
            | "CURRENT_DATE" -> Some SqlCurrentTemporalKind.Date
            | "CURRENT_TIME" -> Some SqlCurrentTemporalKind.Time
            | "CURRENT_TIMESTAMP" -> Some SqlCurrentTemporalKind.Timestamp
            | _ -> None

        match currentKind with
        | Some kind ->
            let canonical =
                match kind with
                | SqlCurrentTemporalKind.Date -> "CORE_CURRENT_DATE"
                | SqlCurrentTemporalKind.Time -> "CORE_CURRENT_TIME"
                | SqlCurrentTemporalKind.Timestamp -> "CORE_CURRENT_TIMESTAMP"
                | value -> compilationError ("Unsupported current temporal kind '" + string value + "'.")
            if not arguments.IsEmpty then
                compilationError (sourceName + " does not accept arguments.")
            canonicalCall call canonical []
        | None when SqlDatePartCapabilityRules.IsRepresentedPart(sourceName) ->
            if arguments.Length <> 1 then
                compilationError (sourceName + " requires exactly 1 argument.")
            canonicalCall call "CORE_DATE_PART" [ Literal(ScalarValue.Text sourceName); arguments.Head ]
        | None when SqlDateOnlyCapabilityRules.IsMySqlSourceFunction(sourceTool, sourceName) ->
            if arguments.Length <> 1 then
                compilationError "MySQL DATE(expr) requires exactly 1 argument."
            canonicalCall call "CORE_DATE_ONLY" arguments

        | None when not (isNull sourceContract) ->
            match (requireSourceContract ()).CanonicalizationKind with
            | SqlSourceFunctionCanonicalizationKind.DateAdd ->
                if arguments.Length <> 3 then compilationError "DATEADD requires exactly 3 arguments."
                let unit =
                    keywordValue "DATEADD date-part unit" arguments[0]
                    |> fun value -> SqlDateMathCapabilityRules.NormalizeUnit(value, "DATEADD")
                canonicalCall call "CORE_DATE_ADD" [ Literal(ScalarValue.Text unit); arguments[1]; arguments[2] ]

            | SqlSourceFunctionCanonicalizationKind.DateDiff ->
                let portableDay startValue endValue =
                    let canonical =
                        canonicalCall
                            call
                            "CORE_DATE_DIFF"
                            [ Literal(ScalarValue.Text "DAY")
                              dateOnlyOperand targetTool startValue
                              dateOnlyOperand targetTool endValue ]
                    if targetTool = SqlAgentToolType.Sqlite then
                        Cast(canonical, CastType.create "INTEGER")
                    else canonical
                match arguments with
                | [ finish; startValue ] ->
                    portableDay startValue finish
                | [ unitExpr; startValue; finish ] ->
                    let unit =
                        keywordValue "DATEDIFF date-part unit" unitExpr
                        |> fun value -> SqlDateMathCapabilityRules.NormalizeUnit(value, "DATEDIFF")
                    if (sourceTool = SqlAgentToolType.MsSqlServer || sourceTool = SqlAgentToolType.Firebird)
                       && sourceTool = targetTool then
                        canonicalCall call "CORE_DATE_DIFF" [ Literal(ScalarValue.Text unit); startValue; finish ]
                    elif unit <> "DAY" then
                        compilationError (
                            "Cross-dialect DATEDIFF unit '" + unit + "' from " + string sourceTool + " to " + string targetTool
                            + " is not translated: SQL capability 'core_date_diff.unit." + unit.ToLowerInvariant()
                            + "' is not modeled losslessly. DAY is the currently modeled portable intersection.")
                    else
                        portableDay startValue finish
                | values ->
                    compilationError (
                        "DATEDIFF requires either the portable 2-argument (end, start) shape or the "
                        + "3-argument (unit, start, end) shape; received " + string values.Length + " arguments.")

            | SqlSourceFunctionCanonicalizationKind.DateFormat ->
                if arguments.Length <> 2 then compilationError "DATE_FORMAT/FORMAT requires exactly 2 arguments."
                let rawFormat = textLiteral "DATE_FORMAT format" arguments[1]
                let translated =
                    try dateFormats.Translate(rawFormat, sourceTool, targetTool)
                    with
                    | :? FormatException as ex ->
                        raise (SqlCompilationException(
                            "portable date formatting from " + string sourceTool + " to " + string targetTool + " is not supported: " + ex.Message,
                            ex))
                    | :? NotSupportedException as ex ->
                        raise (SqlCompilationException(
                            "portable date formatting from " + string sourceTool + " to " + string targetTool + " is not supported: " + ex.Message,
                            ex))
                canonicalCall call "CORE_DATE_FORMAT" [ arguments[0]; Literal(ScalarValue.Text translated) ]

            | SqlSourceFunctionCanonicalizationKind.DateParse ->
                if arguments.Length <> 2 then compilationError "TO_DATE requires exactly 2 arguments."
                let rawFormat = textLiteral "TO_DATE format" arguments[1]
                let translated =
                    try dateFormats.Translate(rawFormat, sourceTool, targetTool)
                    with
                    | :? FormatException as ex ->
                        raise (SqlCompilationException(
                            "formatted date parsing from " + string sourceTool + " to " + string targetTool + " is not supported: " + ex.Message,
                            ex))
                    | :? NotSupportedException as ex ->
                        raise (SqlCompilationException(
                            "formatted date parsing from " + string sourceTool + " to " + string targetTool + " is not supported: " + ex.Message,
                            ex))
                canonicalCall call "CORE_DATE_PARSE" [ arguments[0]; Literal(ScalarValue.Text translated) ]

            | SqlSourceFunctionCanonicalizationKind.Position ->
                if arguments.Length <> 2 then compilationError (sourceName + " requires exactly 2 arguments.")
                let canonicalArguments =
                    if sourceName = "STRPOS" || sourceName = "INSTR" then
                        [ arguments[0]; arguments[1] ]
                    else
                        [ arguments[1]; arguments[0] ]
                canonicalCall call "CORE_POSITION" canonicalArguments

            | SqlSourceFunctionCanonicalizationKind.JsonExtract ->
                if arguments.Length <> 2 then compilationError "JSON_EXTRACT requires exactly 2 arguments."
                canonicalCall call "CORE_JSON_EXTRACT" arguments

            | SqlSourceFunctionCanonicalizationKind.JsonSet ->
                if arguments.Length <> 3 then compilationError "JSON_SET requires exactly 3 arguments."
                canonicalCall call "CORE_JSON_SET" arguments

            | SqlSourceFunctionCanonicalizationKind.RegexMatch ->
                if arguments.Length <> 2 then
                    compilationError ("Function 'CORE_REGEX_MATCH' requires 2 argument(s); received " + string arguments.Length + ".")
                RegexMatch(arguments[0], arguments[1])

            | SqlSourceFunctionCanonicalizationKind.CurrentTimestamp ->
                if not arguments.IsEmpty then compilationError (sourceName + " does not accept arguments.")
                canonicalCall call "CORE_CURRENT_TIMESTAMP" []

            | SqlSourceFunctionCanonicalizationKind.OracleSysdate ->
                if sourceTool <> SqlAgentToolType.Oracle then
                    compilationError "SYSDATE source semantics are modeled only for Oracle."
                if not arguments.IsEmpty then
                    compilationError "Oracle SYSDATE does not accept arguments."
                canonicalCall call "CORE_ORACLE_SYSDATE" []

            | SqlSourceFunctionCanonicalizationKind.StringAggregate ->
                if sourceName = "STRING_AGG" && arguments.Length <> 2 then
                    compilationError "STRING_AGG requires exactly 2 arguments."
                let normalizedArguments =
                    if sourceName = "GROUP_CONCAT" && sourceTool = SqlAgentToolType.MySQL then
                        if arguments.Length <> 1 then
                            compilationError (
                                "MySQL GROUP_CONCAT comma-separated arguments are multiple value expressions, not a separator. "
                                + "Core currently supports exactly one value expression; use portable STRING_AGG(value, separator) "
                                + "or native SEPARATOR 'literal' for an explicit delimiter.")
                        let separator = call.AggregateSeparator |> Option.defaultValue ","
                        [ arguments.Head; Literal(ScalarValue.Text separator) ]
                    elif arguments.Length = 1 then
                        let separator =
                            match sourceName with
                            | "LISTAGG" -> ""
                            | "GROUP_CONCAT"
                            | "LIST" -> ","
                            | _ -> compilationError ("String aggregate '" + sourceName + "' requires an explicit separator.")
                        [ arguments.Head; Literal(ScalarValue.Text separator) ]
                    else arguments
                canonicalCall call "CORE_STRING_AGG" normalizedArguments

            | value ->
                compilationError (
                    "Unsupported source function canonicalization kind '" + string value + "' for function '" + sourceName + "'.")

        | None when sourceName = "DATE" && sourceTool = SqlAgentToolType.MySQL ->
            if arguments.Length <> 1 then
                compilationError "MySQL DATE(expr) requires exactly 1 argument."
            if targetTool <> SqlAgentToolType.MySQL then
                compilationError (
                    "MySQL DATE(expr) is currently a native-only source capability. "
                    + "Cross-provider lowering remains fail-closed because MySQL DATE coercion and invalid-input semantics "
                    + "are not proven equivalent to target CAST/date functions. Target provider is "
                    + string targetTool + ".")
            FunctionCall { call with Name = FunctionName.create "DATE"; Arguments = arguments }

        | None when sourceName = "COALESCE" ->
            if arguments.Length < 2 then compilationError "COALESCE requires at least 2 arguments."
            FunctionCall { call with Name = FunctionName.create "COALESCE"; Arguments = arguments }

        | None when SqlCanonicalFunctionRegistry.IsDirectPortable(sourceRegistryName) ->
            let renderedName =
                if sourceTool = targetTool then sourceName
                else sourceRegistryName
            FunctionCall { call with Name = FunctionName.create renderedName; Arguments = arguments }

        | None ->
            let sourceDefinition =
                match functionRegistry.Find(sourceTool, sourceRegistryName, arguments.Length) |> Option.ofObj with
                | Some definition -> definition
                | None ->
                    compilationError (
                        "Function '" + sourceName + "' is not registered for source dialect "
                        + string sourceTool + "; normalization remains fail-closed.")
            if not sourceDefinition.Semantic.HasValue then
                compilationError ("Function '" + sourceName + "' has no portable semantic mapping from " + string sourceTool + ".")

            let semantic = sourceDefinition.Semantic.Value
            if sourceTool <> targetTool then
                match semantic with
                | SemanticFunction.Random ->
                    compilationError (
                        "Random function '" + sourceName + "' is not translated across dialects because providers differ in value range and evaluation frequency.")
                | SemanticFunction.StringLength when sourceTool = SqlAgentToolType.MsSqlServer ->
                    if arguments.Length <> 1 then compilationError "SQL Server LEN requires exactly 1 argument."
                    let targetLength =
                        match targetTool with
                        | SqlAgentToolType.Postgres
                        | SqlAgentToolType.Oracle
                        | SqlAgentToolType.Sqlite -> "LENGTH"
                        | SqlAgentToolType.MySQL
                        | SqlAgentToolType.Firebird -> "CHAR_LENGTH"
                        | value -> compilationError ("SQL Server LEN has no Core cross-dialect lowering for target provider " + string value + ".")
                    let trimmed = FunctionCall(emptyFunction "RTRIM" [ arguments.Head ])
                    FunctionCall { call with Name = FunctionName.create targetLength; Arguments = [ trimmed ] }
                | SemanticFunction.StringLength when targetTool = SqlAgentToolType.MsSqlServer ->
                    compilationError "Portable string length cannot be translated losslessly to SQL Server LEN because LEN excludes trailing spaces."
                | SemanticFunction.Repeat when sourceTool = SqlAgentToolType.MsSqlServer || targetTool = SqlAgentToolType.MsSqlServer ->
                    compilationError "REPLICATE/REPEAT is not translated across SQL Server because SQL Server REPLICATE can truncate non-MAX inputs."
                | SemanticFunction.Coalesce when sourceName <> "COALESCE" ->
                    compilationError (
                        "Provider-specific null function '" + sourceName
                        + "' is not translated across dialects because its type-conversion rules differ from COALESCE.")
                | _ ->
                    let targetDefinition =
                        match functionRegistry.Find(targetTool, semantic, arguments.Length) |> Option.ofObj with
                        | Some definition -> definition
                        | None ->
                            compilationError (
                                "Semantic function '" + string semantic + "' with " + string arguments.Length
                                + " argument(s) is not supported by " + string targetTool + ".")
                    if targetDefinition.TranslationKind = FunctionTranslationKind.Template
                       || targetDefinition.TranslationKind = FunctionTranslationKind.Specialized then
                        compilationError (
                            "Function '" + sourceName + "' requires Core " + string targetDefinition.TranslationKind
                            + " translation for target provider " + string targetTool
                            + "; no lossless Core translator is registered yet.")
                    FunctionCall { call with Name = FunctionName.create (targetDefinition.Name.Trim().ToUpperInvariant()); Arguments = arguments }
            else
                FunctionCall { call with Name = FunctionName.create sourceName; Arguments = arguments }

    and private normalizeWindow source target (window: WindowSpec) =
        { window with
            PartitionBy = window.PartitionBy |> List.map (normalizeExpr source target)
            OrderBy = window.OrderBy |> List.map (normalizeOrderBy source target) }

    and private normalizeOrderBy source target (orderBy: OrderBy) =
        { orderBy with Expression = normalizeExpr source target orderBy.Expression }

    and private normalizeSource sourceDialect target (source: TableSource) =
        match source with
        | NamedTable _ | CteTable _ -> source
        | DerivedTable(query, alias) -> DerivedTable(normalizeQuery sourceDialect target query, alias)
        | LateralDerivedTable(query, alias) ->
            LateralDerivedTable(normalizeQuery sourceDialect target query, alias)

    and private normalizeJoin sourceDialect target (join: Join) =
        match join with
        | CrossJoin source -> CrossJoin(normalizeSource sourceDialect target source)
        | NaturalJoin(kind, source) -> NaturalJoin(kind, normalizeSource sourceDialect target source)
        | OnJoin(kind, source, predicate) ->
            OnJoin(
                kind,
                normalizeSource sourceDialect target source,
                normalizeExpr sourceDialect target predicate)
        | UsingJoin(kind, source, columns) ->
            UsingJoin(
                kind,
                normalizeSource sourceDialect target source,
                columns)

    and private normalizeCte sourceDialect target (cte: Cte) =
        let query = normalizeQuery sourceDialect target cte.Query
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

    and private normalizeSelect sourceDialect target (select: Select) =
        { select with
            Ctes = select.Ctes |> List.map (normalizeCte sourceDialect target)
            DistinctMode = select.DistinctMode |> mapDistinctOn (normalizeExpr sourceDialect target)
            ProjectionItems =
                select.ProjectionItems
                |> NonEmpty.map (fun (item: SelectItem) ->
                    { item with Expression = normalizeExpr sourceDialect target item.Expression })
            From = select.From |> Option.map (normalizeSource sourceDialect target)
            Joins = select.Joins |> List.map (normalizeJoin sourceDialect target)
            Where = select.Where |> Option.map (normalizeExpr sourceDialect target)
            GroupBy = select.GroupBy |> List.map (normalizeExpr sourceDialect target)
            Having = select.Having |> Option.map (normalizeExpr sourceDialect target) }

    and private normalizeQuery sourceDialect target (query: Query) =
        { query with
            Head = normalizeSelect sourceDialect target query.Head
            SetOperations =
                query.SetOperations
                |> List.map (fun (branch: SetBranch) ->
                    { branch with Query = normalizeQuery sourceDialect target branch.Query })
            OrderBy = query.OrderBy |> List.map (normalizeOrderBy sourceDialect target) }

    let private normalizeReturning source target (items: ReturningItem list) =
        items
        |> List.map (function
            | ReturningColumn(identifier, alias) ->
                ReturningColumn(identifier, alias)
            | ReturningWildcard alias ->
                ReturningWildcard alias
            | ReturningExpression(expression, alias) ->
                ReturningExpression(normalizeExpr source target expression, alias))

    let private normalizeDocument source target document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(normalizeQuery source target query)
            | InsertStatement insert ->
                let input =
                    match insert.Input with
                    | Values rows -> Values(rows |> NonEmpty.map (NonEmpty.map (normalizeExpr source target)))
                    | QuerySource query -> QuerySource(normalizeQuery source target query)
                    | DefaultValues -> DefaultValues
                InsertStatement { insert with Input = input; Returning = normalizeReturning source target insert.Returning }
            | UpdateStatement update ->
                UpdateStatement
                    { update with
                        AssignmentItems =
                            update.AssignmentItems
                            |> NonEmpty.map (fun assignment ->
                                { assignment with Value = normalizeExpr source target assignment.Value })
                        From = update.From |> List.map (normalizeSource source target)
                        Where = update.Where |> Option.map (normalizeExpr source target)
                        Returning = normalizeReturning source target update.Returning }
            | DeleteStatement delete ->
                DeleteStatement
                    { delete with
                        Using = delete.Using |> List.map (normalizeSource source target)
                        Where = delete.Where |> Option.map (normalizeExpr source target)
                        Returning = normalizeReturning source target delete.Returning }
        { document with Statement = statement }

    let normalize enforceDialectSyntax sourceDialect targetRuntime sourceRegexProof sourceOrdering mySqlPipes sourceProfile targetProfile bound =
        Transition.normalize
            (fun document ->
                withCompilationDiagnostic
                    "SQL_SOURCE_VALIDATION_REJECTED"
                    SqlDiagnosticStage.SourceValidation
                    SqlDiagnosticCategory.Capability
                    document.Span
                    (fun () -> verifySourceRegexDocument sourceRegexProof document)

                let source = sourceProvider sourceDialect
                let target = targetProvider targetRuntime

                withCompilationDiagnostic
                    "SQL_SEMANTIC_VALIDATION_FAILED"
                    SqlDiagnosticStage.SemanticValidation
                    SqlDiagnosticCategory.Semantic
                    document.Span
                    (fun () ->
                        validateAggregateDocument
                            enforceDialectSyntax
                            source
                            sourceProfile
                            target
                            targetProfile
                            document)

                if enforceDialectSyntax then
                    withCompilationDiagnostic
                        "SQL_SOURCE_VALIDATION_REJECTED"
                        SqlDiagnosticStage.SourceValidation
                        SqlDiagnosticCategory.Capability
                        document.Span
                        (fun () -> validateRawSourceDocument source sourceOrdering mySqlPipes document)

                withCompilationDiagnostic
                    "SQL_NORMALIZATION_REJECTED"
                    SqlDiagnosticStage.SemanticValidation
                    SqlDiagnosticCategory.Semantic
                    document.Span
                    (fun () -> normalizeDocument sourceDialect targetRuntime document))
            bound

    let private identifierText = Identifier.text

    let private identifierDiagnosticSpan identifier =
        let parts = Identifier.parts identifier
        match parts with
        | [] -> null
        | first :: _ ->
            let last = List.last parts
            if first.Span.Start < 0 || last.Span.Start < 0 then null
            else
                let finish = last.Span.Start + max 0 last.Span.Length
                SqlDiagnosticSpan(first.Span.Start, max 0 (finish - first.Span.Start))

    let private ensureTableAllowed allowedTables identifier =
        match allowedTables with
        | None | Some [] -> ()
        | Some allowed ->
            let table = identifierText identifier
            if not (allowed |> List.exists (fun value -> StringComparer.OrdinalIgnoreCase.Equals(value, table))) then
                let message = "SQL plan is not authorized to access table(s): " + table
                let diagnostic =
                    SqlDiagnostic(
                        "SQL_POLICY_TABLE_NOT_ALLOWED",
                        SqlDiagnosticStage.Policy,
                        SqlDiagnosticCategory.Policy,
                        message,
                        identifierDiagnosticSpan identifier)
                let error = UnauthorizedAccessException(message)
                error.Data[diagnosticDataKey] <- diagnostic
                raise error

    let private isWildcard = function Wildcard _ -> true | _ -> false

    let private ensureNoDistinctWildcard (call: FunctionCall) =
        if call.IsDistinct && call.Arguments |> List.exists isWildcard then
            invalidOp "COUNT(DISTINCT *) is not a valid Core aggregate shape."

    let rec private validateExpr allowedTables expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) -> validateExpr allowedTables operand
        | Binary(_, left, right) -> validateExpr allowedTables left; validateExpr allowedTables right
        | Like(value, pattern, _, _, _) ->
            validateExpr allowedTables value
            validateExpr allowedTables pattern
        | RawRegexCall _ ->
            invalidOp "Raw REGEXP_LIKE reached plan validation before canonicalization."
        | RegexMatch(value, pattern) ->
            validateExpr allowedTables value
            validateExpr allowedTables pattern
        | FunctionCall call ->
            ensureNoDistinctWildcard call
            call.Arguments |> List.iter (validateExpr allowedTables)
            call.AggregateOrderBy |> List.iter (fun order -> validateExpr allowedTables order.Expression)
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
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) -> validateQuery allowedTables query

    and private validateSelect allowedTables select =
        for cte in select.Ctes do validateQuery allowedTables cte.Query
        if select.From.IsNone && select.Joins.IsEmpty && select.Projection |> List.exists (fun item -> isWildcard item.Expression) then
            invalidOp "Column reference '*' requires a FROM source in the portable Core query model."
        select.From |> Option.iter (validateSource allowedTables)
        iterDistinctOn (validateExpr allowedTables) select
        select.ProjectionItems |> NonEmpty.iter (fun item -> validateExpr allowedTables item.Expression)
        select.Where |> Option.iter (validateExpr allowedTables)
        select.GroupBy |> List.iter (validateExpr allowedTables)
        select.Having |> Option.iter (validateExpr allowedTables)
        select.Joins
        |> List.iter (function
            | CrossJoin source
            | NaturalJoin(_, source)
            | UsingJoin(_, source, _) ->
                validateSource allowedTables source
            | OnJoin(_, source, predicate) ->
                validateSource allowedTables source
                validateExpr allowedTables predicate)

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
                | Column identifier
                | BoundColumn(identifier, ProjectionAlias)
                    when Identifier.parts identifier |> List.length = 1 ->
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
        | Column identifier
        | BoundColumn(identifier, _) ->
            invalidOp ("INSERT VALUES scalar expression cannot reference column '" + identifierText identifier + "' outside a scalar subquery; use INSERT ... SELECT when the inserted value depends on a source row.")
        | Wildcard _ | OrderOrdinal _ -> invalidOp "INSERT VALUES scalar expression cannot contain a wildcard or ORDER BY ordinal."
        | Unary(_, operand) -> validateInsertValueScope operand
        | Binary(_, left, right) -> validateInsertValueScope left; validateInsertValueScope right
        | Like(value, pattern, _, _, _) -> validateInsertValueScope value; validateInsertValueScope pattern
        | RawRegexCall _ -> invalidOp "Raw REGEXP_LIKE reached INSERT validation before canonicalization."
        | RegexMatch(value, pattern) -> validateInsertValueScope value; validateInsertValueScope pattern
        | FunctionCall call ->
            call.Arguments |> List.iter validateInsertValueScope
            call.AggregateOrderBy |> List.iter (fun order -> validateInsertValueScope order.Expression)
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
            if insert.Columns.IsEmpty && insert.Conflict.IsSome then
                invalidOp "INSERT conflict handling requires explicit target columns; implicit target-column order is not admitted for conflict proofs."
            let expectedImplicitWidth =
                if insert.Columns.IsEmpty then
                    rows |> NonEmpty.toList |> List.head |> NonEmpty.length |> Some
                else None
            rows
            |> NonEmpty.iter (fun row ->
                match expectedImplicitWidth with
                | Some expected when NonEmpty.length row <> expected ->
                    invalidOp "INSERT VALUES without an explicit column list requires every row to have the same width."
                | None when NonEmpty.length row <> insert.Columns.Length ->
                    invalidOp "INSERT VALUES row width does not match target column count."
                | _ -> ()
                row |> NonEmpty.iter validateInsertValueScope)
        | QuerySource query ->
            if insert.Columns.IsEmpty then
                ()
            else
                match projectionWidth query with
                | None -> invalidOp "INSERT ... SELECT requires a statically known source projection width; wildcard projections are rejected at the Core validation boundary."
                | Some width when width <> insert.Columns.Length ->
                    invalidOp ("INSERT ... SELECT projection width " + string width + " does not match target column count " + string insert.Columns.Length + ".")
                | _ -> ()

    let private validateReturning allowedTables (items: ReturningItem list) =
        items |> List.iter (fun (item: ReturningItem) -> validateExpr allowedTables item.Expression)

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

    let private proveTargetLiteral targetRuntime (proofs: ExpressionProofs) value =
        let requireProof proof =
            match proof with
            | ProvenCapability -> ()
            | RejectedCapability message -> raise (SqlCompilationException(message))
        match targetRuntime, value with
        | FirebirdRuntime, ScalarValue.Text text when text.Length > 8191 ->
            raise (SqlCompilationException(
                "Firebird string literal exceeds the safe UTF8 VARCHAR limit of 8191 characters."))
        | _, ScalarValue.OffsetDateTime _ ->
            requireProof proofs.OffsetTimestamp
        | _, ScalarValue.Time _ ->
            requireProof proofs.StandaloneTime
        | FirebirdRuntime, ScalarValue.Decimal value ->
            let shape = SqlFirebirdDecimalCapabilityRules.Shape(value)
            if shape.Precision > SqlFirebirdDecimalCapabilityRules.LegacyMaximumPrecision then
                match proofs.FirebirdExtendedDecimal with
                | ProvenCapability -> ()
                | RejectedCapability _ ->
                    raise (SqlCompilationException(
                        "SQL capability 'numeric.decimal_extended' requires an explicit Firebird target "
                        + "capability profile with ServerVersion 4.0 or newer for exact decimal precision "
                        + "above 18; this value requires "
                        + SqlFirebirdDecimalCapabilityRules.FirebirdCastType(value)
                        + "."))
        | _ -> ()

    let private proveSqlServerConcat targetRuntime =
        match targetRuntime with
        | SqlServerRuntime(Proven _) -> ()
        | SqlServerRuntime(Unproven message) -> invalidOp message
        | _ -> ()

    let private requireExpressionCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> invalidOp message

    let private requireFilterCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> raise (SqlCompilationException(message))

    let rec private proveFilterPredicate (proofs: FilterPredicateProofs) expression =
        match expression with
        | BoundColumn(_, OuterRowSource) ->
            requireFilterCapability proofs.OuterReference
        | Column _
        | BoundColumn(_, LocalRowSource)
        | BoundColumn(_, ProjectionAlias)
        | Wildcard _
        | OrderOrdinal _
        | Literal _
        | Interval _ -> ()
        | Unary(_, operand) ->
            proveFilterPredicate proofs operand
        | Binary(_, left, right) ->
            proveFilterPredicate proofs left
            proveFilterPredicate proofs right
        | Like(value, pattern, _, _, _) ->
            proveFilterPredicate proofs value
            proveFilterPredicate proofs pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (proveFilterPredicate proofs)
        | RegexMatch(value, pattern) ->
            proveFilterPredicate proofs value
            proveFilterPredicate proofs pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (proveFilterPredicate proofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveFilterPredicate proofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            proveFilterPredicate proofs value
            proveFilterPredicate proofs predicate
        | Windowed(value, window) ->
            requireFilterCapability proofs.WindowFunction
            proveFilterPredicate proofs value
            window.PartitionBy |> List.iter (proveFilterPredicate proofs)
            window.OrderBy |> List.iter (fun order -> proveFilterPredicate proofs order.Expression)
        | Cast(value, _)
        | Extract(_, value) ->
            proveFilterPredicate proofs value
        | SimpleCase(input, branches, fallback) ->
            proveFilterPredicate proofs input
            branches |> NonEmpty.iter (fun branch ->
                proveFilterPredicate proofs branch.Match
                proveFilterPredicate proofs branch.Result)
            fallback |> Option.iter (proveFilterPredicate proofs)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveFilterPredicate proofs branch.Condition
                proveFilterPredicate proofs branch.Result)
            fallback |> Option.iter (proveFilterPredicate proofs)
        | InList(value, items, _) ->
            proveFilterPredicate proofs value
            items |> NonEmpty.iter (proveFilterPredicate proofs)
        | InSubquery(value, _, _) ->
            proveFilterPredicate proofs value
            requireFilterCapability proofs.Subquery
        | Between(value, lower, upper, _) ->
            proveFilterPredicate proofs value
            proveFilterPredicate proofs lower
            proveFilterPredicate proofs upper
        | IsNull(value, _) ->
            proveFilterPredicate proofs value
        | ScalarSubquery _
        | Exists _ ->
            requireFilterCapability proofs.Subquery

    let rec private proveSourceFilterExpr (expressionProofs: ExpressionProofs) expression =
        match expression with
        | Column _
        | BoundColumn _
        | Wildcard _
        | OrderOrdinal _
        | Literal _ -> ()
        | Interval _ ->
            requireFilterCapability expressionProofs.IntervalLiteral
        | Unary(_, operand) ->
            proveSourceFilterExpr expressionProofs operand
        | Binary(_, left, right) ->
            proveSourceFilterExpr expressionProofs left
            proveSourceFilterExpr expressionProofs right
        | Like(value, pattern, _, _, caseInsensitive) ->
            if caseInsensitive then requireFilterCapability expressionProofs.ILike
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterExpr expressionProofs pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (proveSourceFilterExpr expressionProofs)
        | RegexMatch(value, pattern) ->
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterExpr expressionProofs pattern
        | FunctionCall call ->
            if (FunctionName.value call.Name).Contains(".", StringComparison.Ordinal) then
                requireFilterCapability expressionProofs.QualifiedFunction
            call.Arguments |> List.iter (proveSourceFilterExpr expressionProofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveSourceFilterExpr expressionProofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            requireFilterCapability expressionProofs.AggregateFilter
            proveFilterPredicate expressionProofs.FilterPredicate predicate
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterExpr expressionProofs predicate
        | Windowed(value, window) ->
            proveSourceFilterExpr expressionProofs value
            window.PartitionBy |> List.iter (proveSourceFilterExpr expressionProofs)
            window.OrderBy |> List.iter (fun order -> proveSourceFilterExpr expressionProofs order.Expression)
        | Cast(value, _)
        | Extract(_, value) ->
            proveSourceFilterExpr expressionProofs value
        | SimpleCase(input, branches, fallback) ->
            proveSourceFilterExpr expressionProofs input
            branches |> NonEmpty.iter (fun branch ->
                proveSourceFilterExpr expressionProofs branch.Match
                proveSourceFilterExpr expressionProofs branch.Result)
            fallback |> Option.iter (proveSourceFilterExpr expressionProofs)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                proveSourceFilterExpr expressionProofs branch.Condition
                proveSourceFilterExpr expressionProofs branch.Result)
            fallback |> Option.iter (proveSourceFilterExpr expressionProofs)
        | InList(value, items, _) ->
            proveSourceFilterExpr expressionProofs value
            items |> NonEmpty.iter (proveSourceFilterExpr expressionProofs)
        | InSubquery(value, query, _) ->
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterQuery expressionProofs query
        | Between(value, lower, upper, _) ->
            proveSourceFilterExpr expressionProofs value
            proveSourceFilterExpr expressionProofs lower
            proveSourceFilterExpr expressionProofs upper
        | IsNull(value, _) ->
            proveSourceFilterExpr expressionProofs value
        | ScalarSubquery query
        | Exists(query, _) ->
            proveSourceFilterQuery expressionProofs query

    and private proveSourceFilterSource expressionProofs source =
        match source with
        | NamedTable _
        | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            proveSourceFilterQuery expressionProofs query

    and private proveSourceFilterSelect expressionProofs select =
        select.Ctes |> List.iter (fun cte -> proveSourceFilterQuery expressionProofs cte.Query)
        iterDistinctOn (proveSourceFilterExpr expressionProofs) select
        select.Projection |> List.iter (fun item -> proveSourceFilterExpr expressionProofs item.Expression)
        select.From |> Option.iter (proveSourceFilterSource expressionProofs)
        select.Joins |> List.iter (fun join ->
            proveSourceFilterSource expressionProofs join.Source
            join.Predicate |> Option.iter (proveSourceFilterExpr expressionProofs))
        select.Where |> Option.iter (proveSourceFilterExpr expressionProofs)
        select.GroupBy |> List.iter (proveSourceFilterExpr expressionProofs)
        select.Having |> Option.iter (proveSourceFilterExpr expressionProofs)

    and private proveSourceFilterQuery expressionProofs query =
        proveSourceFilterSelect expressionProofs query.Head
        query.SetOperations |> List.iter (fun branch -> proveSourceFilterQuery expressionProofs branch.Query)
        query.OrderBy |> List.iter (fun order -> proveSourceFilterExpr expressionProofs order.Expression)

    let private proveSourceFilterDocument expressionProofs document =
        match document.Statement with
        | QueryStatement query ->
            proveSourceFilterQuery expressionProofs query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (proveSourceFilterExpr expressionProofs))
            | QuerySource query -> proveSourceFilterQuery expressionProofs query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> proveSourceFilterExpr expressionProofs item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> proveSourceFilterExpr expressionProofs assignment.Value)
            update.From |> List.iter (proveSourceFilterSource expressionProofs)
            update.Where |> Option.iter (proveSourceFilterExpr expressionProofs)
            update.Returning |> List.iter (fun item -> proveSourceFilterExpr expressionProofs item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (proveSourceFilterSource expressionProofs)
            delete.Where |> Option.iter (proveSourceFilterExpr expressionProofs)
            delete.Returning |> List.iter (fun item -> proveSourceFilterExpr expressionProofs item.Expression)

    let rec private proveTargetExpr targetRuntime (expressionProofs: ExpressionProofs) expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ -> ()
        | Literal value -> proveTargetLiteral targetRuntime expressionProofs value
        | Interval _ -> requireExpressionCapability expressionProofs.IntervalLiteral
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
        | RawRegexCall _ ->
            invalidOp "Raw REGEXP_LIKE reached target validation before canonicalization."
        | RegexMatch(value, pattern) ->
            requireExpressionCapability expressionProofs.RegexMatch
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs pattern
        | FunctionCall call ->
            if (FunctionName.value call.Name).Contains(".", StringComparison.Ordinal) then
                requireExpressionCapability expressionProofs.QualifiedFunction
            call.Arguments |> List.iter (proveTargetExpr targetRuntime expressionProofs)
            call.AggregateOrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)
        | FilteredAggregate(value, predicate) ->
            proveTargetExpr targetRuntime expressionProofs value
            proveTargetExpr targetRuntime expressionProofs predicate
        | Windowed(value, window) ->
            proveTargetExpr targetRuntime expressionProofs value
            window.PartitionBy |> List.iter (proveTargetExpr targetRuntime expressionProofs)
            window.OrderBy |> List.iter (fun order -> proveTargetExpr targetRuntime expressionProofs order.Expression)
        | Cast(value, targetType) ->
            let targetTypeName = CastType.value targetType
            match targetRuntime with
            | FirebirdRuntime when targetTypeName.Contains(" WITH TIME ZONE", StringComparison.OrdinalIgnoreCase) ->
                requireExpressionCapability expressionProofs.FirebirdTimeZoneType
            | _ -> ()
            proveTargetExpr targetRuntime expressionProofs value
        | Extract(_, value) ->
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
        | LateralDerivedTable(query, _) ->
            match SqlLateralDerivedTableCapabilityRules.TargetValidationError(
                      targetProvider targetRuntime,
                      null) with
            | null -> proveTargetQuery targetRuntime expressionProofs query
            | message -> raise (SqlCompilationException(message))

    and private proveTargetSelect targetRuntime expressionProofs select =
        if select.Ctes |> List.exists (fun cte -> cte.RecursiveScope) then
            let provider = targetProvider targetRuntime
            if not (SqlRecursiveCteCapabilityRules.SupportsWithRecursiveSyntax(provider)) then
                raise (SqlCompilationException(
                    "SQL capability 'select.recursive_cte' is not supported by target provider "
                    + string provider + "; this provider does not use the modeled WITH RECURSIVE syntax contract."))
        select.Ctes |> List.iter (fun cte -> proveTargetQuery targetRuntime expressionProofs cte.Query)
        match select.DistinctMode with
        | SelectDistinct.DistinctOn expressions ->
            match SqlDistinctOnCapabilityRules.TargetValidationError(targetProvider targetRuntime) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            expressions |> NonEmpty.iter (proveTargetExpr targetRuntime expressionProofs)
        | SelectDistinct.AllRows
        | SelectDistinct.DistinctRows -> ()
        select.ProjectionItems |> NonEmpty.iter (fun item -> proveTargetExpr targetRuntime expressionProofs item.Expression)
        select.From |> Option.iter (proveTargetSource targetRuntime expressionProofs)
        select.Joins
        |> List.iter (function
            | CrossJoin source ->
                proveTargetSource targetRuntime expressionProofs source
            | NaturalJoin(_, source) ->
                match SqlNaturalJoinCapabilityRules.TargetValidationError(targetProvider targetRuntime) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
                proveTargetSource targetRuntime expressionProofs source
            | OnJoin(_, source, predicate) ->
                proveTargetSource targetRuntime expressionProofs source
                proveTargetExpr targetRuntime expressionProofs predicate
            | UsingJoin(_, source, _) ->
                match SqlUsingJoinCapabilityRules.TargetValidationError(targetProvider targetRuntime) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
                proveTargetSource targetRuntime expressionProofs source)
        select.Where |> Option.iter (proveTargetExpr targetRuntime expressionProofs)
        select.GroupBy |> List.iter (proveTargetExpr targetRuntime expressionProofs)
        select.Having |> Option.iter (proveTargetExpr targetRuntime expressionProofs)

    and private proveTargetQuery targetRuntime expressionProofs query =
        if query.FetchPercent.IsSome then
            match SqlFetchPercentCapabilityRules.TargetValidationError(
                      targetProvider targetRuntime,
                      null) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
        if query.FetchWithTies then
            match SqlFetchWithTiesCapabilityRules.TargetValidationError(
                      targetProvider targetRuntime,
                      null) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
        proveTargetSelect targetRuntime expressionProofs query.Head
        query.SetOperations
        |> List.iter (fun branch ->
            match branch.Operator with
            | SetOperator.IntersectAll ->
                match SqlSetAllCapabilityRules.TargetValidationError(
                          "INTERSECT",
                          targetProvider targetRuntime) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SetOperator.ExceptAll ->
                match SqlSetAllCapabilityRules.TargetValidationError(
                          "EXCEPT",
                          targetProvider targetRuntime) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SetOperator.Union
            | SetOperator.UnionAll
            | SetOperator.Intersect
            | SetOperator.Except -> ()
            proveTargetQuery targetRuntime expressionProofs branch.Query)
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
        | RejectedCapability message -> raise (SqlCompilationException(message))

    let private proveJoinKind (proofs: JoinProofs) = function
        | JoinKind.Right -> requireCapability proofs.RightJoin
        | JoinKind.Full -> requireCapability proofs.FullJoin
        | JoinKind.Inner | JoinKind.Left | JoinKind.Cross -> ()

    let rec private proveTargetJoinSource proofs source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) -> proveTargetJoinQuery proofs query

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

    let private requireDmlCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> raise (SqlCompilationException(message))

    let private returningNodeName = function
        | Column _ -> "ColumnExpr"
        | BoundColumn _ -> "BoundColumnExpr"
        | Wildcard _ -> "WildcardExpr"
        | OrderOrdinal _ -> "OrderByOrdinalExpr"
        | Literal _ -> "LiteralExpr"
        | Interval _ -> "IntervalExpr"
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
        | BoundColumn(_, binding) ->
            validateBoundColumn binding
        | Column _ ->
            returningExpressionError "requires every column reference to bind to a local DML row source"
        | Literal _ -> ()
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
                 | BinaryOperator.LessThanOrEqual), left, right) ->
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

    let private proveReturning (proofs: DmlProofs) (items: ReturningItem list) =
        if not (List.isEmpty items) then
            requireDmlCapability proofs.Returning
            let rich =
                items
                |> List.choose (function
                    | ReturningExpression(expression, _) -> Some expression
                    | ReturningColumn _ | ReturningWildcard _ -> None)
            if not rich.IsEmpty then
                requireDmlCapability proofs.ReturningExpression
                rich |> List.iter validateRichReturningExpression

    let private proveTargetDml (proofs: DmlProofs) document =
        match document.Statement with
        | QueryStatement _ -> ()
        | InsertStatement insert ->
            proveReturning proofs insert.Returning
        | UpdateStatement update ->
            if update.TargetAlias.IsSome then requireDmlCapability proofs.TargetAlias
            if not update.From.IsEmpty then requireDmlCapability proofs.UpdateFrom
            proveReturning proofs update.Returning
        | DeleteStatement delete ->
            if delete.TargetAlias.IsSome then requireDmlCapability proofs.TargetAlias
            if not delete.Using.IsEmpty then requireDmlCapability proofs.DeleteUsing
            proveReturning proofs delete.Returning

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
            match order.Expression with
            | BoundColumn(_, LocalRowSource)
            | BoundColumn(_, OuterRowSource) -> ()
            | Column _
            | BoundColumn(_, ProjectionAlias)
            | _ -> raise (nullOrderingCapabilityError targetRuntime)

    let rec private proveOrderingExpr targetRuntime targetOrdering expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
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
            match item.Alias, item.Expression with
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
        match order.Expression with
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
            match order.Expression with
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

    let private proveOrderingAndPaging targetRuntime targetOrdering document =
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
            let target = targetProvider targetRuntime
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
        | RejectedCapability message -> raise (SqlCompilationException(message))

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

    let private proveConflicts targetRuntime (proofs: ConflictProofs) document =
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

    type private QueryPosition =
        | RootQuery
        | InsertSelectSource
        | CteDefinition
        | DerivedTablePosition
        | SetBranchPosition
        | ScalarSubqueryPosition

    let private cteScopeError detail =
        raise (SqlCompilationException(
            "SQL capability 'select.cte_scope' is not supported by the native SQL backend: " + detail + "."))

    let private nestedCteSupported targetRuntime =
        SqlNestedCteCapabilityRules.SupportsTarget(targetProvider targetRuntime)

    let private validateCtePlacement targetRuntime position (ctes: Cte list) =
        if not ctes.IsEmpty && not (nestedCteSupported targetRuntime) then
            match position with
            | RootQuery | InsertSelectSource -> ()
            | CteDefinition ->
                cteScopeError (
                    "provider " + string (targetProvider targetRuntime)
                    + " has no declared portable nested-WITH-inside-a-CTE-definition contract")
            | DerivedTablePosition ->
                cteScopeError (
                    "provider " + string (targetProvider targetRuntime)
                    + " has no declared portable WITH-in-derived-table lowering contract")
            | SetBranchPosition ->
                cteScopeError (
                    "provider " + string (targetProvider targetRuntime)
                    + " has no declared portable WITH-in-set-operation-branch lowering contract")
            | ScalarSubqueryPosition ->
                cteScopeError (
                    "provider " + string (targetProvider targetRuntime)
                    + " has no declared portable WITH-at-the-root-of-a-scalar/EXISTS-subquery contract")

    let rec private validateNestedCteExpr targetRuntime expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) -> validateNestedCteExpr targetRuntime operand
        | Binary(_, left, right) ->
            validateNestedCteExpr targetRuntime left
            validateNestedCteExpr targetRuntime right
        | Like(value, pattern, _, _, _) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteExpr targetRuntime pattern
        | RawRegexCall(arguments, _) -> arguments |> List.iter (validateNestedCteExpr targetRuntime)
        | RegexMatch(value, pattern) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteExpr targetRuntime pattern
        | FunctionCall call ->
            call.Arguments |> List.iter (validateNestedCteExpr targetRuntime)
            call.AggregateOrderBy |> List.iter (fun order -> validateNestedCteExpr targetRuntime order.Expression)
        | FilteredAggregate(value, predicate) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteExpr targetRuntime predicate
        | Windowed(value, window) ->
            validateNestedCteExpr targetRuntime value
            window.PartitionBy |> List.iter (validateNestedCteExpr targetRuntime)
            window.OrderBy |> List.iter (fun order -> validateNestedCteExpr targetRuntime order.Expression)
        | Cast(value, _) | Extract(_, value) -> validateNestedCteExpr targetRuntime value
        | SimpleCase(input, branches, fallback) ->
            validateNestedCteExpr targetRuntime input
            branches |> NonEmpty.iter (fun branch ->
                validateNestedCteExpr targetRuntime branch.Match
                validateNestedCteExpr targetRuntime branch.Result)
            fallback |> Option.iter (validateNestedCteExpr targetRuntime)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                validateNestedCteExpr targetRuntime branch.Condition
                validateNestedCteExpr targetRuntime branch.Result)
            fallback |> Option.iter (validateNestedCteExpr targetRuntime)
        | InList(value, items, _) ->
            validateNestedCteExpr targetRuntime value
            items |> NonEmpty.iter (validateNestedCteExpr targetRuntime)
        | InSubquery(value, query, _) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteQuery targetRuntime ScalarSubqueryPosition query
        | Between(value, lower, upper, _) ->
            validateNestedCteExpr targetRuntime value
            validateNestedCteExpr targetRuntime lower
            validateNestedCteExpr targetRuntime upper
        | IsNull(value, _) -> validateNestedCteExpr targetRuntime value
        | ScalarSubquery query | Exists(query, _) ->
            validateNestedCteQuery targetRuntime ScalarSubqueryPosition query

    and private validateNestedCteTable targetRuntime source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            validateNestedCteQuery targetRuntime DerivedTablePosition query

    and private validateNestedCteSelect targetRuntime position select =
        validateCtePlacement targetRuntime position select.Ctes
        select.Ctes |> List.iter (fun cte ->
            validateNestedCteQuery targetRuntime CteDefinition cte.Query)
        select.From |> Option.iter (validateNestedCteTable targetRuntime)
        select.Joins |> List.iter (fun join ->
            validateNestedCteTable targetRuntime join.Source
            join.Predicate |> Option.iter (validateNestedCteExpr targetRuntime))
        iterDistinctOn (validateNestedCteExpr targetRuntime) select
        select.Projection |> List.iter (fun item -> validateNestedCteExpr targetRuntime item.Expression)
        select.Where |> Option.iter (validateNestedCteExpr targetRuntime)
        select.GroupBy |> List.iter (validateNestedCteExpr targetRuntime)
        select.Having |> Option.iter (validateNestedCteExpr targetRuntime)

    and private validateNestedCteQuery targetRuntime position query =
        validateNestedCteSelect targetRuntime position query.Head

        if position = ScalarSubqueryPosition
           && not query.Head.Ctes.IsEmpty
           && not query.SetOperations.IsEmpty
           && not query.OrderBy.IsEmpty then
            let portableSetTailOrder expression =
                match expression with
                | OrderOrdinal _ -> true
                | Column identifier
                | BoundColumn(identifier, ProjectionAlias) ->
                    Identifier.parts identifier |> List.length = 1
                | _ -> false
            if query.OrderBy
               |> List.exists (fun order -> not (portableSetTailOrder order.Expression)) then
                cteScopeError (
                    "scalar/EXISTS subquery with a root CTE and set-operation tail can order only by an output name "
                    + "or output ordinal; rich ordering expressions would require an unproven scope barrier")

        query.SetOperations |> List.iter (fun branch ->
            validateNestedCteQuery targetRuntime SetBranchPosition branch.Query)
        query.OrderBy |> List.iter (fun order -> validateNestedCteExpr targetRuntime order.Expression)

    let private validateNestedCteDocument targetRuntime document =
        match document.Statement with
        | QueryStatement query -> validateNestedCteQuery targetRuntime RootQuery query
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query -> validateNestedCteQuery targetRuntime InsertSelectSource query
            | Values rows -> rows |> NonEmpty.iter (NonEmpty.iter (validateNestedCteExpr targetRuntime))
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> validateNestedCteExpr targetRuntime item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems |> NonEmpty.iter (fun item -> validateNestedCteExpr targetRuntime item.Value)
            update.From |> List.iter (validateNestedCteTable targetRuntime)
            update.Where |> Option.iter (validateNestedCteExpr targetRuntime)
            update.Returning |> List.iter (fun item -> validateNestedCteExpr targetRuntime item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (validateNestedCteTable targetRuntime)
            delete.Where |> Option.iter (validateNestedCteExpr targetRuntime)
            delete.Returning |> List.iter (fun item -> validateNestedCteExpr targetRuntime item.Expression)

    type private ClauseContext =
        | ProjectionClause
        | PredicateClause
        | GroupByClause
        | HavingClause
        | OrderByClause
        | WindowSpecificationClause
        | AssignmentClause
        | InsertValueClause

    let private clauseName = function
        | ProjectionClause -> "SELECT"
        | PredicateClause -> "WHERE/ON/FILTER"
        | GroupByClause -> "GROUP BY"
        | HavingClause -> "HAVING"
        | OrderByClause -> "ORDER BY"
        | WindowSpecificationClause -> "window specification"
        | AssignmentClause -> "UPDATE SET"
        | InsertValueClause -> "UPDATE SET/INSERT VALUES"

    let rec private isDefinitelyBoolean targetRuntime expression =
        match expression with
        | Literal(ScalarValue.Boolean _) -> true
        | IsNull _ | InList _ | InSubquery _ | Between _ | Exists _ | Like _ -> true
        | RegexMatch _ ->
            SqlRegexCapabilityRules.SupportsTarget(targetProvider targetRuntime, null)
        | Unary(UnaryOperator.Not, _) -> true
        | Binary(operator, _, _) ->
            match operator with
            | BinaryOperator.Equal | BinaryOperator.NotEqual
            | BinaryOperator.GreaterThan | BinaryOperator.LessThan
            | BinaryOperator.GreaterThanOrEqual | BinaryOperator.LessThanOrEqual
            | BinaryOperator.And | BinaryOperator.Or -> true
            | _ -> false
        | SimpleCase(_, branches, fallback) ->
            let values =
                (branches |> NonEmpty.toList |> List.map (fun branch -> branch.Result))
                @ (fallback |> Option.toList)
            let nonNull = values |> List.filter (function Literal ScalarValue.Null -> false | _ -> true)
            not nonNull.IsEmpty && nonNull |> List.forall (isDefinitelyBoolean targetRuntime)
        | SearchedCase(branches, fallback) ->
            let values =
                (branches |> NonEmpty.toList |> List.map (fun branch -> branch.Result))
                @ (fallback |> Option.toList)
            let nonNull = values |> List.filter (function Literal ScalarValue.Null -> false | _ -> true)
            not nonNull.IsEmpty && nonNull |> List.forall (isDefinitelyBoolean targetRuntime)
        | _ -> false

    let private validateBooleanScalar targetRuntime capability expression =
        if isDefinitelyBoolean targetRuntime expression then
            match SqlScalarBooleanCapabilityRules.TargetValidationError(targetProvider targetRuntime, capability) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))

    let private canonicalFunctionKind (call: FunctionCall) =
        let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
        name, SqlCanonicalFunctionRegistry.IsAggregate(name), SqlCanonicalFunctionRegistry.IsWindow(name)

    let private targetCapabilityError provider capability =
        SqlCompilationException(
            "SQL capability '" + capability + "' is not supported by provider "
            + string provider + " for this Core plan.")

    let private integerLiteral = function
        | Literal(ScalarValue.Integer value) -> Some value
        | Literal(ScalarValue.Decimal value)
            when value = Decimal.Truncate(value)
                 && value >= decimal Int64.MinValue
                 && value <= decimal Int64.MaxValue ->
            Some(int64 value)
        | Literal(ScalarValue.Floating value)
            when Double.IsFinite(value)
                 && value = Math.Truncate(value)
                 && value >= float Int64.MinValue
                 && value <= float Int64.MaxValue ->
            Some(int64 value)
        | _ -> None

    let private validateJsonPath provider arguments =
        let path =
            match arguments |> List.tryItem 1 with
            | Some(Literal(ScalarValue.Text value)) -> value
            | _ -> raise (targetCapabilityError provider "json.path.constant")
        if not (System.Text.RegularExpressions.Regex.IsMatch(
                    path,
                    "^\\$\\.[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)) then
            raise (SqlCompilationException(
                "JSON path '" + path + "' is outside the portable Core property-chain subset. "
                + "SQL capability 'json.path.property_chain' is not supported by provider "
                + string provider + " for this Core plan."))

    let private validateCanonicalFunction targetRuntime withinWindow (call: FunctionCall) =
        let provider = targetProvider targetRuntime
        let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
        match SqlCanonicalFunctionRegistry.Find(name) |> Option.ofObj with
        | None ->
            if call.IsDistinct then
                raise (SqlCompilationException(
                    "Function '" + name + "' has no Core DISTINCT capability declaration."))
        | Some contract ->
            if not (contract.AcceptsArgumentCount(call.Arguments.Length)) then
                let expected =
                    if contract.MinArguments = contract.MaxArguments then string contract.MinArguments
                    else string contract.MinArguments + "-" + string contract.MaxArguments
                raise (SqlCompilationException(
                    "Function '" + name + "' requires " + expected + " argument(s); received "
                    + string call.Arguments.Length + "."))
            if call.IsDistinct && not contract.AllowDistinct then
                raise (SqlCompilationException(
                    "Function '" + name + "' does not support DISTINCT in the Core pipeline."))
            if contract.RequireWindow && not withinWindow then
                raise (SqlCompilationException("Function '" + name + "' requires an OVER clause."))

            contract.PlanShapeRules
            |> Seq.iter (fun rule ->
                if rule.ArgumentIndex < 0 || call.Arguments.Length <= rule.ArgumentIndex then
                    raise (SqlCompilationException(
                        "Canonical function '" + contract.Name
                        + "' declares an invalid plan-shape argument index "
                        + string rule.ArgumentIndex + "."))
                let argument = call.Arguments |> List.item rule.ArgumentIndex
                match rule.Kind with
                | SqlCanonicalPlanShapeValidationKind.DistinctWildcardForbidden ->
                    if call.IsDistinct then
                        match argument with
                        | Wildcard None ->
                            let message =
                                rule.ValidationMessage
                                |> Option.ofObj
                                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                |> Option.defaultValue (
                                    "Canonical function '" + contract.Name
                                    + "' does not allow DISTINCT wildcard arguments.")
                            raise (SqlCompilationException(message))
                        | _ -> ()
                | SqlCanonicalPlanShapeValidationKind.LiteralStringRequired ->
                    match argument with
                    | Literal(ScalarValue.Text _) -> ()
                    | _ ->
                        match rule.CapabilityId |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not) with
                        | Some capability -> raise (targetCapabilityError provider capability)
                        | None ->
                            let message =
                                rule.ValidationMessage
                                |> Option.ofObj
                                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                |> Option.defaultValue (
                                    "Canonical function '" + contract.Name
                                    + "' requires a literal string argument at position "
                                    + string (rule.ArgumentIndex + 1) + ".")
                            raise (SqlCompilationException(message))
                | value ->
                    raise (SqlCompilationException(
                        "Unsupported canonical plan-shape rule '" + string value
                        + "' for function '" + contract.Name + "'.")))

            match contract.TargetCapabilityFamily with
            | SqlCanonicalTargetCapabilityFamily.None -> ()
            | SqlCanonicalTargetCapabilityFamily.WindowFunction ->
                match SqlWindowCapabilityRules.FunctionValidationError(contract.Name, provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SqlCanonicalTargetCapabilityFamily.TemporalFormat ->
                match SqlTemporalFormatCapabilityRules.TargetValidationError(contract.Name, provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SqlCanonicalTargetCapabilityFamily.Json ->
                match SqlJsonCapabilityRules.TargetValidationError(contract.Name, provider) with
                | null -> validateJsonPath provider call.Arguments
                | message -> raise (SqlCompilationException(message))
            | SqlCanonicalTargetCapabilityFamily.Regex ->
                match SqlRegexCapabilityRules.ProviderValidationError(provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SqlCanonicalTargetCapabilityFamily.DatePart ->
                match call.Arguments |> List.tryHead with
                | Some(Literal(ScalarValue.Text rawPart)) ->
                    match SqlDatePartCapabilityRules.TargetValidationError(rawPart, provider) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                | _ -> raise (SqlCompilationException(
                            "Canonical function 'CORE_DATE_PART' requires a literal date-part unit."))
            | SqlCanonicalTargetCapabilityFamily.DateMath ->
                match call.Arguments |> List.tryHead with
                | Some(Literal(ScalarValue.Text rawUnit)) ->
                    match SqlDateMathCapabilityRules.TargetValidationError(rawUnit, provider, contract.Name) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                | _ -> raise (SqlCompilationException(
                            "Canonical function '" + contract.Name
                            + "' requires a literal date-part unit."))
            | SqlCanonicalTargetCapabilityFamily.DateOnly ->
                match SqlDateOnlyCapabilityRules.TargetValidationError(provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SqlCanonicalTargetCapabilityFamily.CurrentTemporal ->
                if not contract.CurrentTemporalKind.HasValue then
                    raise (SqlCompilationException(
                        "Canonical function '" + contract.Name
                        + "' declares the current-temporal target family without a temporal kind."))
                match SqlCurrentTemporalCapabilityRules.TargetValidationError(
                          contract.CurrentTemporalKind.Value,
                          provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
            | SqlCanonicalTargetCapabilityFamily.OracleSysdate ->
                if provider <> SqlAgentToolType.Oracle then
                    raise (SqlCompilationException(
                        "SQL capability 'function.oracle_sysdate' is native-only because Oracle SYSDATE uses server-clock DATE semantics that are not equivalent to CURRENT_TIMESTAMP on provider "
                        + string provider + "."))
            | value ->
                raise (SqlCompilationException(
                    "Unsupported canonical target capability family '" + string value
                    + "' for function '" + contract.Name + "'."))

            contract.LiteralArgumentRules
            |> Seq.iter (fun rule ->
                if rule.ArgumentIndex < 0 then
                    raise (SqlCompilationException(
                        "Canonical function '" + contract.Name
                        + "' declares an invalid literal argument index "
                        + string rule.ArgumentIndex + "."))
                match call.Arguments |> List.tryItem rule.ArgumentIndex |> Option.bind integerLiteral with
                | None -> ()
                | Some value ->
                    match rule.Kind with
                    | SqlCanonicalLiteralArgumentValidationKind.PositiveInteger ->
                        if value <= 0L then
                            let message =
                                rule.ValidationMessage
                                |> Option.ofObj
                                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                                |> Option.defaultValue (
                                    "Canonical function '" + contract.Name
                                    + "' requires a positive integer argument.")
                            raise (SqlCompilationException(message))
                    | SqlCanonicalLiteralArgumentValidationKind.WindowOffset ->
                        match SqlWindowCapabilityRules.LiteralOffsetValidationError(
                                  contract.Name, value, provider) with
                        | null -> ()
                        | message -> raise (SqlCompilationException(message))
                    | value ->
                        raise (SqlCompilationException(
                            "Unsupported canonical literal argument rule '" + string value
                            + "' for function '" + contract.Name + "'.")))

    let private windowBoundPosition = function
        | WindowFrameBound.UnboundedPreceding -> Int64.MinValue
        | WindowFrameBound.Preceding offset -> -(int64 (FrameOffset.value offset))
        | WindowFrameBound.CurrentRow -> 0L
        | WindowFrameBound.Following offset -> int64 (FrameOffset.value offset)
        | WindowFrameBound.UnboundedFollowing -> Int64.MaxValue

    let private hasOffsetBound = function
        | WindowFrameBound.Preceding _
        | WindowFrameBound.Following _ -> true
        | _ -> false

    let private validateWindowFrameShape (frame: WindowFrame) =
        match frame.Extent with
        | WindowFrameExtent.SingleBound WindowFrameBound.UnboundedFollowing ->
            raise (SqlCompilationException("Window frame cannot start with UNBOUNDED FOLLOWING."))
        | WindowFrameExtent.SingleBound _ -> ()
        | WindowFrameExtent.BetweenBounds(start, finish) ->
            if start = WindowFrameBound.UnboundedFollowing then
                raise (SqlCompilationException("Window frame cannot start with UNBOUNDED FOLLOWING."))
            if finish = WindowFrameBound.UnboundedPreceding then
                raise (SqlCompilationException("Window frame cannot end with UNBOUNDED PRECEDING."))
            if windowBoundPosition start > windowBoundPosition finish then
                raise (SqlCompilationException(
                    "Window frame start must not be logically after its end bound."))

    let private directWindowFunction = function
        | FunctionCall call -> Some call
        | FilteredAggregate(FunctionCall call, _) -> Some call
        | _ -> None

    let private validateFilterTarget = function
        | FunctionCall call ->
            let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
            match SqlCanonicalFunctionRegistry.Find(name) |> Option.ofObj with
            | Some contract when contract.AllowFilter -> ()
            | _ ->
                raise (SqlCompilationException(
                    "Function '" + name + "' does not support FILTER in the Core pipeline."))
        | _ ->
            raise (SqlCompilationException(
                "FILTER must modify a directly modeled aggregate function."))

    let private validateWindowTarget targetRuntime value (window: WindowSpec) =
        let provider = targetProvider targetRuntime
        match directWindowFunction value with
        | None -> raise (SqlCompilationException(
                    "OVER must modify a directly modeled aggregate or window function."))
        | Some call ->
            let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
            let contract =
                match SqlCanonicalFunctionRegistry.Find(name) |> Option.ofObj with
                | Some contract when contract.AllowWindow -> contract
                | _ ->
                    raise (SqlCompilationException(
                        "Function '" + name + "' does not support OVER in the Core pipeline."))
            if call.IsDistinct then
                raise (SqlCompilationException(
                    "DISTINCT window aggregate '" + name
                    + "' is not a portable Core capability and is rejected before lowering."))
            match window.Frame with
            | Some frame ->
                validateWindowFrameShape frame
                if contract.IsWindowFrameInsensitive
                   && (provider = SqlAgentToolType.MsSqlServer
                       || provider = SqlAgentToolType.Oracle) then
                    raise (targetCapabilityError provider (
                        "window.frame." + name.ToLowerInvariant()))
                if provider = SqlAgentToolType.MsSqlServer
                   && frame.Unit = WindowFrameUnit.Range then
                    match frame.Extent with
                    | WindowFrameExtent.SingleBound bound when hasOffsetBound bound ->
                        raise (targetCapabilityError provider "window.range_offset")
                    | WindowFrameExtent.BetweenBounds(start, finish)
                        when hasOffsetBound start || hasOffsetBound finish ->
                        raise (targetCapabilityError provider "window.range_offset")
                    | _ -> ()
            | None -> ()
            if provider = SqlAgentToolType.MsSqlServer
               && contract.Kind = SqlCanonicalFunctionKind.Window
               && window.OrderBy.IsEmpty then
                raise (targetCapabilityError provider "window.order_by")

    let private validateScalarSubqueryShape (query: Query) =
        let projection = query.Head.Projection
        if projection
           |> List.exists (fun item ->
               match item.Expression with
               | Wildcard _ -> true
               | _ -> false) then
            raise (SqlCompilationException(
                "Scalar subquery projection width must be statically known and cannot contain a wildcard."))
        if projection.Length <> 1 then
            raise (SqlCompilationException(
                "Scalar subquery must project exactly one expression; projected "
                + string projection.Length + "."))

    let rec private validateSemanticExpr targetRuntime context insideSetFunction withinWindow expression =
        match context with
        | ProjectionClause -> validateBooleanScalar targetRuntime "expression.boolean_select" expression
        | AssignmentClause -> validateBooleanScalar targetRuntime "dml.update.boolean_assignment" expression
        | InsertValueClause -> validateBooleanScalar targetRuntime "dml.insert.boolean_value" expression
        | _ -> ()

        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow operand
        | Binary(_, left, right) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow left
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow right
        | Like(value, pattern, _, _, _) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow value
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (validateSemanticExpr targetRuntime context insideSetFunction withinWindow)
        | RegexMatch(value, pattern) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow value
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow pattern
        | FunctionCall call ->
            validateCanonicalFunction targetRuntime withinWindow call
            let name, isAggregate, isWindowFunction = canonicalFunctionKind call
            if isAggregate then
                let allowed =
                    if withinWindow then
                        context = ProjectionClause || context = OrderByClause
                    else
                        context = ProjectionClause
                        || context = HavingClause
                        || context = OrderByClause
                        || (context = WindowSpecificationClause
                            && SqlWindowCapabilityRules.SupportsAggregateInWindowSpecification(targetProvider targetRuntime))
                if not allowed then
                    raise (SqlCompilationException(
                        "Aggregate function '" + name + "' is not allowed in SQL clause '" + clauseName context + "'."))
                if insideSetFunction then
                    raise (SqlCompilationException(
                        "Aggregate function '" + name + "' cannot be nested inside another aggregate or window function."))
            if isWindowFunction then
                if context <> ProjectionClause && context <> OrderByClause then
                    raise (SqlCompilationException(
                        "Window function '" + name + "' is not allowed in SQL clause '" + clauseName context + "'."))
                if insideSetFunction then
                    raise (SqlCompilationException(
                        "Window function '" + name + "' cannot be nested inside another aggregate or window function."))
            let nested = insideSetFunction || isAggregate || isWindowFunction
            call.Arguments |> List.iter (validateSemanticExpr targetRuntime context nested false)
            call.AggregateOrderBy
            |> List.iter (fun order ->
                validateSemanticExpr targetRuntime OrderByClause nested false order.Expression)
        | FilteredAggregate(value, predicate) ->
            validateFilterTarget value
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow value
            validateSemanticExpr targetRuntime PredicateClause false false predicate
        | Windowed(value, window) ->
            validateWindowTarget targetRuntime value window
            if context <> ProjectionClause && context <> OrderByClause then
                raise (SqlCompilationException(
                    "Window expressions are not allowed in SQL clause '" + clauseName context + "'."))
            if insideSetFunction then
                raise (SqlCompilationException("Window functions cannot be nested inside aggregate or window functions."))
            validateSemanticExpr targetRuntime context false true value
            window.PartitionBy
            |> List.iter (validateSemanticExpr targetRuntime WindowSpecificationClause false false)
            window.OrderBy
            |> List.iter (fun order ->
                validateSemanticExpr targetRuntime WindowSpecificationClause false false order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow value
        | SimpleCase(input, branches, fallback) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow input
            branches |> NonEmpty.iter (fun branch ->
                validateSemanticExpr targetRuntime context insideSetFunction withinWindow branch.Match
                validateSemanticExpr targetRuntime context insideSetFunction withinWindow branch.Result)
            fallback |> Option.iter (validateSemanticExpr targetRuntime context insideSetFunction withinWindow)
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch ->
                validateSemanticExpr targetRuntime PredicateClause false false branch.Condition
                validateSemanticExpr targetRuntime context insideSetFunction withinWindow branch.Result)
            fallback |> Option.iter (validateSemanticExpr targetRuntime context insideSetFunction withinWindow)
        | InList(value, items, _) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow value
            items |> NonEmpty.iter (validateSemanticExpr targetRuntime context insideSetFunction withinWindow)
        | InSubquery(value, query, _) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow value
            validateScalarSubqueryShape query
            validateSemanticQuery targetRuntime query
        | Between(value, lower, upper, _) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow value
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow lower
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow upper
        | IsNull(value, _) ->
            validateSemanticExpr targetRuntime context insideSetFunction withinWindow value
        | ScalarSubquery query ->
            validateScalarSubqueryShape query
            validateSemanticQuery targetRuntime query
        | Exists(query, _) ->
            validateSemanticQuery targetRuntime query

    and private validateSemanticTable targetRuntime source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) -> validateSemanticQuery targetRuntime query

    and private validateSemanticSelect targetRuntime select =
        select.Ctes |> List.iter (fun cte -> validateSemanticQuery targetRuntime cte.Query)
        select.From |> Option.iter (validateSemanticTable targetRuntime)
        select.Joins |> List.iter (fun join ->
            validateSemanticTable targetRuntime join.Source
            join.Predicate
            |> Option.iter (validateSemanticExpr targetRuntime PredicateClause false false))
        iterDistinctOn (validateSemanticExpr targetRuntime ProjectionClause false false) select
        select.Projection
        |> List.iter (fun item ->
            validateSemanticExpr targetRuntime ProjectionClause false false item.Expression)
        select.Where
        |> Option.iter (validateSemanticExpr targetRuntime PredicateClause false false)
        select.GroupBy
        |> List.iter (validateSemanticExpr targetRuntime GroupByClause false false)
        select.Having
        |> Option.iter (validateSemanticExpr targetRuntime HavingClause false false)

    and private identifierPartEquivalent targetRuntime (left: IdentifierPart) (right: IdentifierPart) =
        let normalize (part: IdentifierPart) =
            if part.WasQuoted || part.PreserveSpelling then
                part.Value
            else
                match targetProvider targetRuntime with
                | SqlAgentToolType.Postgres -> part.Value.ToLowerInvariant()
                | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> part.Value.ToUpperInvariant()
                | _ -> part.Value
        let comparer =
            match targetProvider targetRuntime with
            | SqlAgentToolType.Postgres | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird ->
                StringComparer.Ordinal
            | _ -> StringComparer.OrdinalIgnoreCase
        comparer.Equals(normalize left, normalize right)

    and private projectionOutputNames (select: Select) =
        let names =
            select.Projection
            |> List.map (fun item ->
                match item.Alias, item.Expression with
                | Some alias, _ -> Some alias
                | None, Column identifier
                | None, BoundColumn(identifier, _) -> Identifier.parts identifier |> List.tryLast
                | None, _ -> None)
        if names |> List.exists Option.isNone then None
        else Some(names |> List.choose id)

    and private validateSetOrderReference targetRuntime outputNames identifier =
        let parts = Identifier.parts identifier
        if parts.Length <> 1 then
            raise (SqlCompilationException(
                "Set-operation ORDER BY can reference combined output columns only; branch-qualified reference '"
                + Identifier.text identifier + "' is not valid after combination."))
        match outputNames with
        | None -> ()
        | Some names ->
            let reference = parts.Head
            let matches = names |> List.filter (identifierPartEquivalent targetRuntime reference)
            if matches.IsEmpty then
                raise (SqlCompilationException(
                    "Set-operation ORDER BY reference '" + reference.Value
                    + "' is not present in the combined output projection."))
            if matches.Length > 1 then
                raise (SqlCompilationException(
                    "Set-operation ORDER BY reference '" + reference.Value
                    + "' is ambiguous in the combined output projection; use an output position."))

    and private validateSemanticQuery targetRuntime query =
        validateSemanticSelect targetRuntime query.Head
        let expectedWidth =
            if query.Head.Projection |> List.exists (fun item -> match item.Expression with Wildcard _ -> true | _ -> false) then None
            else Some query.Head.Projection.Length
        query.SetOperations
        |> List.iter (fun branch ->
            validateSemanticQuery targetRuntime branch.Query
            match expectedWidth with
            | Some expected ->
                let actual =
                    if branch.Query.Head.Projection
                       |> List.exists (fun item -> match item.Expression with Wildcard _ -> true | _ -> false) then None
                    else Some branch.Query.Head.Projection.Length
                match actual with
                | Some value when value <> expected ->
                    raise (SqlCompilationException(
                        "Set operation projection width " + string value
                        + " does not match head projection width " + string expected + "."))
                | _ -> ()
            | None -> ())
        let headHasWildcard =
            query.Head.Projection
            |> List.exists (fun item ->
                match item.Expression with
                | Wildcard _ -> true
                | _ -> false)
        let outputNames = projectionOutputNames query.Head
        query.OrderBy
        |> List.iter (fun order ->
            match order.Expression with
            | OrderOrdinal ordinal
                when not headHasWildcard
                     && PositiveRowCount.value ordinal > query.Head.Projection.Length ->
                raise (SqlCompilationException(
                    "ORDER BY output position " + string (PositiveRowCount.value ordinal)
                    + " exceeds projection width " + string query.Head.Projection.Length + "."))
            | BoundColumn(identifier, ColumnBinding.ProjectionAlias)
                when not query.SetOperations.IsEmpty ->
                validateSetOrderReference targetRuntime outputNames identifier
            | BoundColumn(identifier, ColumnBinding.ProjectionAlias) ->
                let aliases = query.Head.Projection |> List.choose (fun item -> item.Alias)
                match Identifier.parts identifier |> List.tryHead with
                | Some reference when aliases |> List.exists (identifierPartEquivalent targetRuntime reference) -> ()
                | Some reference when query.Head.From.IsNone ->
                    raise (SqlCompilationException(
                        "Column reference '" + reference.Value
                        + "' requires a FROM source in the portable Core query model."))
                | Some reference ->
                    raise (SqlCompilationException(
                        "ORDER BY projection alias '" + reference.Value
                        + "' does not resolve under target identifier semantics."))
                | None -> ()
            | Column identifier
            | BoundColumn(identifier, _)
                when not query.SetOperations.IsEmpty ->
                validateSetOrderReference targetRuntime outputNames identifier
            | _ -> ()
            validateSemanticExpr targetRuntime OrderByClause false false order.Expression)

    let private noFromReferenceError identifier =
        raise (SqlCompilationException(
            "Column reference '" + Identifier.text identifier
            + "' requires a FROM source in the portable Core query model."))

    let rec private validateNoFromExpression allowWildcard expression =
        match expression with
        | Literal _ | Interval _ | OrderOrdinal _ -> ()
        | Column identifier ->
            noFromReferenceError identifier
        | BoundColumn(_, ColumnBinding.OuterRowSource) ->
            ()
        | BoundColumn(identifier, _) ->
            noFromReferenceError identifier
        | Wildcard _ when allowWildcard -> ()
        | Wildcard None ->
            raise (SqlCompilationException(
                "Column reference '*' requires a FROM source in the portable Core query model."))
        | Wildcard(Some identifier) ->
            noFromReferenceError identifier
        | Unary(_, operand) ->
            validateNoFromExpression false operand
        | Binary(_, left, right) ->
            validateNoFromExpression false left
            validateNoFromExpression false right
        | Like(value, pattern, _, _, _) ->
            validateNoFromExpression false value
            validateNoFromExpression false pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter (validateNoFromExpression false)
        | RegexMatch(value, pattern) ->
            validateNoFromExpression false value
            validateNoFromExpression false pattern
        | FunctionCall call ->
            let name =
                FunctionName.value call.Name
                |> fun value -> value.Trim().ToUpperInvariant()
            call.Arguments
            |> List.iteri (fun index argument ->
                let allowFunctionWildcard =
                    name = "COUNT"
                    && index = 0
                    && (match argument with
                        | Wildcard None -> true
                        | _ -> false)
                validateNoFromExpression allowFunctionWildcard argument)
            call.AggregateOrderBy
            |> List.iter (fun order ->
                validateNoFromExpression false order.Expression)
        | FilteredAggregate(value, predicate) ->
            validateNoFromExpression false value
            validateNoFromExpression false predicate
        | Windowed(value, window) ->
            validateNoFromExpression false value
            window.PartitionBy |> List.iter (validateNoFromExpression false)
            window.OrderBy
            |> List.iter (fun order ->
                validateNoFromExpression false order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            validateNoFromExpression false value
        | SimpleCase(input, branches, fallback) ->
            validateNoFromExpression false input
            branches
            |> NonEmpty.iter (fun branch ->
                validateNoFromExpression false branch.Match
                validateNoFromExpression false branch.Result)
            fallback |> Option.iter (validateNoFromExpression false)
        | SearchedCase(branches, fallback) ->
            branches
            |> NonEmpty.iter (fun branch ->
                validateNoFromExpression false branch.Condition
                validateNoFromExpression false branch.Result)
            fallback |> Option.iter (validateNoFromExpression false)
        | InList(value, items, _) ->
            validateNoFromExpression false value
            items |> NonEmpty.iter (validateNoFromExpression false)
        | InSubquery(value, query, _) ->
            validateNoFromExpression false value
            validateNoFromQuery query
        | Between(value, lower, upper, _) ->
            validateNoFromExpression false value
            validateNoFromExpression false lower
            validateNoFromExpression false upper
        | IsNull(value, _) ->
            validateNoFromExpression false value
        | ScalarSubquery query | Exists(query, _) ->
            validateNoFromQuery query

    and private visitNestedNoFromExpression expression =
        match expression with
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | Unary(_, operand) ->
            visitNestedNoFromExpression operand
        | Binary(_, left, right) ->
            visitNestedNoFromExpression left
            visitNestedNoFromExpression right
        | Like(value, pattern, _, _, _)
        | RegexMatch(value, pattern) ->
            visitNestedNoFromExpression value
            visitNestedNoFromExpression pattern
        | RawRegexCall(arguments, _) ->
            arguments |> List.iter visitNestedNoFromExpression
        | FunctionCall call ->
            call.Arguments |> List.iter visitNestedNoFromExpression
            call.AggregateOrderBy
            |> List.iter (fun order ->
                visitNestedNoFromExpression order.Expression)
        | FilteredAggregate(value, predicate) ->
            visitNestedNoFromExpression value
            visitNestedNoFromExpression predicate
        | Windowed(value, window) ->
            visitNestedNoFromExpression value
            window.PartitionBy |> List.iter visitNestedNoFromExpression
            window.OrderBy
            |> List.iter (fun order ->
                visitNestedNoFromExpression order.Expression)
        | Cast(value, _) | Extract(_, value) ->
            visitNestedNoFromExpression value
        | SimpleCase(input, branches, fallback) ->
            visitNestedNoFromExpression input
            branches
            |> NonEmpty.iter (fun branch ->
                visitNestedNoFromExpression branch.Match
                visitNestedNoFromExpression branch.Result)
            fallback |> Option.iter visitNestedNoFromExpression
        | SearchedCase(branches, fallback) ->
            branches
            |> NonEmpty.iter (fun branch ->
                visitNestedNoFromExpression branch.Condition
                visitNestedNoFromExpression branch.Result)
            fallback |> Option.iter visitNestedNoFromExpression
        | InList(value, items, _) ->
            visitNestedNoFromExpression value
            items |> NonEmpty.iter visitNestedNoFromExpression
        | InSubquery(value, query, _) ->
            visitNestedNoFromExpression value
            validateNoFromQuery query
        | Between(value, lower, upper, _) ->
            visitNestedNoFromExpression value
            visitNestedNoFromExpression lower
            visitNestedNoFromExpression upper
        | IsNull(value, _) ->
            visitNestedNoFromExpression value
        | ScalarSubquery query | Exists(query, _) ->
            validateNoFromQuery query

    and private validateNoFromSource source =
        match source with
        | NamedTable _ | CteTable _ -> ()
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            validateNoFromQuery query

    and private validateNoFromSelect select =
        select.Ctes
        |> List.iter (fun cte ->
            validateNoFromQuery cte.Query)
        select.From |> Option.iter validateNoFromSource
        select.Joins
        |> List.iter (fun join ->
            validateNoFromSource join.Source
            join.Predicate |> Option.iter visitNestedNoFromExpression)

        match select.From with
        | Some _ ->
            iterDistinctOn visitNestedNoFromExpression select
            select.Projection
            |> List.iter (fun item ->
                visitNestedNoFromExpression item.Expression)
            select.Where |> Option.iter visitNestedNoFromExpression
            select.GroupBy |> List.iter visitNestedNoFromExpression
            select.Having |> Option.iter visitNestedNoFromExpression
        | None ->
            if not select.Joins.IsEmpty then
                raise (SqlCompilationException(
                    "A Core SELECT cannot contain JOIN sources without a primary FROM source."))
            iterDistinctOn (validateNoFromExpression false) select
            select.Projection
            |> List.iter (fun item ->
                validateNoFromExpression false item.Expression)
            select.Where |> Option.iter (validateNoFromExpression false)
            select.GroupBy |> List.iter (validateNoFromExpression false)
            select.Having |> Option.iter (validateNoFromExpression false)

    and private validateNoFromQuery query =
        validateNoFromSelect query.Head
        query.SetOperations
        |> List.iter (fun branch ->
            validateNoFromQuery branch.Query)
        query.OrderBy
        |> List.iter (fun order ->
            match order.Expression with
            | BoundColumn(_, ColumnBinding.ProjectionAlias) -> ()
            | _ ->
                if query.Head.From.IsNone then
                    validateNoFromExpression false order.Expression
                else
                    visitNestedNoFromExpression order.Expression)

    let private validateNoFromDocument targetRuntime document =
        let _ = targetRuntime
        match document.Statement with
        | QueryStatement query ->
            validateNoFromQuery query
        | InsertStatement insert ->
            match insert.Input with
            | QuerySource query ->
                validateNoFromQuery query
            | Values rows ->
                rows
                |> NonEmpty.iter (NonEmpty.iter visitNestedNoFromExpression)
            | DefaultValues -> ()
            insert.Returning
            |> List.iter (fun item ->
                visitNestedNoFromExpression item.Expression)
        | UpdateStatement update ->
            update.From |> List.iter validateNoFromSource
            update.AssignmentItems
            |> NonEmpty.iter (fun item ->
                visitNestedNoFromExpression item.Value)
            update.Where |> Option.iter visitNestedNoFromExpression
            update.Returning
            |> List.iter (fun item ->
                visitNestedNoFromExpression item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter validateNoFromSource
            delete.Where |> Option.iter visitNestedNoFromExpression
            delete.Returning
            |> List.iter (fun item ->
                visitNestedNoFromExpression item.Expression)

    let rec private containsVolatileRandom expression =
        match expression with
        | FunctionCall call ->
            let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
            name = "RAND" || name = "RANDOM"
            || (call.Arguments |> List.exists containsVolatileRandom)
            || (call.AggregateOrderBy |> List.exists (fun order -> containsVolatileRandom order.Expression))
        | Unary(_, operand) -> containsVolatileRandom operand
        | Binary(_, left, right) -> containsVolatileRandom left || containsVolatileRandom right
        | Like(value, pattern, _, _, _) | RegexMatch(value, pattern) ->
            containsVolatileRandom value || containsVolatileRandom pattern
        | RawRegexCall(arguments, _) -> arguments |> List.exists containsVolatileRandom
        | FilteredAggregate(value, predicate) ->
            containsVolatileRandom value || containsVolatileRandom predicate
        | Windowed(value, window) ->
            containsVolatileRandom value
            || (window.PartitionBy |> List.exists containsVolatileRandom)
            || (window.OrderBy |> List.exists (fun order -> containsVolatileRandom order.Expression))
        | Cast(value, _) | Extract(_, value) -> containsVolatileRandom value
        | SimpleCase(input, branches, fallback) ->
            containsVolatileRandom input
            || (branches
                |> NonEmpty.toList
                |> List.exists (fun branch ->
                    containsVolatileRandom branch.Match || containsVolatileRandom branch.Result))
            || (fallback |> Option.exists containsVolatileRandom)
        | SearchedCase(branches, fallback) ->
            (branches
             |> NonEmpty.toList
             |> List.exists (fun branch ->
                 containsVolatileRandom branch.Condition || containsVolatileRandom branch.Result))
            || (fallback |> Option.exists containsVolatileRandom)
        | InList(value, items, _) ->
            containsVolatileRandom value
            || (items |> NonEmpty.toList |> List.exists containsVolatileRandom)
        | InSubquery(value, _, _) -> containsVolatileRandom value
        | Between(value, lower, upper, _) ->
            containsVolatileRandom value
            || containsVolatileRandom lower
            || containsVolatileRandom upper
        | IsNull(value, _) -> containsVolatileRandom value
        | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _ | Literal _ | Interval _
        | ScalarSubquery _ | Exists _ -> false

    let private rejectVolatileMutationPredicate expression =
        if containsVolatileRandom expression then
            raise (SqlCompilationException(
                "Nondeterministic function in UPDATE/DELETE predicate is not allowed before mutation because the approved row set must be deterministic."))

    let private validateSemanticDocument targetRuntime document =
        match document.Statement with
        | QueryStatement query -> validateSemanticQuery targetRuntime query
        | InsertStatement insert ->
            match insert.Input with
            | Values rows ->
                rows
                |> NonEmpty.iter (NonEmpty.iter (
                    validateSemanticExpr targetRuntime InsertValueClause false false))
            | QuerySource query -> validateSemanticQuery targetRuntime query
            | DefaultValues -> ()
            insert.Returning
            |> List.iter (fun item ->
                validateSemanticExpr targetRuntime ProjectionClause false false item.Expression)
        | UpdateStatement update ->
            update.AssignmentItems
            |> NonEmpty.iter (fun item ->
                validateSemanticExpr targetRuntime AssignmentClause false false item.Value)
            update.From |> List.iter (validateSemanticTable targetRuntime)
            update.Where
            |> Option.iter (fun predicate ->
                rejectVolatileMutationPredicate predicate
                validateSemanticExpr targetRuntime PredicateClause false false predicate)
            update.Returning
            |> List.iter (fun item ->
                validateSemanticExpr targetRuntime ProjectionClause false false item.Expression)
        | DeleteStatement delete ->
            delete.Using |> List.iter (validateSemanticTable targetRuntime)
            delete.Where
            |> Option.iter (fun predicate ->
                rejectVolatileMutationPredicate predicate
                validateSemanticExpr targetRuntime PredicateClause false false predicate)
            delete.Returning
            |> List.iter (fun item ->
                validateSemanticExpr targetRuntime ProjectionClause false false item.Expression)

    let validate allowedTables targetRuntime sourceExpressions targetExpressions sourceJoins targetJoins targetOrdering sourceDml targetDml conflictProofs canonical =
        let document = Canonical.value canonical

        let sourceCheck action =
            withCompilationDiagnostic
                "SQL_SOURCE_VALIDATION_REJECTED"
                SqlDiagnosticStage.SourceValidation
                SqlDiagnosticCategory.Capability
                document.Span
                action

        let semanticCheck action =
            withCompilationDiagnostic
                "SQL_SEMANTIC_VALIDATION_FAILED"
                SqlDiagnosticStage.SemanticValidation
                SqlDiagnosticCategory.Semantic
                document.Span
                action

        let targetCheck action =
            withCompilationDiagnostic
                "SQL_TARGET_CAPABILITY_REJECTED"
                SqlDiagnosticStage.TargetCapability
                SqlDiagnosticCategory.Capability
                document.Span
                action

        targetCheck (fun () -> validateNestedCteDocument targetRuntime document)
        targetCheck (fun () -> validateNoFromDocument targetRuntime document)
        sourceCheck (fun () -> proveSourceFilterDocument sourceExpressions document)
        targetCheck (fun () -> proveSourceFilterDocument targetExpressions document)

        let validated =
            semanticCheck (fun () ->
                validateSemanticDocument targetRuntime document
                validateDocument allowedTables document)

        targetCheck (fun () -> proveTargetDocument targetRuntime targetExpressions validated |> ignore)
        sourceCheck (fun () -> proveTargetJoins sourceJoins validated)
        targetCheck (fun () -> proveTargetJoins targetJoins validated)
        targetCheck (fun () -> proveOrderingAndPaging targetRuntime targetOrdering validated)
        sourceCheck (fun () -> proveTargetDml sourceDml validated)
        targetCheck (fun () -> proveTargetDml targetDml validated)
        targetCheck (fun () -> proveConflicts targetRuntime conflictProofs validated)
        ValidatedSql(validated, targetRuntime)
