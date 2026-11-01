namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
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

    let private compilationError message =
        raise (SqlCompilationException(message))

    let private sourceCapabilityMessage =
        RewriteCapabilityProvenance.sourceMessage "source semantic validation"

    let private targetCapabilityMessage =
        RewriteCapabilityProvenance.targetMessage "target capability validation"

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

    // Aggregate ordering asks whether an expression contains a row-column
    // reference. As before, subquery bodies are not searched and BETWEEN
    // only checks its value operand for this specific proof.
    let private expressionReferencesColumn expression =
        RewriteAstTraversal.exists false (function
            | Column _ | BoundColumn _ | ScalarSubquery _ | Exists _ -> true
            | _ -> false) expression

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

    // Aggregate capability validation must walk the complete query graph,
    // including scalar/derived subqueries, without recursive CLR frames.
    // This stage has a deliberately different visitation order from table
    // policy validation. Keep that order and its first-failure diagnostics.
    [<Struct>]
    type private AggregateFrame =
        | AggregateExpr of expression: Expr
        | AggregateSource of sourceBody: TableSource
        | AggregateQuery of queryBody: Query
        | AggregateSelect of selectBody: Select
        | AggregateSelectAfterCtes of selectionBody: Select

    let private validateAggregateWalk enforceSource source sourceProfile target targetProfile root =
        let pending = ResizeArray<AggregateFrame>()
        let inline push frame = pending.Add frame
        let pushExprs (values: Expr list) =
            let values = List.toArray values
            for index = values.Length - 1 downto 0 do
                push (AggregateExpr values.[index])
        let pushOrders (orders: OrderBy list) =
            let orders = List.toArray orders
            for index = orders.Length - 1 downto 0 do
                push (AggregateExpr orders.[index].Expression)
        push root
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | AggregateExpr expression ->
                match expression with
                | Spanned(_, inner) -> push (AggregateExpr inner)
                | FunctionCall call ->
                    validateAggregateCall enforceSource source sourceProfile target targetProfile call
                    pushOrders call.AggregateOrderBy
                    pushExprs call.Arguments
                | Unary(_, value)
                | Cast(value, _)
                | Extract(_, value)
                | IsNull(value, _) ->
                    push (AggregateExpr value)
                | Binary(_, left, right)
                | RegexMatch(left, right)
                | Like(left, right, _, _, _)
                | FilteredAggregate(left, right)
                | DateAdd(_, left, right)
                | DateDiff(_, left, right) ->
                    push (AggregateExpr right)
                    push (AggregateExpr left)
                | PostgresJsonAccess(value, _, _) ->
                    match SqlJsonCapabilityRules.PostgresArrowSourceValidationError(source, sourceProfile) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                    match SqlJsonCapabilityRules.PostgresArrowTargetValidationError(target, targetProfile) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                    push (AggregateExpr value)
                | RawRegexCall(arguments, _) -> pushExprs arguments
                | Windowed(value, window) ->
                    pushOrders window.OrderBy
                    pushExprs window.PartitionBy
                    push (AggregateExpr value)
                | SimpleCase(input, branches, fallback) ->
                    fallback |> Option.iter (fun value -> push (AggregateExpr value))
                    NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                        push (AggregateExpr branch.Result)
                        push (AggregateExpr branch.Match)) branches
                    push (AggregateExpr input)
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter (fun value -> push (AggregateExpr value))
                    NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                        push (AggregateExpr branch.Result)
                        push (AggregateExpr branch.Condition)) branches
                | InList(value, items, _) ->
                    NonEmpty.iterReverse (fun item ->
                        push (AggregateExpr item)) items
                    push (AggregateExpr value)
                | InSubquery(value, query, _) ->
                    push (AggregateQuery query)
                    push (AggregateExpr value)
                | Between(value, lower, upper, _) ->
                    push (AggregateExpr upper)
                    push (AggregateExpr lower)
                    push (AggregateExpr value)
                | ScalarSubquery query
                | Exists(query, _) -> push (AggregateQuery query)
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ -> ()
            | AggregateSource table ->
                match table with
                | NamedTable _ | CteTable _ -> ()
                | DerivedTable(query, _) -> push (AggregateQuery query)
                | LateralDerivedTable(query, _) ->
                    // Oracle, MySQL and PostgreSQL share a typed lateral
                    // shape, but provider-specific correlation, cardinality
                    // and row-source visibility do not imply translation.
                    if source <> target
                       && (source = SqlAgentToolType.MySQL
                           || target = SqlAgentToolType.MySQL
                           || source = SqlAgentToolType.Oracle
                           || target = SqlAgentToolType.Oracle) then
                        raise (SqlCompilationException(
                            "SQL capability 'select.lateral_derived' is native-only for Oracle/MySQL; cross-provider LATERAL lowering remains fail-closed. Source provider "
                            + string source + ", target provider " + string target + "."))
                    if enforceSource then
                        match SqlLateralDerivedTableCapabilityRules.SourceValidationError(source, sourceProfile) with
                        | null -> ()
                        | message -> raise (SqlCompilationException(message))
                    match SqlLateralDerivedTableCapabilityRules.TargetValidationError(target, targetProfile) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                    push (AggregateQuery query)
            | AggregateSelect select ->
                // Ordinary WITH is a native MySQL 8.0.1+ grammar capability.
                // Check both sides here as well as at parsing so a parsed
                // statement cannot bypass version proof through a CLR adapter.
                if not select.Ctes.IsEmpty
                   && target = SqlAgentToolType.MySQL
                   && not (select.Ctes |> List.exists (fun cte -> cte.RecursiveScope)) then
                    if enforceSource then
                        match SqlMySqlOrdinaryCteCapabilityRules.SourceValidationError(source, sourceProfile) with
                        | null -> ()
                        | message -> raise (SqlCompilationException(message))
                    match SqlMySqlOrdinaryCteCapabilityRules.TargetValidationError(target, targetProfile) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                if select.Joins |> List.exists (function ApplyJoin _ -> true | _ -> false) then
                    if select.From.IsNone then
                        raise (SqlCompilationException(
                            "SQL capability 'select.apply_join' requires a preceding FROM source."))
                    if source <> target then
                        raise (SqlCompilationException(
                            "SQL capability 'select.apply_join' is native-only; cross-provider APPLY semantics are not proven. Source provider "
                            + string source + ", target provider " + string target + "."))
                    if enforceSource then
                        match SqlApplyJoinCapabilityRules.SourceValidationError(source, sourceProfile) with
                        | null -> ()
                        | message -> raise (SqlCompilationException(message))
                    match SqlApplyJoinCapabilityRules.TargetValidationError(target, targetProfile) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                    for join in select.Joins do
                        match join with
                        | ApplyJoin(_, DerivedTable _) -> ()
                        | ApplyJoin _ ->
                            raise (SqlCompilationException(
                                "SQL capability 'select.apply_join' accepts only a derived SELECT as the APPLY source."))
                        | _ -> ()
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
                push (AggregateSelectAfterCtes select)
                let ctes = List.toArray select.Ctes
                for index = ctes.Length - 1 downto 0 do
                    push (AggregateQuery ctes.[index].Query)
            | AggregateSelectAfterCtes select ->
                // Original visitation order: distinct, projection, FROM,
                // JOIN source/predicate, WHERE, GROUP BY, HAVING.
                select.Having |> Option.iter (fun value -> push (AggregateExpr value))
                pushExprs select.GroupBy
                select.Where |> Option.iter (fun value -> push (AggregateExpr value))
                let joins = List.toArray select.Joins
                for index = joins.Length - 1 downto 0 do
                    match joins.[index].Predicate with
                    | Some predicate -> push (AggregateExpr predicate)
                    | None -> ()
                    push (AggregateSource joins.[index].Source)
                select.From |> Option.iter (fun table -> push (AggregateSource table))
                NonEmpty.iterReverse (fun item ->
                    push (AggregateExpr item.Expression)) select.ProjectionItems
                match select.DistinctMode with
                | SelectDistinct.DistinctOn values ->
                    NonEmpty.iterReverse (fun value ->
                        push (AggregateExpr value)) values
                | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> ()
            | AggregateQuery query ->
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
                pushOrders query.OrderBy
                let branches = List.toArray query.SetOperations
                for index = branches.Length - 1 downto 0 do
                    push (AggregateQuery branches.[index].Query)
                push (AggregateSelect query.Head)

    let private validateAggregateExpr enforceSource source sourceProfile target targetProfile expression =
        validateAggregateWalk enforceSource source sourceProfile target targetProfile
            (AggregateExpr expression)

    let private validateAggregateSource enforceSource source sourceProfile target targetProfile table =
        validateAggregateWalk enforceSource source sourceProfile target targetProfile
            (AggregateSource table)

    let private validateAggregateQuery enforceSource source sourceProfile target targetProfile query =
        validateAggregateWalk enforceSource source sourceProfile target targetProfile
            (AggregateQuery query)

    let private hasRichReturning items =
        items
        |> List.exists (function
            | ReturningExpression _ -> true
            | ReturningColumn _
            | ReturningWildcard _ -> false)

    let private requireNativeSqliteRichReturning source target items =
        if source <> target
           && hasRichReturning items
           && (source = SqlAgentToolType.Sqlite || target = SqlAgentToolType.Sqlite) then
            raise (SqlCompilationException(
                "SQL capability 'dml.returning.expression' is native-only for SQLite and remains fail-closed cross-provider because RETURNING row-source visibility and expression semantics are provider-bound. Source provider "
                + string source + ", target provider " + string target + "."))

    /// SQLite's UPDATE FROM arbitrarily chooses one joined source row
    /// when a target matches multiple. This is an at-most-one-row proof,
    /// independent of live table data and never inferred from WHERE equality.
    /// Each earlier read-only CTE may contribute a singleton relational
    /// source only if its closed SELECT cannot multiply input rows.
    let private sqliteUpdateFromSingletonCte (update: Update) =
        // Match SQLite Binder's identifier canonicalization. SQLite CTE
        // binding is case-insensitive even for quoted identifiers.
        let key (name: IdentifierPart) = name.Value.ToUpperInvariant()
        let singleton = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let seen = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        let mutable uniqueNames = true
        let isSingletonCteSource (source: TableSource) =
            match source with
            | CteTable(identifier, _) ->
                match Identifier.parts identifier with
                | [ name ] -> singleton.Contains(key name)
                | _ -> false
            | _ -> false

        for cte in update.Ctes do
            RewriteResourceBudget.checkpoint ()
            let name = key cte.Name
            if not (seen.Add name) then uniqueNames <- false
            let query = cte.Query
            if not cte.RecursiveScope
               && query.SetOperations.IsEmpty
               && query.Head.Ctes.IsEmpty
               && query.Head.Joins.IsEmpty
               && (query.Head.From.IsNone
                   || Option.exists isSingletonCteSource query.Head.From) then
                singleton.Add name |> ignore
            else
                // Never reuse an earlier singleton proof when a later CTE
                // with the same canonical name has unproven cardinality.
                singleton.Remove name |> ignore

        // One syntactically bound CTE source at most. Physical tables,
        // derived relations, duplicate names, extra FROM sources and
        // unproven chains fail closed.
        uniqueNames
        && (match update.From with
            | [ source ] -> isSingletonCteSource source
            | _ -> false)

    /// A closed, provider-local cardinality proof for native SQL Server
    /// CTE-prefixed joined UPDATE/DELETE. A CTE rooted in SELECT without
    /// FROM has at most one row; a SELECT over exactly one earlier proven
    /// singleton CTE cannot increase row count. No physical table, derived
    /// relation, JOIN, grouping, or set branch can acquire this proof by
    /// being named as a CTE. Proof consumes bound CteTable nodes, not raw
    /// names, so column aliases and quoted references follow Binder scope.
    let private sqlServerSingletonCteSource (ctes: Cte list) (sources: TableSource list) =
        let singleton = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let seen = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let mutable uniqueNames = true
        let isProvenSource source =
            match source with
            | CteTable(identifier, _) ->
                match Identifier.parts identifier with
                | [ name ] -> singleton.Contains name.Value
                | _ -> false
            | NamedTable _ | DerivedTable _ | LateralDerivedTable _ -> false

        for cte in ctes do
            RewriteResourceBudget.checkpoint ()
            if not (seen.Add cte.Name.Value) then uniqueNames <- false
            let query = cte.Query
            if not cte.RecursiveScope
               && query.SetOperations.IsEmpty
               && query.Head.Ctes.IsEmpty
               && query.Head.Joins.IsEmpty
               && query.Head.GroupBy.IsEmpty
               && query.Head.Having.IsNone
               && not query.FetchWithTies
               && (query.Head.From.IsNone
                   || Option.exists isProvenSource query.Head.From) then
                singleton.Add cte.Name.Value |> ignore
            else
                singleton.Remove cte.Name.Value |> ignore

        uniqueNames
        && (match sources with
            | [ source ] -> isProvenSource source
            | _ -> false)

    /// A live physical unique source INT and live target INT prove one source
    /// row per target only for this closed, nonmultiplying CTE path. A caller
    /// catalog alone is never evidence; the DB-minted object is rechecked in
    /// the host's actual transaction before the approved write.
    let private sqlServerLiveIntCteProof
        (proof: SqlServerCteUniqueKeyAssurance | null)
        (sourceProfile: SqlProviderCapabilityProfile | null)
        (targetProfile: SqlProviderCapabilityProfile | null)
        (snapshot: SqlSchemaSnapshot | null)
        (ctes: Cte list) (target: Identifier) (targetAlias: IdentifierPart option)
        (sources: TableSource list) (predicate: Expr option) =

        let same (left: string) (right: string) =
            StringComparer.Ordinal.Equals(left, right)
        let versioned (profile: SqlProviderCapabilityProfile | null) =
            not (isNull profile)
            && profile.Provider = SqlAgentToolType.MsSqlServer
            && not (isNull profile.ServerVersion)
            && profile.ServerVersion.CompareTo(Version(9, 0)) >= 0
        let qualified (identifier: Identifier) (physical: string) =
            match Identifier.parts identifier, physical.Split('.') with
            | [schema; table], [|expectedSchema; expectedTable|] ->
                same schema.Value expectedSchema && same table.Value expectedTable
            | _ -> false
        let isColumn field qualifier column =
            match Expr.unspan field with
            | Column identifier
            | BoundColumn(identifier, ColumnBinding.LocalRowSource) ->
                match Identifier.parts identifier with
                | [owner; memberName] ->
                    same owner.Value qualifier && same memberName.Value column
                | _ -> false
            | _ -> false
        let isTargetKey field (physical: string) column =
            match Expr.unspan field, physical.Split('.') with
            | Column identifier, [|schema; table|]
            | BoundColumn(identifier, ColumnBinding.LocalRowSource), [|schema; table|] ->
                match Identifier.parts identifier with
                | [s; t; k] ->
                    same s.Value schema && same t.Value table && same k.Value column
                | _ -> false
            | _ -> false
        let simpleColumn field inputName inputQualifier =
            match Expr.unspan field with
            | Column identifier
            | BoundColumn(identifier, ColumnBinding.LocalRowSource) ->
                match Identifier.parts identifier with
                | [column] -> same column.Value inputName
                | [owner; column] ->
                    same owner.Value inputQualifier && same column.Value inputName
                | _ -> false
            | _ -> false
        if isNull proof
           || proof.TargetKeyColumns.Count = 0
           || proof.TargetKeyColumns.Count <> proof.SourceKeyColumns.Count
           || proof.SourceKeyColumns.Count > 2
           || not (versioned sourceProfile) || not (versioned targetProfile)
           || (not (isNull snapshot)
               && (snapshot.Provider <> SqlAgentToolType.MsSqlServer
                   || not (same snapshot.Sha256 proof.SchemaSha256)))
           || not (qualified target proof.TargetTable)
           || targetAlias.IsSome
           || ctes.IsEmpty || ctes.Length > 3 then false
        else
            match sources with
            | [CteTable(cteIdentifier, outerAlias)] ->
                let sourceKeys = proof.SourceKeyColumns |> Seq.toList
                let targetKeys = proof.TargetKeyColumns |> Seq.toArray
                let mutable valid = true
                let mutable lastName = ""
                let mutable lastKeys: string list = []
                let seen = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
                // One physical ordered UNIQUE tuple -> at most three single-
                // source CTEs projecting EACH key unchanged in SAME ordinal.
                // This is executable proof, unlike Virtual Catalog evidence.
                for index, cte in ctes |> List.indexed do
                    RewriteResourceBudget.checkpoint ()
                    let query = cte.Query
                    let head = query.Head
                    let input =
                        match head.From, index with
                        | Some(NamedTable(name, alias)), 0
                            when qualified name proof.SourceTable ->
                            Some(sourceKeys,
                                 match alias, Identifier.parts name with
                                 | Some a, _ -> a.Value
                                 | None, [_; table] -> table.Value
                                 | _ -> "")
                        | Some(CteTable(name, alias)), n when n > 0 ->
                            match Identifier.parts name with
                            | [namePart] when same namePart.Value lastName ->
                                Some(lastKeys, match alias with
                                               | Some a -> a.Value
                                               | None -> lastName)
                            | _ -> None
                        | _ -> None
                    let directProjection =
                        match input with
                        | Some(keys, qualifier)
                            when List.length head.Projection >= List.length keys
                                 && List.length head.Projection <= 32
                                 && (cte.ColumnAliases.IsEmpty
                                     || List.length cte.ColumnAliases = List.length head.Projection) ->
                            // A key may sit anywhere among inert payload
                            // columns. Every projected expression remains a
                            // direct local-row-source column: no scalar
                            // subquery, multiplication or computed key can
                            // borrow the physical uniqueness witness.
                            let projected =
                                head.Projection
                                |> List.mapi (fun ordinal item ->
                                    let inputName =
                                        match Expr.unspan item.Expression with
                                        | Column identifier
                                        | BoundColumn(identifier, ColumnBinding.LocalRowSource) ->
                                            match Identifier.parts identifier with
                                            | [column] -> Some column.Value
                                            | [owner; column] when same owner.Value qualifier ->
                                                Some column.Value
                                            | _ -> None
                                        | _ -> None
                                    inputName
                                    |> Option.map (fun name ->
                                        let output =
                                            if cte.ColumnAliases.IsEmpty then
                                                defaultArg (item.Alias |> Option.map (fun a -> a.Value)) name
                                            else
                                                (List.item ordinal cte.ColumnAliases).Value
                                        name, output))
                            if projected |> List.forall Option.isSome then
                                let columns = projected |> List.choose id
                                let inputNames = columns |> List.map fst
                                let outputNames = columns |> List.map snd
                                let selectedKeys =
                                    keys |> List.map (fun key ->
                                        let matching =
                                            columns
                                            |> List.mapi (fun pos (name, output) ->
                                                if same name key then Some(pos, output)
                                                else None)
                                            |> List.choose id
                                        match matching with
                                        | [single] -> Some single
                                        | _ -> None)
                                if List.length (List.distinct inputNames) = List.length columns
                                   && List.length (List.distinct outputNames) = List.length columns
                                   && (selectedKeys |> List.forall Option.isSome) then
                                    let positions = selectedKeys |> List.choose id
                                    if (positions |> List.pairwise
                                        |> List.forall (fun ((before, _), (after, _)) ->
                                            before < after)) then
                                        Some(positions |> List.map snd)
                                    else None
                                else None
                            else None
                        | _ -> None
                    if not valid
                       || not (seen.Add cte.Name.Value)
                       || cte.RecursiveScope
                       || not query.SetOperations.IsEmpty
                       || not query.OrderBy.IsEmpty
                       || query.Limit.IsSome || query.Offset.IsSome
                       || query.FetchPercent.IsSome || query.FetchWithTies
                       || not head.Ctes.IsEmpty || not head.Joins.IsEmpty
                       || not head.GroupBy.IsEmpty || head.Having.IsSome
                       || head.DistinctMode <> SelectDistinct.AllRows
                       || directProjection.IsNone then
                        valid <- false
                    else
                        lastName <- cte.Name.Value
                        lastKeys <- directProjection.Value
                let outerNameMatches =
                    match Identifier.parts cteIdentifier with
                    | [name] -> same name.Value lastName
                    | _ -> false
                if not valid || not outerNameMatches
                   || List.length lastKeys <> targetKeys.Length then false
                else
                    let qualifier = match outerAlias with
                                    | Some alias -> alias.Value
                                    | None -> lastName
                    let pending = ResizeArray<Expr>()
                    predicate |> Option.iter pending.Add
                    let proven = Array.create targetKeys.Length false
                    let mutable duplicate = false
                    while pending.Count > 0 do
                        RewriteResourceBudget.checkpoint ()
                        let last = pending.Count - 1
                        let node = pending.[last]
                        pending.RemoveAt(last)
                        match Expr.unspan node with
                        | Binary(BinaryOperator.And, left, right) ->
                            pending.Add right
                            pending.Add left
                        | Binary(BinaryOperator.Equal, left, right) ->
                            for i = 0 to targetKeys.Length - 1 do
                                if (isTargetKey left proof.TargetTable targetKeys.[i]
                                    && isColumn right qualifier lastKeys.[i])
                                   || (isTargetKey right proof.TargetTable targetKeys.[i]
                                       && isColumn left qualifier lastKeys.[i]) then
                                    if proven.[i] then duplicate <- true
                                    else proven.[i] <- true
                        | _ -> ()
                    not duplicate && (proven |> Array.forall id)
            | _ -> false

    /// Public CLR mutation statements may be constructed without the native
    /// Parser. Never trust an unqualified CTE name as the physical UPDATE/
    /// DELETE write target: SQL Server permits writes through updatable CTEs,
    /// which would bypass the verified physical table identity allowlist.
    let private requireSqlServerPhysicalCteTarget (ctes: Cte list) target =
        match Identifier.parts target with
        | [ part ] when ctes |> List.exists (fun cte ->
            StringComparer.OrdinalIgnoreCase.Equals(part.Value, cte.Name.Value)) ->
            raise (SqlCompilationException(
                "SQL Server CTE name is not a verified physical mutation target; CTE-target writes require separate catalog lineage proof."))
        | _ -> ()

    /// A caller-declared Catalog is NOT a runtime key assurance. Only a
    /// separate, metadata-verified, schema-bound proof may permit this one
    /// physical-source native SQLite UPDATE FROM shape.
    let private sqliteLivePkUpdateFromProof
            (assurance: SqliteUpdateFromKeyAssurance | null)
            (snapshot: SqlSchemaSnapshot | null)
            (sourceProfile: SqlProviderCapabilityProfile | null)
            (targetProfile: SqlProviderCapabilityProfile | null)
            (update: Update) =

        let same a b = StringComparer.OrdinalIgnoreCase.Equals(a, b)
        let versionProven (profile: SqlProviderCapabilityProfile | null) =
            not (isNull profile)
            && profile.Provider = SqlAgentToolType.Sqlite
            && not (isNull profile.ServerVersion)
            && profile.ServerVersion.CompareTo(Version(3, 33, 0)) >= 0

        if isNull assurance || isNull snapshot
           || snapshot.Provider <> SqlAgentToolType.Sqlite
           || not (StringComparer.Ordinal.Equals(snapshot.Sha256, assurance.SchemaSha256))
           || not (versionProven sourceProfile)
           || not (versionProven targetProfile) then false
        else
            let exactlyOne identifier expected =
                match Identifier.parts identifier with
                | [part] -> same part.Value expected
                | _ -> false
            let keyColumn qualifier key expr =
                match Expr.unspan expr with
                | Column id
                | BoundColumn(id, ColumnBinding.LocalRowSource) ->
                    match Identifier.parts id with
                    | [ table; column ] -> same table.Value qualifier && same column.Value key
                    | _ -> false
                | _ -> false

            match update.TargetAlias, update.From with
            | None, [ NamedTable(name, alias) ]
                when exactlyOne update.Target assurance.TargetTable
                     && exactlyOne name assurance.SourceTable
                     && not (same assurance.TargetTable assurance.SourceTable) ->
                let sourceQualifier =
                    alias |> Option.map (fun x -> x.Value)
                    |> Option.defaultValue assurance.SourceTable
                let matchesKey left right =
                    (keyColumn sourceQualifier assurance.SourcePrimaryKey left
                     && keyColumn assurance.TargetTable assurance.TargetPrimaryKey right)
                    || (keyColumn sourceQualifier assurance.SourcePrimaryKey right
                        && keyColumn assurance.TargetTable assurance.TargetPrimaryKey left)
                let pending = ResizeArray<Expr>()
                update.Where |> Option.iter pending.Add
                let mutable proven = false
                while pending.Count > 0 && not proven do
                    RewriteResourceBudget.checkpoint ()
                    let at = pending.Count - 1
                    let predicate = pending.[at]
                    pending.RemoveAt at
                    match Expr.unspan predicate with
                    | Binary(BinaryOperator.And, left, right) ->
                        pending.Add left
                        pending.Add right
                    | Binary(BinaryOperator.Equal, left, right) when matchesKey left right ->
                        proven <- true
                    | _ -> ()
                proven
            | _ -> false

    let private validateAggregateDocument enforceSource source sourceProfile target targetProfile liveKeyAssurance sqlServerLiveProof schemaSnapshot document =
        match document.Statement with
        | QueryStatement query ->
            validateAggregateQuery enforceSource source sourceProfile target targetProfile query
        | InsertStatement insert ->
            requireNativeSqliteRichReturning source target insert.Returning
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
                // A SQL Server target requires the native statement-level CTE
                // grammar, even when the typed source came from PostgreSQL
                // or SQLite. Source proof is owned by the parser; target
                // proof must not be bypassed by cross-provider compilation.
                if target = SqlAgentToolType.MsSqlServer && not query.Head.Ctes.IsEmpty then
                    match SqlSqlServerWithMutationCteCapabilityRules.TargetValidationError(target, targetProfile) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                    // SQL Server can resolve an unqualified INSERT target
                    // against a directly updatable CTE alias. A syntactic
                    // CTE name is never an attested physical write target.
                    match Identifier.parts insert.Target with
                    | [ part ] when query.Head.Ctes |> List.exists (fun cte ->
                        StringComparer.OrdinalIgnoreCase.Equals(part.Value, cte.Name.Value)) ->
                        raise (SqlCompilationException(
                            "SQL Server CTE name is not a verified physical INSERT target; CTE-target writes require separate catalog lineage proof."))
                    | _ -> ()
                validateAggregateQuery enforceSource source sourceProfile target targetProfile query
            | DefaultValues -> ()
            insert.Returning |> List.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Expression)
        | UpdateStatement update ->
            if not update.Ctes.IsEmpty then
                let nativePostgres =
                    source = SqlAgentToolType.Postgres && target = SqlAgentToolType.Postgres
                let nativeSqlite =
                    source = SqlAgentToolType.Sqlite && target = SqlAgentToolType.Sqlite
                let nativeMySql =
                    source = SqlAgentToolType.MySQL && target = SqlAgentToolType.MySQL
                let nativeSqlServer =
                    source = SqlAgentToolType.MsSqlServer && target = SqlAgentToolType.MsSqlServer
                if not nativePostgres && not nativeSqlite && not nativeMySql && not nativeSqlServer then
                    raise (SqlCompilationException(
                        "Statement-level WITH UPDATE is proven only for native PostgreSQL, SQLite, MySQL or SQL Server source and target; cross-provider lowering remains fail-closed."))
                if nativeSqlServer then
                    requireSqlServerPhysicalCteTarget update.Ctes update.Target
                    if not update.From.IsEmpty
                       && not (sqlServerSingletonCteSource update.Ctes update.From)
                       && not (sqlServerLiveIntCteProof sqlServerLiveProof sourceProfile targetProfile schemaSnapshot update.Ctes update.Target update.TargetAlias update.From update.Where) then
                        raise (SqlCompilationException(
                            "SQL capability 'sqlserver.with_update.from' requires an at-most-one-row read-only CTE source for every target row; unproven joined cardinality remains fail-closed."))
                    for side, profile in [ "source", sourceProfile; "target", targetProfile ] do
                        let error =
                            if side = "source" then
                                SqlSqlServerWithMutationCteCapabilityRules.SourceValidationError(source, profile)
                            else
                                SqlSqlServerWithMutationCteCapabilityRules.TargetValidationError(target, profile)
                        match error with
                        | null -> ()
                        | message -> raise (SqlCompilationException(message))
                if nativeMySql then
                    if not update.From.IsEmpty then
                        raise (SqlCompilationException(
                            "SQL capability 'mysql.with_update.from' is not proven for CTE-prefixed MySQL UPDATE; joined mutation cardinality remains fail-closed."))
                    for side, profile in [ "source", sourceProfile; "target", targetProfile ] do
                        match profileVersion profile with
                        | Some declared when declared.CompareTo(Version(8, 0)) < 0 ->
                            raise (SqlCompilationException(
                                "SQL capability 'mysql.with_update' requires MySQL "
                                + side + " ServerVersion 8.0+; declared version is "
                                + string declared + "."))
                        | _ -> ()
                if nativeSqlite then
                    if not update.From.IsEmpty
                       && not (sqliteUpdateFromSingletonCte update)
                       && not (sqliteLivePkUpdateFromProof liveKeyAssurance schemaSnapshot sourceProfile targetProfile update) then
                        raise (SqlCompilationException(
                            "SQL capability 'sqlite.with_update.from' requires a single singleton CTE, or a live-attested SQLite INTEGER primary-key match bound to the schema snapshot; unproven sources remain fail-closed."))
                    for side, profile in [ "source", sourceProfile; "target", targetProfile ] do
                        match profileVersion profile with
                        | Some declared when declared.CompareTo(Version(3, 8, 3)) < 0 ->
                            raise (SqlCompilationException(
                                "SQL capability 'sqlite.with_update' requires SQLite "
                                + side + " ServerVersion 3.8.3+; declared version is "
                                + string declared + "."))
                        | _ -> ()
                update.Ctes |> List.iter (fun cte ->
                    validateAggregateQuery enforceSource source sourceProfile target targetProfile cte.Query)
            requireNativeSqliteRichReturning source target update.Returning
            if not update.From.IsEmpty
               && source <> target
               && (source = SqlAgentToolType.MsSqlServer
                   || target = SqlAgentToolType.MsSqlServer
                   || source = SqlAgentToolType.Sqlite
                   || target = SqlAgentToolType.Sqlite
                   || source = SqlAgentToolType.Oracle
                   || target = SqlAgentToolType.Oracle) then
                raise (SqlCompilationException(
                    "SQL capability 'dml.update.from' is native-only and remains fail-closed cross-provider when SQL Server, SQLite, or Oracle participates because duplicate-match and target-row selection semantics are not proven equivalent across providers. Source provider "
                    + string source + ", target provider " + string target + "."))
            update.AssignmentItems |> NonEmpty.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Value)
            update.From |> List.iter (validateAggregateSource enforceSource source sourceProfile target targetProfile)
            update.Where |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
            update.Returning |> List.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Expression)
        | DeleteStatement delete ->
            if not delete.Ctes.IsEmpty then
                let nativePostgres =
                    source = SqlAgentToolType.Postgres && target = SqlAgentToolType.Postgres
                let nativeSqlite =
                    source = SqlAgentToolType.Sqlite && target = SqlAgentToolType.Sqlite
                let nativeMySql =
                    source = SqlAgentToolType.MySQL && target = SqlAgentToolType.MySQL
                let nativeSqlServer =
                    source = SqlAgentToolType.MsSqlServer && target = SqlAgentToolType.MsSqlServer
                if not nativePostgres && not nativeSqlite && not nativeMySql && not nativeSqlServer then
                    raise (SqlCompilationException(
                        "Statement-level WITH DELETE is proven only for native PostgreSQL, SQLite, MySQL or SQL Server source and target; cross-provider lowering remains fail-closed."))
                if nativeSqlServer then
                    requireSqlServerPhysicalCteTarget delete.Ctes delete.Target
                    if not delete.Using.IsEmpty
                       && not (sqlServerSingletonCteSource delete.Ctes delete.Using)
                       && not (sqlServerLiveIntCteProof sqlServerLiveProof sourceProfile targetProfile schemaSnapshot delete.Ctes delete.Target delete.TargetAlias delete.Using delete.Where) then
                        raise (SqlCompilationException(
                            "SQL capability 'sqlserver.with_delete.from' requires an at-most-one-row read-only CTE source for every target row; unproven joined cardinality remains fail-closed."))
                    for side, profile in [ "source", sourceProfile; "target", targetProfile ] do
                        let error =
                            if side = "source" then
                                SqlSqlServerWithMutationCteCapabilityRules.SourceValidationError(source, profile)
                            else
                                SqlSqlServerWithMutationCteCapabilityRules.TargetValidationError(target, profile)
                        match error with
                        | null -> ()
                        | message -> raise (SqlCompilationException(message))
                if nativeMySql then
                    if not delete.Using.IsEmpty then
                        raise (SqlCompilationException(
                            "SQL capability 'mysql.with_delete.using' is not proven for CTE-prefixed MySQL DELETE; only a single physical delete target is supported."))
                    for side, profile in [ "source", sourceProfile; "target", targetProfile ] do
                        match profileVersion profile with
                        | Some declared when declared.CompareTo(Version(8, 0)) < 0 ->
                            raise (SqlCompilationException(
                                "SQL capability 'mysql.with_delete' requires MySQL "
                                + side + " ServerVersion 8.0+; declared version is "
                                + string declared + "."))
                        | _ -> ()
                if nativeSqlite then
                    for side, profile in [ "source", sourceProfile; "target", targetProfile ] do
                        match profileVersion profile with
                        | Some declared when declared.CompareTo(Version(3, 8, 3)) < 0 ->
                            raise (SqlCompilationException(
                                "SQL capability 'sqlite.with_delete' requires SQLite "
                                + side + " ServerVersion 3.8.3+; declared version is "
                                + string declared + "."))
                        | _ -> ()
                delete.Ctes |> List.iter (fun cte ->
                    validateAggregateQuery enforceSource source sourceProfile target targetProfile cte.Query)
            requireNativeSqliteRichReturning source target delete.Returning
            delete.Using |> List.iter (validateAggregateSource enforceSource source sourceProfile target targetProfile)
            delete.Where |> Option.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
            delete.Returning |> List.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Expression)
        | MergeStatement merge ->
            merge.Source.SourceValues |> NonEmpty.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile)
            validateAggregateExpr enforceSource source sourceProfile target targetProfile merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> validateAggregateExpr enforceSource source sourceProfile target targetProfile item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (validateAggregateExpr enforceSource source sourceProfile target targetProfile))

    let private emptyFunction name arguments =
        { FunctionCall.Name = FunctionName.create name
          Arguments = arguments
          IsDistinct = false
          AggregateOrderBy = []
          AggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
          AggregateSeparator = None }

    let private textLiteral label expression =
        match Expr.unspan expression with
        | Literal(ScalarValue.Text value) -> value
        | _ -> compilationError (label + " must be a string literal.")

    let private keywordValue label expression =
        match Expr.unspan expression with
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
            FunctionCall(emptyFunction "TRUNC" [ Cast(expression, CastType.forTarget target SqlType.SqlDate "DATE") ])
        | SqlAgentToolType.MySQL
        | SqlAgentToolType.Sqlite ->
            FunctionCall(emptyFunction "DATE" [ expression ])
        | SqlAgentToolType.Postgres
        | SqlAgentToolType.MsSqlServer
        | SqlAgentToolType.Firebird ->
            Cast(expression, CastType.forTarget target SqlType.SqlDate "DATE")
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
        // Standalone DML expression trees use the existing scalar mapper.
        // Its Query callback delegates to the mixed graph mapper below,
        // so nested Query/Select/Source subgraphs do not recurse.
        RewriteAstTraversal.mapPostOrder
            (fun _ -> RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation)
            (normalizeScalarNode source target)
            (normalizeQuery source target)
            expression

    and private normalizeScalarNode source target (rebuilt: Expr) =
        match rebuilt with
        | Unary(Positive, operand) -> operand
        | RawRegexCall(arguments, _) ->
            match arguments with
            | [ value; pattern ] -> RegexMatch(value, pattern)
            | values ->
                compilationError (
                    "Function 'CORE_REGEX_MATCH' requires 2 argument(s); received "
                    + string values.Length + ".")
        | FunctionCall call -> normalizeFunctionPrepared source target call
        | Cast(value, targetType) ->
            Cast(
                value,
                RewriteCastTypes.normalize
                    (sourceProvider source)
                    (TargetRuntime.provider target)
                    targetType)
        | _ -> rebuilt

    and private normalizeFunctionPrepared source target (call: FunctionCall) =
        // Child arguments and aggregate ORDER BY are already normalized by
        // the shared post-order walker, in the original source order.
        let sourceTool = sourceProvider source
        let targetTool = TargetRuntime.provider target
        let arguments = call.Arguments
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

        if FunctionName.requiresNativeIdentifierSemantics call.Name then
            // Native quoted or qualified names are opaque identities. They are never canonicalized
            // into portable built-ins; same-provider compilation preserves them, while cross-provider
            // compilation remains intact until the typed target capability boundary rejects it.
            FunctionCall call
        else
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
                    if call.IsDistinct then
                        compilationError "CORE_DATE_ADD does not support DISTINCT."
                    if not call.AggregateOrderBy.IsEmpty || call.AggregateSeparator.IsSome then
                        compilationError "CORE_DATE_ADD does not support aggregate-local modifiers."
                    if arguments.Length <> 3 then
                        compilationError (sourceName + " requires exactly 3 arguments.")
                    let unit =
                        keywordValue (sourceName + " date-part unit") arguments[0]
                        |> fun value -> SqlDateMathUnit.Parse(value, sourceName)
                    DateAdd(unit, arguments[1], arguments[2])

                | SqlSourceFunctionCanonicalizationKind.DateDiff ->
                    if call.IsDistinct then
                        compilationError "CORE_DATE_DIFF does not support DISTINCT."
                    if not call.AggregateOrderBy.IsEmpty || call.AggregateSeparator.IsSome then
                        compilationError "CORE_DATE_DIFF does not support aggregate-local modifiers."
                    let portableDay startValue endValue =
                        let canonical =
                            DateDiff(
                                SqlDateMathUnit.Day,
                                dateOnlyOperand targetTool startValue,
                                dateOnlyOperand targetTool endValue)
                        if targetTool = SqlAgentToolType.Sqlite then
                            Cast(canonical, CastType.forTarget targetTool SqlType.SqlInteger "INTEGER")
                        else canonical
                    match arguments with
                    | [ finish; startValue ] ->
                        portableDay startValue finish
                    | [ unitExpr; startValue; finish ] ->
                        let unit =
                            keywordValue (sourceName + " date-part unit") unitExpr
                            |> fun value -> SqlDateMathUnit.Parse(value, sourceName)
                        if (sourceTool = SqlAgentToolType.MsSqlServer
                            || sourceTool = SqlAgentToolType.Firebird
                            || (sourceTool = SqlAgentToolType.MySQL && sourceName = "TIMESTAMPDIFF"))
                           && sourceTool = targetTool then
                            DateDiff(unit, startValue, finish)
                        elif unit <> SqlDateMathUnit.Day then
                            let keyword = SqlDateMathUnit.Keyword unit
                            compilationError (
                                "Cross-dialect DATEDIFF unit '" + keyword + "' from " + string sourceTool + " to " + string targetTool
                                + " is not translated: SQL capability 'core_date_diff.unit." + keyword.ToLowerInvariant()
                                + "' is not modeled losslessly. DAY is the currently modeled portable intersection.")
                        else
                            portableDay startValue finish
                    | values ->
                        compilationError (
                            sourceName + " requires either the portable 2-argument (end, start) shape or the "
                            + "3-argument (unit, start, end) shape; received " + string values.Length + " arguments.")

                | SqlSourceFunctionCanonicalizationKind.DatePart ->
                    if call.IsDistinct then
                        compilationError "CORE_DATE_PART does not support DISTINCT."
                    if not call.AggregateOrderBy.IsEmpty || call.AggregateSeparator.IsSome then
                        compilationError "CORE_DATE_PART does not support aggregate-local modifiers."
                    if arguments.Length <> 2 then
                        compilationError (sourceName + " requires exactly 2 arguments.")
                    let rawPart =
                        if sourceName = "DATE_PART" then
                            textLiteral "DATE_PART field" arguments[0]
                        else
                            keywordValue "DATEPART field" arguments[0]
                    let part = rawPart.Trim().ToUpperInvariant()
                    if not (SqlDatePartCapabilityRules.IsRepresentedPart(part)) then
                        compilationError (
                            "Date part " + part
                            + " is outside the declared Core date-part family.")
                    Extract(ExtractField.create part, arguments[1])

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
                let renderedName =
                    if FunctionName.requiresNativeIdentifierSemantics call.Name then
                        call.Name
                    else
                        FunctionName.create "COALESCE"
                FunctionCall { call with Name = renderedName; Arguments = arguments }

            | None when SqlCanonicalFunctionRegistry.IsDirectPortable(sourceRegistryName) ->
                let renderedName =
                    if sourceTool = targetTool && FunctionName.requiresNativeIdentifierSemantics call.Name then
                        call.Name
                    else
                        let name = if sourceTool = targetTool then sourceName else sourceRegistryName
                        FunctionName.create name
                FunctionCall { call with Name = renderedName; Arguments = arguments }

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
                    let renderedName =
                        if FunctionName.requiresNativeIdentifierSemantics call.Name then
                            call.Name
                        else
                            FunctionName.create sourceName
                    FunctionCall { call with Name = renderedName; Arguments = arguments }

    and private normalizeCteAliases (cte: Cte) =
        // The Query has already been normalized by the mixed post-order
        // mapper, so aliases are lowered after the CTE body completes.
        if cte.ColumnAliases.IsEmpty then cte
        else
            let projection = cte.Query.Head.Projection
            if projection |> List.exists (fun item ->
                match Expr.unspan item.Expression with Wildcard _ -> true | _ -> false) then
                invalidOp (
                    "CTE '" + cte.Name.Value
                    + "' column aliases cannot be lowered safely when the CTE projection contains a wildcard.")
            if projection.Length <> cte.ColumnAliases.Length then
                invalidOp (
                    "CTE '" + cte.Name.Value + "' declares "
                    + string cte.ColumnAliases.Length
                    + " column alias(es) but its statically modeled projection has "
                    + string projection.Length + " column(s).")
            let rewritten =
                (projection, cte.ColumnAliases)
                ||> List.map2 (fun item alias -> { item with Alias = Some alias })
                |> NonEmpty.ofList "CTE projection"
            { cte with
                ColumnAliases = []
                Query =
                    { cte.Query with
                        Head = { cte.Query.Head with ProjectionItems = rewritten } } }

    and private normalizeQuery sourceDialect target (query: Query) =
        // Deep, inert scalar wrappers were already proven safe for peeling
        // and rebuilding; retain that optimization instead of allocating a
        // full mixed-graph frame set at each of thousands of nested SELECTs.
        if RewriteAstTraversal.trySimpleScalarChain query |> Option.isSome then
            let wrappers = ResizeArray<struct (Query * SelectItem)>()
            let mutable current = query
            let mutable peeling = true
            while peeling do
                match RewriteAstTraversal.trySimpleScalarChain current with
                | Some(item, child) ->
                    RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation
                    wrappers.Add(struct (current, item))
                    current <- child
                | None -> peeling <- false
            let mutable normalized = normalizeQuery sourceDialect target current
            for index = wrappers.Count - 1 downto 0 do
                let struct (outer, item) = wrappers.[index]
                normalized <- RewriteAstTraversal.rebuildSimpleScalarChain outer item normalized
            normalized
        else
            let select = query.Head
            let plainSource = function
                | NamedTable _ | CteTable _ -> true
                | DerivedTable _ | LateralDerivedTable _ -> false
            let queryFree expression =
                not (RewriteAstTraversal.exists true
                    (function
                     | ScalarSubquery _ | Exists _ | InSubquery _ -> true
                     | _ -> false) expression)
            let isFlat =
                // This branch has no child Query anywhere, including CASE,
                // aggregate-local ORDER BY, join ON or statement ORDER BY.
                // Scanning AST expressions is iterative and has no semantic
                // effects, so the old scalar fast path retains its exact
                // source-order error and parameter behavior.
                query.SetOperations.IsEmpty
                && select.Ctes.IsEmpty
                && (select.From |> Option.map plainSource |> Option.defaultValue true)
                && (select.Joins |> List.forall (fun join ->
                    plainSource join.Source
                    && (join.Predicate |> Option.map queryFree |> Option.defaultValue true)))
                && (match select.DistinctMode with
                    | SelectDistinct.DistinctOn values ->
                        NonEmpty.toList values |> List.forall queryFree
                    | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> true)
                && (NonEmpty.toList select.ProjectionItems
                    |> List.forall (fun item -> queryFree item.Expression))
                && (select.Where |> Option.map queryFree |> Option.defaultValue true)
                && (select.GroupBy |> List.forall queryFree)
                && (select.Having |> Option.map queryFree |> Option.defaultValue true)
                && (query.OrderBy |> List.forall (fun item -> queryFree item.Expression))
            if isFlat then
                RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation
                let head =
                    { select with
                        DistinctMode =
                            select.DistinctMode
                            |> mapDistinctOn (normalizeExpr sourceDialect target)
                        ProjectionItems =
                            select.ProjectionItems
                            |> NonEmpty.map (fun item ->
                                { item with
                                    Expression =
                                        normalizeExpr sourceDialect target item.Expression })
                        Joins =
                            select.Joins
                            |> List.map (function
                                | OnJoin(kind, source, predicate) ->
                                    OnJoin(kind, source, normalizeExpr sourceDialect target predicate)
                                | other -> other)
                        Where = select.Where |> Option.map (normalizeExpr sourceDialect target)
                        GroupBy = select.GroupBy |> List.map (normalizeExpr sourceDialect target)
                        Having = select.Having |> Option.map (normalizeExpr sourceDialect target) }
                { query with
                    Head = head
                    OrderBy =
                        query.OrderBy
                        |> List.map (fun order ->
                            { order with
                                Expression =
                                    normalizeExpr sourceDialect target order.Expression }) }
            else
                // Mixed Query/Expr/Select/CTE/Source/Join edges are handled
                // by one heap-backed post-order frame machine, never by a
                // depth-proportional callback chain.
                RewriteAstTraversal.mapQueryGraphPostOrder
                    (fun _ -> RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation)
                    (normalizeScalarNode sourceDialect target)
                    (fun _ -> RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation)
                    normalizeCteAliases
                    query

    and private normalizeSource sourceDialect target (source: TableSource) =
        RewriteAstTraversal.mapSourceGraphPostOrder
            (fun _ -> RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation)
            (normalizeScalarNode sourceDialect target)
            (fun _ -> RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation)
            normalizeCteAliases
            source

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
                        Ctes = update.Ctes |> List.map (fun cte ->
                            { cte with Query = normalizeQuery source target cte.Query })
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
                        Ctes = delete.Ctes |> List.map (fun cte ->
                            { cte with Query = normalizeQuery source target cte.Query })
                        Using = delete.Using |> List.map (normalizeSource source target)
                        Where = delete.Where |> Option.map (normalizeExpr source target)
                        Returning = normalizeReturning source target delete.Returning }
            | MergeStatement merge ->
                let matched =
                    merge.Matched
                    |> Option.map (function
                        | MergeDelete -> MergeDelete
                        | MergeUpdate assignments ->
                            assignments
                            |> NonEmpty.map (fun assignment ->
                                { assignment with Value = normalizeExpr source target assignment.Value })
                            |> MergeUpdate)
                let notMatched =
                    merge.NotMatched
                    |> Option.map (fun mergeInsert ->
                        { mergeInsert with
                            InsertValues =
                                mergeInsert.InsertValues |> NonEmpty.map (normalizeExpr source target) })
                MergeStatement
                    { merge with
                        Source =
                            { merge.Source with
                                SourceValues = merge.Source.SourceValues |> NonEmpty.map (normalizeExpr source target) }
                        MatchPredicate = normalizeExpr source target merge.MatchPredicate
                        Matched = matched
                        NotMatched = notMatched }
        { document with Statement = statement }

    let internal normalizeDocumentForInspection sourceDialect targetRuntime document =
        normalizeDocument sourceDialect targetRuntime document

    let normalize enforceDialectSyntax sourceDialect targetRuntime sourceRegexProof sourceOrdering mySqlPipes sourceProfile targetProfile liveKeyAssurance sqlServerLiveProof schemaSnapshot bound =
        Transition.normalize
            (fun document ->
                withCompilationDiagnostic
                    "SQL_SOURCE_VALIDATION_REJECTED"
                    SqlDiagnosticStage.SourceValidation
                    SqlDiagnosticCategory.Capability
                    document.Span
                    (fun () -> RewriteSourceValidation.verifyRegexDocument sourceRegexProof document)

                let source = sourceProvider sourceDialect
                let target = TargetRuntime.provider targetRuntime

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
                            liveKeyAssurance
                            sqlServerLiveProof
                            schemaSnapshot
                            document)

                if enforceDialectSyntax then
                    withCompilationDiagnostic
                        "SQL_SOURCE_VALIDATION_REJECTED"
                        SqlDiagnosticStage.SourceValidation
                        SqlDiagnosticCategory.Capability
                        document.Span
                        (fun () -> RewriteSourceValidation.validateRawDocument source sourceOrdering mySqlPipes document)

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

    let private isWildcard expression =
        match Expr.unspan expression with
        | Wildcard _ -> true
        | _ -> false

    let private ensureNoDistinctWildcard (call: FunctionCall) =
        if call.IsDistinct && call.Arguments |> List.exists isWildcard then
            invalidOp "COUNT(DISTINCT *) is not a valid Core aggregate shape."

    // All policy checks run as an explicit continuation-machine traversal.
    // Preserve the legacy recursive walk's first-error order, notably CTEs
    // before SELECT, query alias checks before UNION tails, and ON predicates.
    let private validateWalk allowedTables frame =
        RewriteAstTraversal.iterIncludingQueries (function
            | RewriteAstTraversal.ExprNode(RawRegexCall _) ->
                invalidOp "Raw REGEXP_LIKE reached plan validation before canonicalization."
            | RewriteAstTraversal.ExprNode(FunctionCall call) ->
                ensureNoDistinctWildcard call
            | RewriteAstTraversal.SourceNode(NamedTable(identifier, _)) ->
                ensureTableAllowed allowedTables identifier
            | RewriteAstTraversal.AfterCtes select ->
                if select.From.IsNone && select.Joins.IsEmpty
                   && (select.Projection |> List.exists (fun item -> isWildcard item.Expression)) then
                    invalidOp "Column reference '*' requires a FROM source in the portable Core query model."
            | RewriteAstTraversal.AfterQueryHead query ->
                let duplicateAliases = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                let seenAliases = HashSet<string>(StringComparer.OrdinalIgnoreCase)
                for item in query.Head.Projection do
                    match item.Alias with
                    | Some alias when not (seenAliases.Add alias.Value) ->
                        duplicateAliases.Add alias.Value |> ignore
                    | _ -> ()
                if duplicateAliases.Count > 0 then
                    for order in query.OrderBy do
                        match Expr.unspan order.Expression with
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
            | _ -> ()) frame

    let private validateExpr allowedTables expression =
        validateWalk allowedTables (RewriteAstTraversal.ExprNode expression)

    let private validateSource allowedTables source =
        validateWalk allowedTables (RewriteAstTraversal.SourceNode source)

    let private validateQuery allowedTables query =
        validateWalk allowedTables (RewriteAstTraversal.QueryNode query)

    // Use heap-backed work frames rather than consuming a CLR call frame
    // for every nested INSERT VALUES scalar expression.
    let private validateInsertValueScope expression =
        RewriteAstTraversal.iter (function
            | Column identifier
            | BoundColumn(identifier, _) ->
                invalidOp ("INSERT VALUES scalar expression cannot reference column '" + identifierText identifier + "' outside a scalar subquery; use INSERT ... SELECT when the inserted value depends on a source row.")
            | Wildcard _ | OrderOrdinal _ ->
                invalidOp "INSERT VALUES scalar expression cannot contain a wildcard or ORDER BY ordinal."
            | RawRegexCall _ ->
                invalidOp "Raw REGEXP_LIKE reached INSERT validation before canonicalization."
            | _ -> ()) expression

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
            update.Ctes |> List.iter (fun cte -> validateQuery allowedTables cte.Query)
            ensureTableAllowed allowedTables update.Target
            update.From |> List.iter (validateSource allowedTables)
            update.AssignmentItems |> NonEmpty.iter (fun assignment -> validateExpr allowedTables assignment.Value)
            update.Where |> Option.iter (validateExpr allowedTables)
            validateReturning allowedTables update.Returning
        | DeleteStatement delete ->
            delete.Ctes |> List.iter (fun cte -> validateQuery allowedTables cte.Query)
            ensureTableAllowed allowedTables delete.Target
            delete.Using |> List.iter (validateSource allowedTables)
            delete.Where |> Option.iter (validateExpr allowedTables)
            validateReturning allowedTables delete.Returning
        | MergeStatement merge ->
            ensureTableAllowed allowedTables merge.Target
            merge.Source.SourceValues |> NonEmpty.iter (validateExpr allowedTables)
            validateExpr allowedTables merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments |> NonEmpty.iter (fun item -> validateExpr allowedTables item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues |> NonEmpty.iter (validateExpr allowedTables))
        document

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

    // Boolean CASE classification only needs to inspect result expressions.
    // Use explicit work frames rather than recursive CASE -> CASE calls. Do
    // not inspect branch WHEN predicates: this is a result-type proof, not
    // full semantic validation.
    let private isDefinitelyBoolean targetRuntime expression =
        let pending = ResizeArray<Expr>()
        pending.Add expression
        let mutable valid = true
        while valid && pending.Count > 0 do
            let last = pending.Count - 1
            let current = Expr.unspan pending.[last]
            pending.RemoveAt(last)
            match current with
            | Literal(ScalarValue.Boolean _) -> ()
            | IsNull _ | InList _ | InSubquery _ | Between _
            | Exists _ | Like _ -> ()
            | RegexMatch _ ->
                valid <- SqlRegexCapabilityRules.SupportsTarget(TargetRuntime.provider targetRuntime, null)
            | Unary(UnaryOperator.Not, _) -> ()
            | Binary(operator, _, _) ->
                valid <-
                    match operator with
                    | BinaryOperator.Equal | BinaryOperator.NotEqual
                    | BinaryOperator.GreaterThan | BinaryOperator.LessThan
                    | BinaryOperator.GreaterThanOrEqual | BinaryOperator.LessThanOrEqual
                    | BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom
                    | BinaryOperator.And | BinaryOperator.Or -> true
                    | _ -> false
            | SimpleCase(_, branches, fallback) ->
                let mutable nonNull = 0
                let pushResult value =
                    match Expr.unspan value with
                    | Literal ScalarValue.Null -> ()
                    | _ ->
                        nonNull <- nonNull + 1
                        pending.Add value
                fallback |> Option.iter pushResult
                NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                    pushResult branch.Result) branches
                if nonNull = 0 then valid <- false
            | SearchedCase(branches, fallback) ->
                let mutable nonNull = 0
                let pushResult value =
                    match Expr.unspan value with
                    | Literal ScalarValue.Null -> ()
                    | _ ->
                        nonNull <- nonNull + 1
                        pending.Add value
                fallback |> Option.iter pushResult
                NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                    pushResult branch.Result) branches
                if nonNull = 0 then valid <- false
            | _ -> valid <- false
        valid

    let private validateBooleanScalar targetRuntime capability expression =
        if isDefinitelyBoolean targetRuntime expression then
            match SqlScalarBooleanCapabilityRules.TargetValidationError(TargetRuntime.provider targetRuntime, capability) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))

    let private canonicalFunctionKind (call: FunctionCall) =
        let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
        if FunctionName.requiresNativeIdentifierSemantics call.Name then
            name, false, false
        else
            name, SqlCanonicalFunctionRegistry.IsAggregate(name), SqlCanonicalFunctionRegistry.IsWindow(name)

    let private targetCapabilityError provider capability =
        SqlCompilationException(
            "SQL capability '" + capability + "' is not supported by provider "
            + string provider + " for this Core plan.")

    let private integerLiteral expression =
        match Expr.unspan expression with
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

    let private validateJsonPath provider canonicalName arguments =
        let path =
            match arguments |> List.tryItem 1 |> Option.map Expr.unspan with
            | Some(Literal(ScalarValue.Text value)) -> value
            | _ -> raise (targetCapabilityError provider "json.path.constant")
        let parsedPath =
            match JsonPath.tryParse path with
            | Ok value -> value
            | Error _ ->
                raise (SqlCompilationException(
                    "JSON path '" + path + "' is outside the portable Core property/array-index subset. "
                    + "SQL capability 'json.path.property_chain' is not supported by provider "
                    + string provider + " for this Core plan."))
        if JsonPath.hasArrayIndex parsedPath then
            match canonicalName with
            | "CORE_JSON_EXTRACT"
                when provider = SqlAgentToolType.Postgres
                     || provider = SqlAgentToolType.MySQL
                     || provider = SqlAgentToolType.Sqlite -> ()
            | "CORE_JSON_SET" ->
                raise (targetCapabilityError provider "json.path.mutation_array_index")
            | _ ->
                raise (targetCapabilityError provider "json.path.array_index_extract")

    let private validateCanonicalFunction targetRuntime withinWindow (call: FunctionCall) =
        let provider = TargetRuntime.provider targetRuntime
        let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
        let nativeIdentifierIdentity = FunctionName.requiresNativeIdentifierSemantics call.Name
        if nativeIdentifierIdentity
           && (call.IsDistinct || not call.AggregateOrderBy.IsEmpty || call.AggregateSeparator.IsSome) then
            raise (SqlCompilationException(
                "Native quoted or qualified function '" + name
                + "' cannot use DISTINCT or aggregate-local modifiers until its aggregate semantics are explicitly modeled."))
        let contract =
            if nativeIdentifierIdentity then None
            else SqlCanonicalFunctionRegistry.Find(name) |> Option.ofObj
        match contract with
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
                let argument =
                    call.Arguments
                    |> List.item rule.ArgumentIndex
                    |> Expr.unspan
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
                | null -> validateJsonPath provider contract.Name call.Arguments
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
                    let unit = SqlDateMathUnit.Parse(rawUnit, contract.Name)
                    match SqlDateMathCapabilityRules.TargetValidationError(unit, provider, contract.Name) with
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

    let private directWindowFunction expression =
        match Expr.unspan expression with
        | FunctionCall call -> Some call
        | FilteredAggregate(value, _) ->
            match Expr.unspan value with
            | FunctionCall call -> Some call
            | _ -> None
        | _ -> None

    let private validateFilterTarget expression =
        match Expr.unspan expression with
        | FunctionCall call ->
            let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
            if FunctionName.requiresNativeIdentifierSemantics call.Name then
                raise (SqlCompilationException(
                    "Native quoted or qualified function '" + name
                    + "' cannot use FILTER until its aggregate semantics are explicitly modeled."))
            match SqlCanonicalFunctionRegistry.Find(name) |> Option.ofObj with
            | Some contract when contract.AllowFilter -> ()
            | _ ->
                raise (SqlCompilationException(
                    "Function '" + name + "' does not support FILTER in the Core pipeline."))
        | _ ->
            raise (SqlCompilationException(
                "FILTER must modify a directly modeled aggregate function."))

    let private validateWindowTarget targetRuntime value (window: WindowSpec) =
        let provider = TargetRuntime.provider targetRuntime
        match directWindowFunction value with
        | None -> raise (SqlCompilationException(
                    "OVER must modify a directly modeled aggregate or window function."))
        | Some call ->
            let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
            if FunctionName.requiresNativeIdentifierSemantics call.Name then
                raise (SqlCompilationException(
                    "Native quoted or qualified function '" + name
                    + "' cannot use OVER until its aggregate/window semantics are explicitly modeled."))
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
               match Expr.unspan item.Expression with
               | Wildcard _ -> true
               | _ -> false) then
            raise (SqlCompilationException(
                "Scalar subquery projection width must be statically known and cannot contain a wildcard."))
        if projection.Length <> 1 then
            raise (SqlCompilationException(
                "Scalar subquery must project exactly one expression; projected "
                + string projection.Length + "."))

    let private identifierPartEquivalent targetRuntime (left: IdentifierPart) (right: IdentifierPart) =
        let normalize (part: IdentifierPart) =
            if part.WasQuoted || part.PreserveSpelling then
                part.Value
            else
                match TargetRuntime.provider targetRuntime with
                | SqlAgentToolType.Postgres -> part.Value.ToLowerInvariant()
                | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> part.Value.ToUpperInvariant()
                | _ -> part.Value
        let comparer =
            match TargetRuntime.provider targetRuntime with
            | SqlAgentToolType.Postgres | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird ->
                StringComparer.Ordinal
            | _ -> StringComparer.OrdinalIgnoreCase
        comparer.Equals(normalize left, normalize right)

    let private projectionOutputNames (select: Select) =
        let names =
            select.Projection
            |> List.map (fun item ->
                match item.Alias, Expr.unspan item.Expression with
                | Some alias, _ -> Some alias
                | None, Column identifier
                | None, BoundColumn(identifier, _) -> Identifier.parts identifier |> List.tryLast
                | None, _ -> None)
        if names |> List.exists Option.isNone then None
        else Some(names |> List.choose id)

    let private validateSetOrderReference targetRuntime outputNames identifier =
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

    // One explicit frame machine validates Expr, Query, Select and source
    // subgraphs. In particular, a scalar subquery no longer recursively
    // enters validateSemanticQuery -> validateSemanticSelect -> Expr.
    // Continuation frames preserve the old diagnostic-first order.
    [<Struct>]
    type private SemanticFrame =
        | SemanticExpr of clause: ClauseContext * inSet: bool * inWindow: bool * expression: Expr
        | SemanticTable of tableSource: TableSource
        | SemanticSelect of selectBody: Select
        | SemanticAfterCtes of selectTail: Select
        | SemanticQuery of queryBody: Query
        | SemanticAfterHead of queryTail: Query
        | SemanticAfterBranch of expectedWidth: int option * branchQuery: Query
        | SemanticAfterBranches of queryTail: Query
        | SemanticOrder of orderQuery: Query * outputNames: IdentifierPart list option * headHasWildcard: bool * order: OrderBy
        | SemanticInSubquery of queryBody: Query

    let private validateSemanticWalk targetRuntime (root: SemanticFrame) =
        let pending = ResizeArray<SemanticFrame>()
        let inline push frame = pending.Add frame
        let inline pushExpr context inSet inWindow expression =
            push (SemanticExpr(context, inSet, inWindow, expression))
        let pushExprs context inSet inWindow (values: Expr list) =
            let ordered = List.toArray values
            for index = ordered.Length - 1 downto 0 do
                pushExpr context inSet inWindow ordered.[index]
        let pushOrders context inSet inWindow (orders: OrderBy list) =
            let ordered = List.toArray orders
            for index = ordered.Length - 1 downto 0 do
                pushExpr context inSet inWindow ordered.[index].Expression
        let hasWildcard (select: Select) =
            select.Projection |> List.exists (fun item ->
                match Expr.unspan item.Expression with
                | Wildcard _ -> true
                | _ -> false)
        push root
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last
            match frame with
            | SemanticExpr(context, insideSetFunction, withinWindow, expression) ->
                RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation
                match context with
                | ProjectionClause ->
                    validateBooleanScalar targetRuntime "expression.boolean_select" expression
                | AssignmentClause ->
                    validateBooleanScalar targetRuntime "dml.update.boolean_assignment" expression
                | InsertValueClause ->
                    validateBooleanScalar targetRuntime "dml.insert.boolean_value" expression
                | _ -> ()
                let inline visit expr = pushExpr context insideSetFunction withinWindow expr
                match expression with
                | Spanned(_, inner) -> visit inner
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ -> ()
                | DateAdd(_, amount, value)
                | DateDiff(_, amount, value)
                | Binary(_, amount, value)
                | Like(amount, value, _, _, _)
                | RegexMatch(amount, value) ->
                    visit value
                    visit amount
                | Unary(_, operand)
                | PostgresJsonAccess(operand, _, _)
                | Cast(operand, _) | Extract(_, operand)
                | IsNull(operand, _) -> visit operand
                | RawRegexCall(arguments, _) ->
                    pushExprs context insideSetFunction withinWindow arguments
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
                                    && SqlWindowCapabilityRules.SupportsAggregateInWindowSpecification(TargetRuntime.provider targetRuntime))
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
                    pushOrders OrderByClause nested false call.AggregateOrderBy
                    pushExprs context nested false call.Arguments
                | FilteredAggregate(value, predicate) ->
                    validateFilterTarget value
                    pushExpr PredicateClause false false predicate
                    visit value
                | Windowed(value, window) ->
                    validateWindowTarget targetRuntime value window
                    if context <> ProjectionClause && context <> OrderByClause then
                        raise (SqlCompilationException(
                            "Window expressions are not allowed in SQL clause '" + clauseName context + "'."))
                    if insideSetFunction then
                        raise (SqlCompilationException("Window functions cannot be nested inside aggregate or window functions."))
                    pushOrders WindowSpecificationClause false false window.OrderBy
                    pushExprs WindowSpecificationClause false false window.PartitionBy
                    pushExpr context false true value
                | SimpleCase(input, branches, fallback) ->
                    fallback |> Option.iter visit
                    NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                        visit branch.Result
                        visit branch.Match) branches
                    visit input
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter visit
                    NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                        visit branch.Result
                        pushExpr PredicateClause false false branch.Condition) branches
                | InList(value, items, _) ->
                    NonEmpty.iterReverse visit items
                    visit value
                | InSubquery(value, query, _) ->
                    push (SemanticInSubquery query)
                    visit value
                | Between(value, lower, upper, _) ->
                    visit upper
                    visit lower
                    visit value
                | ScalarSubquery query ->
                    validateScalarSubqueryShape query
                    push (SemanticQuery query)
                | Exists(query, _) -> push (SemanticQuery query)
            | SemanticInSubquery query ->
                validateScalarSubqueryShape query
                push (SemanticQuery query)
            | SemanticTable source ->
                match source with
                | NamedTable _ | CteTable _ -> ()
                | DerivedTable(query, _) | LateralDerivedTable(query, _) ->
                    push (SemanticQuery query)
            | SemanticSelect select ->
                push (SemanticAfterCtes select)
                let ctes = List.toArray select.Ctes
                for index = ctes.Length - 1 downto 0 do
                    push (SemanticQuery ctes.[index].Query)
            | SemanticAfterCtes select ->
                // FROM, JOIN source/predicate, DISTINCT, projection, WHERE,
                // GROUP, HAVING -- identical order to original validator.
                select.Having |> Option.iter (pushExpr HavingClause false false)
                pushExprs GroupByClause false false select.GroupBy
                select.Where |> Option.iter (pushExpr PredicateClause false false)
                NonEmpty.iterReverse (fun item ->
                    pushExpr ProjectionClause false false item.Expression) select.ProjectionItems
                match select.DistinctMode with
                | SelectDistinct.DistinctOn values ->
                    NonEmpty.iterReverse (pushExpr ProjectionClause false false) values
                | SelectDistinct.AllRows | SelectDistinct.DistinctRows -> ()
                let joins = List.toArray select.Joins
                for index = joins.Length - 1 downto 0 do
                    match joins.[index].Predicate with
                    | Some predicate -> pushExpr PredicateClause false false predicate
                    | None -> ()
                    push (SemanticTable joins.[index].Source)
                select.From |> Option.iter (fun source -> push (SemanticTable source))
            | SemanticQuery query ->
                RewriteStackSafety.compile SqlDiagnosticStage.SemanticValidation
                push (SemanticAfterHead query)
                push (SemanticSelect query.Head)
            | SemanticAfterHead query ->
                let width =
                    if hasWildcard query.Head then None
                    else Some query.Head.Projection.Length
                push (SemanticAfterBranches query)
                let branches = List.toArray query.SetOperations
                for index = branches.Length - 1 downto 0 do
                    push (SemanticAfterBranch(width, branches.[index].Query))
                    push (SemanticQuery branches.[index].Query)
            | SemanticAfterBranch(expectedWidth, query) ->
                match expectedWidth with
                | Some expected ->
                    if not (hasWildcard query.Head)
                       && query.Head.Projection.Length <> expected then
                        raise (SqlCompilationException(
                            "Set operation projection width " + string query.Head.Projection.Length
                            + " does not match head projection width " + string expected + "."))
                | None -> ()
            | SemanticAfterBranches query ->
                let headWildcard = hasWildcard query.Head
                let names = projectionOutputNames query.Head
                let orders = List.toArray query.OrderBy
                for index = orders.Length - 1 downto 0 do
                    push (SemanticOrder(query, names, headWildcard, orders.[index]))
            | SemanticOrder(query, outputNames, headHasWildcard, order) ->
                match Expr.unspan order.Expression with
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
                pushExpr OrderByClause false false order.Expression

    let private validateSemanticExpr targetRuntime context insideSetFunction withinWindow expression =
        validateSemanticWalk targetRuntime
            (SemanticExpr(context, insideSetFunction, withinWindow, expression))

    let private validateSemanticTable targetRuntime source =
        validateSemanticWalk targetRuntime (SemanticTable source)

    let private validateSemanticQuery targetRuntime query =
        validateSemanticWalk targetRuntime (SemanticQuery query)

    // The EXISTS-style traversal short-circuits at the first known volatile
    // function. As before, scalar-subquery bodies are not inspected here.
    let private containsVolatileRandom expression =
        RewriteAstTraversal.exists true (function
            | FunctionCall call ->
                let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
                not (FunctionName.hasQuotedParts call.Name)
                && (name = "RAND" || name = "RANDOM")
            | _ -> false) expression

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
            update.Ctes |> List.iter (fun cte -> validateSemanticQuery targetRuntime cte.Query)
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
            delete.Ctes |> List.iter (fun cte -> validateSemanticQuery targetRuntime cte.Query)
            delete.Using |> List.iter (validateSemanticTable targetRuntime)
            delete.Where
            |> Option.iter (fun predicate ->
                rejectVolatileMutationPredicate predicate
                validateSemanticExpr targetRuntime PredicateClause false false predicate)
            delete.Returning
            |> List.iter (fun item ->
                validateSemanticExpr targetRuntime ProjectionClause false false item.Expression)
        | MergeStatement merge ->
            merge.Source.SourceValues
            |> NonEmpty.iter (validateSemanticExpr targetRuntime InsertValueClause false false)
            rejectVolatileMutationPredicate merge.MatchPredicate
            validateSemanticExpr targetRuntime PredicateClause false false merge.MatchPredicate
            merge.Matched
            |> Option.iter (function
                | MergeDelete -> ()
                | MergeUpdate assignments ->
                    assignments
                    |> NonEmpty.iter (fun item ->
                        validateSemanticExpr targetRuntime AssignmentClause false false item.Value))
            merge.NotMatched
            |> Option.iter (fun mergeInsert ->
                mergeInsert.InsertValues
                |> NonEmpty.iter (validateSemanticExpr targetRuntime InsertValueClause false false))

    let validate allowedTables targetRuntime targetProfile sourceExpressions targetExpressions sourceJoins targetJoins targetOrdering sourceDml targetDml conflictProofs canonical =
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

        targetCheck (fun () -> RewriteStructuralValidation.validateNestedCteDocument targetRuntime document)
        targetCheck (fun () -> RewriteStructuralValidation.validateNoFromDocument targetRuntime document)
        sourceCheck (fun () ->
            RewriteCapabilityValidation.proveFilterDocument sourceCapabilityMessage sourceExpressions document)
        targetCheck (fun () ->
            RewriteCapabilityValidation.proveFilterDocument targetCapabilityMessage targetExpressions document)

        let validated =
            semanticCheck (fun () ->
                validateSemanticDocument targetRuntime document
                validateDocument allowedTables document)

        targetCheck (fun () -> RewriteCapabilityValidation.proveTargetDocument targetRuntime targetProfile targetExpressions validated |> ignore)
        sourceCheck (fun () -> RewritePlanCapabilityValidation.proveJoins sourceCapabilityMessage sourceJoins validated)
        targetCheck (fun () -> RewritePlanCapabilityValidation.proveJoins targetCapabilityMessage targetJoins validated)
        targetCheck (fun () -> RewritePlanCapabilityValidation.proveOrderingAndPaging targetRuntime targetOrdering validated)
        sourceCheck (fun () -> RewritePlanCapabilityValidation.proveDml sourceCapabilityMessage sourceDml validated)
        targetCheck (fun () -> RewritePlanCapabilityValidation.proveDml targetCapabilityMessage targetDml validated)
        targetCheck (fun () -> RewritePlanCapabilityValidation.proveConflicts targetRuntime conflictProofs validated)
        ValidatedSql(validated, targetRuntime)
