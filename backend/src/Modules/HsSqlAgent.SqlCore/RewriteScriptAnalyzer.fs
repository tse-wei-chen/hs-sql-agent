namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteSchemaAnalyzer

/// Statement-order coordinator. A Catalog is simulated, not queried or
/// executed; neither successful analysis nor a known Catalog authorizes SQL.
module internal RewriteScriptAnalyzer =

    let private readOnly (items: seq<'T>) : IReadOnlyList<'T> =
        (items |> Seq.toArray |> Array.AsReadOnly) :> IReadOnlyList<'T>

    let private diagnostic (syntax: SqlScriptStatementSyntax) code category message =
        SqlDiagnostic(code, SqlDiagnosticStage.SemanticValidation, category, message, syntax.Span)

    let private changed (syntax: SqlScriptStatementSyntax) name operation certainty =
        SqlCatalogChange(name, operation, certainty, syntax.Span)

    let private changedColumn (syntax: SqlScriptStatementSyntax) name column operation =
        SqlCatalogChange(name, operation, SqlCatalogKnowledge.Known, syntax.Span, column)

    let private changedExact (syntax: SqlScriptStatementSyntax)
                             (name: SqlCatalogQualifiedName) operation certainty =
        SqlCatalogChange(
            name.DisplayName, operation, certainty, syntax.Span, null, name)

    let private changedColumnExact (syntax: SqlScriptStatementSyntax)
                                   (name: SqlCatalogQualifiedName) column operation =
        SqlCatalogChange(
            name.DisplayName, operation, SqlCatalogKnowledge.Known,
            syntax.Span, column, name)

    let private analyzeDdl sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                           (scope: RewriteCatalogSchemaResolution.SchemaScope)
                           (state: SqlCatalogState) (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "SchemaLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
            |> List.toArray
        let parsed =
            RewriteResourceBudget.withStage "SchemaParsing" SqlDiagnosticStage.Parse (fun () ->
                RewriteSchemaAnalyzer.parseForWith sourceDialect sourceProfile tokens)
        let fail message =
            let error = diagnostic syntax "SQL_SCHEMA_SEMANTIC_INVALID" SqlDiagnosticCategory.Semantic message
            SqlStatementAnalysisStatus.Invalid, SqlCatalogKnowledge.Known,
            state, ([]: SqlCatalogChange list), [error]
        let partial (name: SqlCatalogQualifiedName) operation =
            let next = RewriteCatalogModel.unknownTable state name
            SqlStatementAnalysisStatus.PartiallyAnalyzed, SqlCatalogKnowledge.Unknown,
            next, [changed syntax name.DisplayName operation SqlCatalogKnowledge.Unknown], []
        let noChange =
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            state, ([]: SqlCatalogChange list), ([]: SqlDiagnostic list)
        let exact (name: SqlCatalogQualifiedName) operation next =
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            next, [changedExact syntax name operation SqlCatalogKnowledge.Known], []
        let dependencyUnproven message =
            let unknown = RewriteCatalogModel.taintAll state
            let error =
                diagnostic syntax "SQL_SCHEMA_DEPENDENCY_UNPROVEN"
                    SqlDiagnosticCategory.Capability message
            SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
            unknown,
            [changed syntax "*" SqlCatalogOperation.Indeterminate SqlCatalogKnowledge.Unknown],
            [error]
        let unsupportedDeclaredScope =
            sourceDialect = SqlAgentToolType.Postgres
            && (match scope with
                | RewriteCatalogSchemaResolution.Unproven ->
                    not (isNull sourceProfile)
                    && not (isNull (sourceProfile.GetSessionSetting("SEARCH_PATH")))
                | _ -> false)
        let resolve = RewriteCatalogSchemaResolution.resolve scope
        let resolveConstraint (constraintInfo: SqlCatalogConstraint) =
            if isNull constraintInfo.ReferencedIdentity then constraintInfo
            else
                SqlCatalogConstraint(
                    constraintInfo.Name, constraintInfo.Kind,
                    constraintInfo.Columns,
                    resolve constraintInfo.ReferencedIdentity,
                    constraintInfo.ReferencedColumns)
        let resolvedOperation = function
            | Create(name, columns, constraints, conditional) ->
                Create(resolve name, columns, List.map resolveConstraint constraints, conditional)
            | Drop(name, conditional) -> Drop(resolve name, conditional)
            | AlterColumns(name, actions) -> AlterColumns(resolve name, actions)
            | CreateIndex(name, indexName, cols) -> CreateIndex(resolve name, indexName, cols)
            | DropIndex(indexName, target) -> DropIndex(indexName, Option.map resolve target)
            | AddConstraint(name, constraintInfo) ->
                AddConstraint(resolve name, resolveConstraint constraintInfo)
            | DropConstraint(name, constraintName) -> DropConstraint(resolve name, constraintName)
        // Inspect raw AST parts before qualifying them; a converted two-part
        // name must never hide an unsupported one-part lookup.
        let rawNames =
            match parsed with
            | Recognized operation ->
                match operation with
                | Create(name, _, constraints, _) ->
                    name :: (constraints |> List.choose (fun c ->
                        if isNull c.ReferencedIdentity then None
                        else Some c.ReferencedIdentity))
                | AddConstraint(name, constraintInfo) ->
                    [name] @
                    (if isNull constraintInfo.ReferencedIdentity then []
                     else [constraintInfo.ReferencedIdentity])
                | Drop(name, _)
                | AlterColumns(name, _)
                | CreateIndex(name, _, _)
                | DropConstraint(name, _) -> [name]
                | DropIndex(_, target) -> Option.toList target
            | _ -> []
        let rawUnqualified =
            rawNames |> List.exists (fun n -> n.Parts.Count = 1)
        let rawSystemName =
            rawNames |> List.exists (fun n ->
                n.Parts.Count = 1 &&
                n.Parts[0].StartsWith("pg_", StringComparison.OrdinalIgnoreCase))
        let parsed =
            match parsed with
            | Recognized operation -> Recognized(resolvedOperation operation)
            | other -> other

        let referencedTableName = function
            | Create(name, _, _, _)
            | Drop(name, _)
            | AlterColumns(name, _)
            | CreateIndex(name, _, _)
            | AddConstraint(name, _)
            | DropConstraint(name, _) -> Some name
            | DropIndex(_, target) -> target
        match parsed with
        | Recognized operation when
            unsupportedDeclaredScope && rawUnqualified ->
            dependencyUnproven "Declared PostgreSQL SEARCH_PATH is outside the supported single ordinary ASCII schema proof."
        | Recognized operation when
            rawSystemName &&
            (match scope with
             | RewriteCatalogSchemaResolution.SingleSchema _ -> true
             | _ -> false) ->
            dependencyUnproven "PostgreSQL pg_catalog/pg_temp implicit name resolution is not proven by a single user schema."
        | Recognized operation when
            (match scope with
             | RewriteCatalogSchemaResolution.SingleSchema _ ->
                 not (RewriteCatalogSchemaResolution.canProject scope state)
                 && (referencedTableName operation
                     |> Option.exists (fun n -> n.Parts.Count = 2))
             | _ -> false) ->
            dependencyUnproven "Declared schema cannot be applied to a Catalog containing unqualified or system-schema identities."
        | Recognized operation when
            referencedTableName operation
            |> Option.exists (RewriteCatalogModel.hasUnqualifiedScopeConflict state) ->
            dependencyUnproven
                "Default schema/search_path is not modeled; unqualified table name may resolve to an existing qualified table."
        | Recognized _ when
            RewriteCatalogIdentifierProof.mode sourceDialect sourceProfile =
                RewriteCatalogIdentifierProof.FoldAscii
            && not (RewriteCatalogIdentifierProof.isAsciiSql syntax.Text) ->
            dependencyUnproven "Identifier names outside the ASCII-safe provider proof subset cannot establish a known DDL Catalog."
        | Invalid message -> fail message
        | Unsupported ->
            let unknown = RewriteCatalogModel.taintAll state
            let error =
                diagnostic syntax "SQL_SCHEMA_UNSUPPORTED_STATEMENT"
                    SqlDiagnosticCategory.Capability
                    "Source dialect DDL is outside the statically proven TABLE subset; catalog facts are invalidated."
            SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
            unknown, [changed syntax "*" SqlCatalogOperation.Indeterminate SqlCatalogKnowledge.Unknown], [error]
        | Recognized (Create(name, columns, constraints, ifNotExists)) ->
            match RewriteCatalogModel.lookup state name with
            | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known && ifNotExists ->
                noChange
            | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known ->
                fail ("CREATE TABLE " + name.DisplayName + " already exists in the known catalog.")
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                match RewriteSchemaDependencies.validateCreate sourceDialect sourceProfile state name columns constraints with
                | RewriteSchemaDependencies.Proven ->
                    RewriteCatalogModel.knownTableWithConstraints state name columns constraints
                    |> exact name SqlCatalogOperation.CreateTable
                | RewriteSchemaDependencies.Invalid message -> fail message
                | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
            | _ -> partial name SqlCatalogOperation.CreateTable
        | Recognized (Drop(name, ifExists)) ->
            match RewriteCatalogModel.lookup state name with
            | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known ->
                match RewriteSchemaDependencies.canDropTable state name with
                | RewriteSchemaDependencies.Proven ->
                    RewriteCatalogModel.dropKnown state name
                    |> exact name SqlCatalogOperation.DropTable
                | RewriteSchemaDependencies.Invalid message -> fail message
                | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
            | _ when RewriteCatalogModel.isKnownAbsent state name && ifExists ->
                noChange
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                fail ("DROP TABLE " + name.DisplayName + " does not exist in the known catalog.")
            | _ -> partial name SqlCatalogOperation.DropTable
        | Recognized (CreateIndex(tableName, indexName, members)) ->
            match RewriteCatalogModel.lookup state tableName with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known
                              && state.IsClosedWorld ->
                let globalIndex =
                    state.Tables
                    |> Seq.collect (fun t -> t.Indexes)
                    |> Seq.exists (fun idx ->
                        String.Equals(idx.Name, indexName, StringComparison.Ordinal))
                if globalIndex then
                    fail ("CREATE INDEX '" + indexName + "' already exists in the known Catalog.")
                elif state.Tables |> Seq.exists (fun t -> t.Constraints.Count > 0) then
                    dependencyUnproven "Implicit constraint-backed index names are not modeled; index namespace uniqueness cannot be proven."
                elif members.IsEmpty || List.distinct members <> members then
                    fail "CREATE INDEX has duplicate or empty column members."
                elif members |> List.exists (fun memberName ->
                    not (table.Columns |> Seq.exists (fun col ->
                        String.Equals(col.Name, memberName, StringComparison.Ordinal)))) then
                    fail "CREATE INDEX references an absent column."
                else
                    let idx = SqlCatalogIndex(indexName, Array.AsReadOnly(List.toArray members))
                    let updated =
                        RewriteCatalogModel.withIndexes state tableName
                            (seq { yield! table.Indexes; yield idx })
                    exact tableName SqlCatalogOperation.CreateIndex updated
            | _ -> dependencyUnproven "Index target is not a proven table in a closed Catalog."
        | Recognized (DropIndex(indexName, target)) ->
            if not state.IsClosedWorld ||
               (state.Tables |> Seq.exists (fun t -> t.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Index owner and absence require a complete known Catalog."
            else
                let owners =
                    state.Tables |> Seq.filter (fun t ->
                        let matchesScope =
                            match scope, target with
                            | RewriteCatalogSchemaResolution.SingleSchema schema, None ->
                                t.QualifiedName.Parts.Count = 2
                                && String.Equals(t.QualifiedName.Parts[0], schema, StringComparison.Ordinal)
                            | _ -> true
                        let matchesTarget =
                            match target with
                            | Some tableName ->
                                String.Equals(tableName.Key, t.QualifiedName.Key, StringComparison.Ordinal)
                            | None -> true
                        matchesScope && matchesTarget
                        && (t.Indexes |> Seq.exists (fun idx ->
                            String.Equals(idx.Name, indexName, StringComparison.Ordinal))))
                    |> Seq.toList
                match owners with
                | [owner] ->
                    let remaining =
                        owner.Indexes |> Seq.filter (fun idx ->
                            not (String.Equals(idx.Name, indexName, StringComparison.Ordinal)))
                    RewriteCatalogModel.withIndexes state owner.QualifiedName remaining
                    |> exact owner.QualifiedName SqlCatalogOperation.DropIndex
                | [] ->
                    dependencyUnproven
                        ("DROP INDEX '" + indexName +
                         "' is not a tracked standalone index; implicit constraint indexes cannot be ruled out.")
                | _ -> dependencyUnproven "Index name resolves to more than one modeled owner."
        | Recognized (AddConstraint(tableName, newConstraint)) ->
            match RewriteCatalogModel.lookup state tableName with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                match RewriteSchemaDependencies.validateNewConstraint sourceDialect sourceProfile state
                          tableName newConstraint with
                | RewriteSchemaDependencies.Proven ->
                    let updated =
                        RewriteCatalogModel.withConstraints state tableName
                            (seq { yield! table.Constraints; yield newConstraint })
                    exact tableName SqlCatalogOperation.AddConstraint updated
                | RewriteSchemaDependencies.Invalid message -> fail message
                | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
            | _ -> dependencyUnproven "Cannot establish the table's known constraint set."
        | Recognized (DropConstraint(tableName, constraintName)) ->
            match RewriteCatalogModel.lookup state tableName with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                match table.Constraints |> Seq.tryFind (fun item ->
                    String.Equals(item.Name, constraintName, StringComparison.Ordinal)) with
                | None ->
                    fail ("DROP CONSTRAINT '" + constraintName + "' does not exist.")
                | Some target ->
                    match RewriteSchemaDependencies.canDropConstraint state tableName target with
                    | RewriteSchemaDependencies.Proven ->
                        RewriteCatalogModel.withConstraints state tableName
                            (table.Constraints |> Seq.filter (fun c ->
                                not (String.Equals(c.Name, constraintName, StringComparison.Ordinal))))
                        |> exact tableName SqlCatalogOperation.DropConstraint
                    | RewriteSchemaDependencies.Invalid message -> fail message
                    | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
            | _ -> dependencyUnproven "Constraint owner is not a proven table."
        | Recognized (AlterColumns(name, actions)) ->
            match RewriteCatalogModel.lookup state name with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                // Transactional *analysis* within one ALTER statement: stage
                // every operation in local working state, then publish a single
                // immutable snapshot. If any action fails, publish no deltas.
                let working = ResizeArray<SqlCatalogColumn>(table.Columns)
                let pending = ResizeArray<SqlCatalogChange>()
                let mutable failure: string option = None
                let mutable unknownDependency: string option = None
                let locate column =
                    working |> Seq.tryFindIndex (fun c ->
                        String.Equals(c.Name, column, StringComparison.Ordinal))
                for action in actions do
                    RewriteResourceBudget.checkpoint ()
                    if failure.IsNone then
                        match action with
                        | Add column ->
                            if locate column.Name |> Option.isSome then
                                failure <- Some(
                                    "ALTER TABLE " + name.DisplayName + " column " + column.Name +
                                    " already exists.")
                            else
                                working.Add column
                                pending.Add(changedColumnExact syntax name column.Name SqlCatalogOperation.AddColumn)
                        | Remove(columnName, ifExists) ->
                            match locate columnName with
                            | Some index when working.Count = 1 ->
                                failure <- Some(
                                    "ALTER TABLE " + name.DisplayName + " cannot drop its last column.")
                            | Some index ->
                                match RewriteSchemaDependencies.canDropColumn state name columnName with
                                | RewriteSchemaDependencies.Proven ->
                                    working.RemoveAt index
                                    pending.Add(changedColumnExact syntax name columnName SqlCatalogOperation.DropColumn)
                                | RewriteSchemaDependencies.Invalid message ->
                                    failure <- Some message
                                | RewriteSchemaDependencies.Unproven message ->
                                    unknownDependency <- Some message
                            | None when ifExists -> ()
                            | None ->
                                failure <- Some(
                                    "ALTER TABLE " + name.DisplayName + " column " + columnName +
                                    " does not exist.")
                match failure, unknownDependency with
                | Some message, _ -> fail message
                | None, Some message -> dependencyUnproven message
                | None, None when pending.Count = 0 -> noChange
                | None, None ->
                    let updated = RewriteCatalogModel.knownTable state name working
                    SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                    updated, (pending |> Seq.toList), []
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                fail ("ALTER TABLE " + name.DisplayName + " does not exist in the known catalog.")
            | _ ->
                // The referenced table/columns are not proven. A composite
                // ALTER cannot claim its individual mutations succeeded.
                partial name SqlCatalogOperation.Indeterminate

    let analyze sql sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null) (initial: SqlCatalogState) =
        if Object.ReferenceEquals(initial, null) then nullArg "initialCatalog"
        RewriteResourceBudget.withCompilation (fun () ->
            let parsed = RewriteScriptParser.parse sql sourceDialect sourceProfile
            let results = ResizeArray<SqlStatementAnalysis>()
            let errors = ResizeArray<SqlDiagnostic>()
            let mutable current = initial
            let mutable transaction = RewriteTransactionAnalyzer.Outside
            let mutable session = RewriteSessionAnalyzer.initial sourceDialect sourceProfile
            let mutable transactionSession : RewriteSessionAnalyzer.SessionContext option = None

            RewriteResourceBudget.withStage "ScriptAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                for syntax in parsed.Statements do
                    RewriteResourceBudget.checkpoint ()
                    let before = current
                    let transactionBefore = RewriteTransactionAnalyzer.status transaction
                    let sessionBefore = RewriteSessionAnalyzer.snapshot session
                    let mutable securityIntents : SqlSecurityIntent list = []
                    let status, effects, certainty, bindingStatus, nameStatus, typeStatus, next, changes, diagnostics =
                        if syntax.Status = SqlScriptParseStatus.Invalid then
                            SqlStatementAnalysisStatus.Invalid, SqlEffectKind.None,
                            SqlCatalogKnowledge.Unknown, SqlCatalogBindingStatus.Rejected,
                            SqlCatalogBindingStatus.Rejected, SqlCatalogBindingStatus.NotApplicable, current, [],
                            (if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                             else [syntax.Diagnostic])
                        elif syntax.Kind = SqlScriptStatementKind.Transaction then
                            let status, certainty, next, newTransaction, changes, diagnostics =
                                RewriteTransactionAnalyzer.analyze sourceDialect sourceProfile current transaction syntax
                            transaction <- newTransaction
                            if status = SqlStatementAnalysisStatus.Analyzed then
                                match transactionBefore, RewriteTransactionAnalyzer.status newTransaction with
                                | SqlTransactionState.Outside, SqlTransactionState.Active ->
                                    transactionSession <- Some session
                                | SqlTransactionState.Active, SqlTransactionState.Outside ->
                                    // Only recognized ROLLBACK reaches this branch.
                                    if syntax.Text.TrimStart().StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase) then
                                        session <- Option.defaultValue RewriteSessionAnalyzer.unknown transactionSession
                                    transactionSession <- None
                                | _ -> ()
                            else
                                session <- RewriteSessionAnalyzer.unknown
                                transactionSession <- None
                            let effects =
                                if changes.IsEmpty then SqlEffectKind.ChangeTransaction
                                else SqlEffectKind.ChangeTransaction ||| SqlEffectKind.ChangeSchema
                            status, effects, certainty, SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable, SqlCatalogBindingStatus.NotApplicable,
                            next, changes, diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Security then
                            let status, intents, diagnostics =
                                RewriteResourceBudget.withStage "SecurityAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                                    RewriteSecurityAnalyzer.analyze sourceDialect sourceProfile syntax)
                            securityIntents <- intents
                            status, SqlEffectKind.ChangePermissions, SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            current, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Session then
                            let status, certainty, nextSession, nextCatalog, diagnostics =
                                RewriteSessionAnalyzer.analyze sourceDialect sourceProfile session current syntax
                            session <- nextSession
                            status, SqlEffectKind.ChangeSession, certainty,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            nextCatalog, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Schema then
                            let status, certainty, next, changes, diagnostics =
                                analyzeDdl sourceDialect sourceProfile (RewriteSessionAnalyzer.scope session) current syntax
                            status, SqlEffectKind.ChangeSchema, certainty, SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable, SqlCatalogBindingStatus.NotApplicable,
                            next, changes, diagnostics
                        else
                            let effects =
                                match syntax.Kind with
                                | SqlScriptStatementKind.Query -> SqlEffectKind.ReadData
                                | SqlScriptStatementKind.Mutation -> SqlEffectKind.WriteData
                                | SqlScriptStatementKind.Security -> SqlEffectKind.ChangePermissions
                                | SqlScriptStatementKind.Transaction -> SqlEffectKind.ChangeTransaction
                                | SqlScriptStatementKind.Session -> SqlEffectKind.ChangeSession
                                | SqlScriptStatementKind.Utility -> SqlEffectKind.Administrative
                                | _ -> SqlEffectKind.None
                            let bindingStatus, nameStatus, typeStatus, status, certainty, catalogErrors =
                                if syntax.Status = SqlScriptParseStatus.Parsed
                                   && (syntax.Kind = SqlScriptStatementKind.Query
                                       || syntax.Kind = SqlScriptStatementKind.Mutation) then
                                    RewriteCatalogBinding.verify sourceDialect sourceProfile
                                        (RewriteSessionAnalyzer.scope session) current syntax
                                else
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlStatementAnalysisStatus.Unsupported,
                                    SqlCatalogKnowledge.Unknown,
                                    []
                            let next =
                                if syntax.Kind = SqlScriptStatementKind.Procedural
                                   || syntax.Kind = SqlScriptStatementKind.Unknown then
                                    RewriteCatalogModel.taintAll current
                                else current
                            let diagnostics =
                                let parserDiagnostics =
                                    if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                                    else [syntax.Diagnostic]
                                parserDiagnostics @ catalogErrors
                            status, effects, certainty, bindingStatus, nameStatus, typeStatus, next, [], diagnostics
                    let nextTransaction =
                        RewriteTransactionAnalyzer.invalidateIfUnproven transaction syntax.Kind status
                    let stateBecameUnknown =
                        transactionBefore = SqlTransactionState.Active
                        && RewriteTransactionAnalyzer.status nextTransaction = SqlTransactionState.Unknown
                    transaction <- nextTransaction
                    if stateBecameUnknown then
                        session <- RewriteSessionAnalyzer.unknown
                        transactionSession <- None
                    let after =
                        if stateBecameUnknown then RewriteCatalogModel.taintAll next else next
                    let additionalErrors =
                        if stateBecameUnknown then
                            [diagnostic syntax "SQL_TRANSACTION_CONTEXT_UNPROVEN" SqlDiagnosticCategory.Capability
                                "An unproven statement or failure has invalidated the active transaction and Catalog proofs."]
                        else []
                    let allDiagnostics = diagnostics @ additionalErrors
                    current <- after
                    for item in allDiagnostics do errors.Add item
                    let detailedEffects, effectCoverage =
                        RewriteResourceBudget.withStage "ScriptEffects" SqlDiagnosticStage.SemanticValidation (fun () ->
                            RewriteStatementEffects.describe sourceDialect sourceProfile syntax
                                status certainty effects (readOnly changes) (readOnly securityIntents))
                    results.Add(
                        SqlStatementAnalysis(
                            syntax, status, effects,
                            (if stateBecameUnknown then SqlCatalogKnowledge.Unknown else certainty),
                            bindingStatus, nameStatus, typeStatus, before, after,
                            transactionBefore, RewriteTransactionAnalyzer.status transaction,
                            sessionBefore, RewriteSessionAnalyzer.snapshot session,
                            readOnly changes, detailedEffects, effectCoverage,
                            readOnly securityIntents, readOnly allDiagnostics)))
            SqlScriptAnalysisResult(
                readOnly results, readOnly errors, current,
                RewriteTransactionAnalyzer.status transaction,
                RewriteSessionAnalyzer.snapshot session))
