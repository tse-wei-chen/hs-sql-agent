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

    type private BatchIndexResolution =
        | BatchIndexFound of SqlCatalogQualifiedName * string
        | BatchIndexAbsent
        | BatchIndexInvalid of string
        | BatchIndexUnproven of string

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
            // A locally invalid DDL is rejected regardless of prior schema
            // state, but rejection must never promote an incomplete Catalog
            // into a known, closed-world proof. Keep a known rejection only
            // when the caller's current Catalog itself is completely known.
            let certainty =
                if state.Knowledge = SqlCatalogKnowledge.Known then
                    SqlCatalogKnowledge.Known
                else SqlCatalogKnowledge.Unknown
            SqlStatementAnalysisStatus.Invalid, certainty,
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
            | DropTableBatch(names, conditional) ->
                DropTableBatch(List.map resolve names, conditional)
            | AlterColumns(name, actions) -> AlterColumns(resolve name, actions)
            | CreateIndex(name, indexName, cols) -> CreateIndex(resolve name, indexName, cols)
            | DropIndex(indexName, target, conditional) ->
                DropIndex(indexName, Option.map resolve target, conditional)
            | DropQualifiedIndex(indexIdentity, conditional) ->
                DropQualifiedIndex(indexIdentity, conditional)
            | DropIndexBatch(identities, conditional) ->
                DropIndexBatch(identities, conditional)
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
                | DropIndex(_, target, _) -> Option.toList target
                | DropQualifiedIndex(indexIdentity, _) -> [indexIdentity]
                | DropIndexBatch(identities, _) -> identities
                | DropTableBatch(names, _) -> names
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
            | DropIndex(_, target, _) -> target
            | DropQualifiedIndex _
            | DropIndexBatch _
            | DropTableBatch _ -> None
        // Indexes in PostgreSQL belong to their parent table's schema.
        // A caller-supplied one-part Catalog identity has no proven
        // physical schema: it can therefore collide with *any* explicit
        // schema and must never be filtered out by a qualified target.
        let possiblySameIndexSchema (left: SqlCatalogQualifiedName)
                                    (right: SqlCatalogQualifiedName) =
            sourceDialect <> SqlAgentToolType.Postgres
            || left.Parts.Count <> 2
            || right.Parts.Count <> 2
            || String.Equals(left.Parts[0], right.Parts[0], StringComparison.Ordinal)
        let ambiguousIndexSchema (left: SqlCatalogQualifiedName)
                                 (right: SqlCatalogQualifiedName) =
            // Keep existing conservative equality among two unqualified
            // Catalog owners; mixing an explicit and an unqualified
            // owner cannot establish a definite physical schema match.
            sourceDialect = SqlAgentToolType.Postgres
            && ((left.Parts.Count = 2) <> (right.Parts.Count = 2))
        let relationName (name: SqlCatalogQualifiedName) =
            name.Parts[name.Parts.Count - 1]
        match parsed with
        | Recognized (DropTableBatch _) when
            rawUnqualified &&
            not (match scope with
                 | RewriteCatalogSchemaResolution.SingleSchema _ ->
                     RewriteCatalogSchemaResolution.canProject scope state
                 | _ -> false) ->
            dependencyUnproven "Batch DROP TABLE requires a proven single search_path for unqualified members."
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
             | RewriteCatalogSchemaResolution.SingleSchema _
             | RewriteCatalogSchemaResolution.SingleDatabase _ ->
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
            // PostgreSQL tables and indexes share the schema-local
            // relation namespace. A virtual Catalog that knows an index
            // with this name cannot simultaneously prove a new table.
            // Unknown owner schema requires an Unproven result, not a
            // confident declaration of a conflicting physical relation.
            let existingIndexOwner =
                if sourceDialect = SqlAgentToolType.Postgres then
                    state.Tables
                    |> Seq.tryFind (fun owner ->
                        possiblySameIndexSchema name owner.QualifiedName
                        && (owner.Indexes |> Seq.exists (fun idx ->
                            String.Equals(idx.Name, relationName name, StringComparison.Ordinal))))
                else None
            match existingIndexOwner with
            | Some owner when ambiguousIndexSchema name owner.QualifiedName ->
                dependencyUnproven "Index owner schema is not proven; CREATE TABLE may collide with an existing index relation."
            | Some _ when ifNotExists ->
                // PostgreSQL IF NOT EXISTS skips creation if *any*
                // relation (not only a table) has the requested name.
                noChange
            | Some _ ->
                fail ("CREATE TABLE " + name.DisplayName + " conflicts with a known index relation in its schema.")
            | None ->
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
        | Recognized (DropTableBatch(names, conditional)) ->
            // Batch names are fully parsed and scope-resolved before this
            // branch. Examine every target against the same ORIGINAL
            // Catalog; independent single-table DROP validations would
            // incorrectly block an FK between two tables dropped together.
            let distinctNames =
                names |> List.distinctBy (fun name -> name.Key)
            let duplicate = distinctNames.Length <> names.Length
            let ambiguousOwner =
                state.Tables |> Seq.exists (fun owner ->
                    owner.QualifiedName.Parts.Count = 1
                    && (names |> List.exists (fun name ->
                        String.Equals(
                            relationName name,
                            relationName owner.QualifiedName,
                            StringComparison.Ordinal))))
            if sourceDialect <> SqlAgentToolType.Postgres
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun table ->
                   table.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Batch DROP TABLE requires a complete known PostgreSQL Catalog."
            elif ambiguousOwner then
                dependencyUnproven "Unqualified Catalog identities may shadow schema-qualified DROP TABLE targets."
            elif duplicate then
                fail "DROP TABLE batch contains duplicate physical table identities."
            else
                let existing =
                    names |> List.choose (fun name ->
                        match RewriteCatalogModel.lookup state name with
                        | Some _ -> Some name
                        | None -> None)
                let missing =
                    names |> List.tryFind (fun name ->
                        (RewriteCatalogModel.lookup state name).IsNone)
                let sameSchema (left: SqlCatalogQualifiedName)
                               (right: SqlCatalogQualifiedName) =
                    left.Parts.Count = 2
                    && right.Parts.Count = 2
                    && String.Equals(left.Parts[0], right.Parts[0], StringComparison.Ordinal)
                let wrongKind =
                    names |> List.tryFind (fun name ->
                        (RewriteCatalogModel.lookup state name).IsNone
                        && (state.Tables |> Seq.exists (fun owner ->
                            sameSchema name owner.QualifiedName
                            && (owner.Indexes |> Seq.exists (fun index ->
                                String.Equals(index.Name, relationName name,
                                              StringComparison.Ordinal)))))
                // All inbound FKs whose referencing table is also being
                // dropped disappear in the same PostgreSQL DDL statement.
                // A dependency originating outside this set blocks the
                // entire RESTRICT operation; do not remove any table first.
                let targets =
                    names |> List.map (fun name -> name.Key) |> Set.ofList
                let externalForeignKey =
                    existing |> List.tryPick (fun target ->
                        RewriteCatalogModel.inboundForeignKeys state target
                        |> Seq.tryFind (fun (owner, _) ->
                            not (Set.contains owner.QualifiedName.Key targets))
                        |> Option.map (fun (owner, _) -> target, owner))
                match wrongKind, missing, externalForeignKey with
                | Some name, _, _ ->
                    fail ("DROP TABLE " + name.DisplayName +
                          " names a tracked index relation, not a table.")
                | None, Some name, _ when not conditional ->
                    fail ("DROP TABLE " + name.DisplayName +
                          " does not exist in the known Catalog.")
                | None, _, Some(target, dependent) ->
                    fail ("DROP TABLE " + target.DisplayName +
                          " is blocked by a FOREIGN KEY in non-batch table '" +
                          dependent.Name + "'.")
                | _ ->
                    // Only after the full proof succeeds do we mutate the
                    // local persistent state and publish exact deltas.
                    let next =
                        existing |> List.fold (fun catalog name ->
                            RewriteCatalogModel.dropKnown catalog name) state
                    let deltas =
                        existing |> List.map (fun name ->
                            changedExact syntax name SqlCatalogOperation.DropTable
                                SqlCatalogKnowledge.Known)
                    SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                    next, deltas, ([]: SqlDiagnostic list)
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
                // MySQL / SQL Server: owning table namespace.
                // PostgreSQL: parent table's schema-local relation namespace.
                // Unqualified PostgreSQL identities remain potentially in
                // every schema, so only two explicit *different* schemas may
                // be excluded from collision and implicit-index checks.
                // SQLite / Oracle / Firebird keep the original conservative
                // Catalog-wide namespace.
                let tableLocalNamespace =
                    sourceDialect = SqlAgentToolType.MySQL
                    || sourceDialect = SqlAgentToolType.MsSqlServer
                let indexNamespace : seq<SqlCatalogTable> =
                    if tableLocalNamespace then Seq.singleton table
                    elif sourceDialect = SqlAgentToolType.Postgres then
                        state.Tables
                        |> Seq.filter (fun owner ->
                            possiblySameIndexSchema tableName owner.QualifiedName)
                    else state.Tables :> seq<SqlCatalogTable>
                let indexOwner =
                    indexNamespace
                    |> Seq.tryFind (fun owner ->
                        owner.Indexes |> Seq.exists (fun idx ->
                            String.Equals(idx.Name, indexName, StringComparison.Ordinal)))
                // PostgreSQL indexes also conflict with ordinary tables
                // named the same within the target schema.
                let relationOwner =
                    if sourceDialect = SqlAgentToolType.Postgres then
                        indexNamespace |> Seq.tryFind (fun owner ->
                            String.Equals(relationName owner.QualifiedName, indexName,
                                          StringComparison.Ordinal))
                    else None
                match indexOwner, relationOwner with
                | Some owner, _ when ambiguousIndexSchema tableName owner.QualifiedName ->
                    dependencyUnproven "Index owner has no proven schema; index name collision cannot be resolved."
                | Some _, _ ->
                    fail ("CREATE INDEX '" + indexName + "' already exists in the known Catalog.")
                | None, Some owner when ambiguousIndexSchema tableName owner.QualifiedName ->
                    dependencyUnproven "Relation has no proven schema; index name may collide with an existing table."
                | None, Some _ ->
                    fail ("CREATE INDEX '" + indexName + "' conflicts with a known table relation in its schema.")
                | None, None when indexNamespace |> Seq.exists (fun t -> t.Constraints.Count > 0) ->
                    dependencyUnproven "Implicit constraint-backed index names are not modeled; index namespace uniqueness cannot be proven."
                | None, None ->
                    if members.IsEmpty || List.distinct members <> members then
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
        | Recognized (DropIndexBatch(identities, conditional)) ->
            // This statement must never commit a virtual Catalog delta
            // before ALL requested index relations have been resolved.
            // Resolve structured identities first; compare fully qualified
            // physical keys, not dotted display-name strings.
            let resolved = identities |> List.map (resolve)
            let duplicate =
                (resolved |> List.distinctBy (fun identity -> identity.Key) |> List.length)
                <> List.length resolved
            if sourceDialect <> SqlAgentToolType.Postgres
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun table ->
                   table.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "A multi-index DROP requires a complete PostgreSQL Catalog."
            elif duplicate then
                fail "DROP INDEX batch contains duplicate index identities."
            else
                let prove (identity: SqlCatalogQualifiedName) =
                    let schemaAndName =
                        if identity.Parts.Count = 2 then
                            Some(identity.Parts[0], identity.Parts[1])
                        else
                            match scope with
                            | RewriteCatalogSchemaResolution.SingleSchema schema when
                                RewriteCatalogSchemaResolution.canProject scope state ->
                                Some(schema, identity.Parts[0])
                            | _ -> None
                    match schemaAndName with
                    | None ->
                        BatchIndexUnproven
                            "Multi-index DROP unqualified names require a proven single search_path."
                    | Some(schema, indexName) ->
                        let sameIndex (index: SqlCatalogIndex) =
                            String.Equals(index.Name, indexName, StringComparison.Ordinal)
                        let inSchema (owner: SqlCatalogTable) =
                            owner.QualifiedName.Parts.Count = 2
                            && String.Equals(owner.QualifiedName.Parts[0], schema,
                                             StringComparison.Ordinal)
                        let ambiguousOwner =
                            state.Tables |> Seq.exists (fun owner ->
                                owner.QualifiedName.Parts.Count = 1
                                && ((owner.Indexes |> Seq.exists sameIndex)
                                    || owner.Constraints.Count > 0
                                    || String.Equals(relationName owner.QualifiedName,
                                                     indexName, StringComparison.Ordinal)))
                        let possibleImplicit =
                            state.Tables |> Seq.exists (fun owner ->
                                inSchema owner && owner.Constraints.Count > 0)
                        let namedTable =
                            state.Tables |> Seq.exists (fun owner ->
                                inSchema owner
                                && String.Equals(relationName owner.QualifiedName,
                                                 indexName, StringComparison.Ordinal))
                        let matchingOwners =
                            state.Tables |> Seq.filter (fun owner ->
                                inSchema owner && (owner.Indexes |> Seq.exists sameIndex))
                            |> Seq.toList
                        if ambiguousOwner then
                            BatchIndexUnproven
                                ("Index '" + identity.DisplayName +
                                 "' may belong to an unqualified Catalog owner.")
                        elif possibleImplicit then
                            BatchIndexUnproven
                                ("Implicit constraint-backed index identity '" +
                                 identity.DisplayName + "' cannot be proven.")
                        elif namedTable then
                            BatchIndexInvalid
                                ("DROP INDEX '" + identity.DisplayName +
                                 "' names a known table relation.")
                        else
                            match matchingOwners with
                            | [owner] -> BatchIndexFound(owner.QualifiedName, indexName)
                            | [] when conditional -> BatchIndexAbsent
                            | [] ->
                                BatchIndexUnproven
                                    ("DROP INDEX '" + identity.DisplayName +
                                     "' is not a proven standalone index.")
                            | _ ->
                                BatchIndexUnproven
                                    ("DROP INDEX '" + identity.DisplayName +
                                     "' resolves to multiple Catalog owners.")
                // All proofs are evaluated over the ORIGINAL immutable
                // Catalog, and no mutation occurs until all have succeeded.
                let proofs = identities |> List.map prove
                let invalid =
                    proofs |> List.tryPick (function
                        | BatchIndexInvalid message -> Some message
                        | _ -> None)
                let unproven =
                    proofs |> List.tryPick (function
                        | BatchIndexUnproven message -> Some message
                        | _ -> None)
                match invalid, unproven with
                | Some message, _ -> fail message
                | None, Some message -> dependencyUnproven message
                | None, None ->
                    let removals =
                        proofs |> List.choose (function
                            | BatchIndexFound(owner, indexName) -> Some(owner, indexName)
                            | _ -> None)
                    // A single statement publishes every exact delta or
                    // none. Two indexes may belong to the same table: read
                    // the updated table from the local fold state.
                    let next =
                        removals |> List.fold (fun catalog (owner, indexName) ->
                            let current = RewriteCatalogModel.lookup catalog owner |> Option.get
                            let remaining =
                                current.Indexes |> Seq.filter (fun index ->
                                    not (String.Equals(index.Name, indexName,
                                                       StringComparison.Ordinal)))
                            RewriteCatalogModel.withIndexes catalog owner remaining
                        ) state
                    let deltas =
                        removals |> List.map (fun (owner, _) ->
                            changedExact syntax owner SqlCatalogOperation.DropIndex
                                SqlCatalogKnowledge.Known)
                    SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                    next, deltas, ([]: SqlDiagnostic list)
        | Recognized (DropQualifiedIndex(indexIdentity, conditional)) ->
            // Index identity and table identity are separate structured
            // names. A qualified index cannot inherit current search_path
            // or be projected into a synthetic table name.
            let schema = indexIdentity.Parts[0]
            let indexName = indexIdentity.Parts[1]
            let inSchema (table: SqlCatalogTable) =
                table.QualifiedName.Parts.Count = 2
                && String.Equals(table.QualifiedName.Parts[0], schema, StringComparison.Ordinal)
            let sameIndex (index: SqlCatalogIndex) =
                String.Equals(index.Name, indexName, StringComparison.Ordinal)
            if sourceDialect <> SqlAgentToolType.Postgres
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun t ->
                   t.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Qualified index requires a complete PostgreSQL Catalog."
            elif state.Tables |> Seq.exists (fun t ->
                t.QualifiedName.Parts.Count = 1
                && ((t.Indexes |> Seq.exists sameIndex) || t.Constraints.Count > 0)) then
                dependencyUnproven "An unqualified Catalog owner may carry the specified schema's index or implicit index."
            else
                let matchingOwners =
                    state.Tables
                    |> Seq.filter (fun t ->
                        inSchema t && (t.Indexes |> Seq.exists sameIndex))
                    |> Seq.toList
                let namedTable =
                    state.Tables
                    |> Seq.exists (fun t ->
                        inSchema t
                        && String.Equals(relationName t.QualifiedName, indexName,
                                         StringComparison.Ordinal))
                let possibleImplicitIndex =
                    state.Tables
                    |> Seq.exists (fun t -> inSchema t && t.Constraints.Count > 0)
                match matchingOwners with
                | [owner] when not namedTable && not possibleImplicitIndex ->
                    let remaining = owner.Indexes |> Seq.filter (sameIndex >> not)
                    RewriteCatalogModel.withIndexes state owner.QualifiedName remaining
                    |> exact owner.QualifiedName SqlCatalogOperation.DropIndex
                | [] when namedTable ->
                    // IF EXISTS does not license a wrong-kind relation.
                    fail ("DROP INDEX " + indexIdentity.DisplayName +
                          " names a known table relation, not an index.")
                | [] when conditional && not possibleImplicitIndex ->
                    noChange
                | [] ->
                    dependencyUnproven "Qualified index owner or absence cannot be proven against implicit indexes."
                | _ ->
                    dependencyUnproven "Qualified index identity has conflicting or unproven Catalog owners."
        | Recognized (DropIndex(indexName, None, true)) ->
            // IF EXISTS must not promote absence from an arbitrary SQL
            // search_path. The modeled single-schema session premise
            // provides the only safe name-resolution scope for this
            // unqualified conditional form. Caller-supplied unqualified
            // Catalog owners defeat that projection.
            let scopedSchema =
                match scope with
                | RewriteCatalogSchemaResolution.SingleSchema schema when
                    RewriteCatalogSchemaResolution.canProject scope state ->
                    Some schema
                | _ -> None
            if sourceDialect <> SqlAgentToolType.Postgres
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun t ->
                   t.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Unqualified IF EXISTS requires a fully known PostgreSQL Catalog."
            else
                match scopedSchema with
                | None ->
                    dependencyUnproven "Unqualified IF EXISTS requires a proven single PostgreSQL search_path schema."
                | Some schema ->
                    let inScope (owner: SqlCatalogTable) =
                        owner.QualifiedName.Parts.Count = 2
                        && String.Equals(owner.QualifiedName.Parts[0], schema,
                                         StringComparison.Ordinal)
                    let matchesIndex (idx: SqlCatalogIndex) =
                        String.Equals(idx.Name, indexName, StringComparison.Ordinal)
                    let possibleImplicit =
                        state.Tables |> Seq.exists (fun owner ->
                            inScope owner && owner.Constraints.Count > 0)
                    let namedTable =
                        state.Tables |> Seq.exists (fun owner ->
                            inScope owner
                            && String.Equals(relationName owner.QualifiedName,
                                             indexName, StringComparison.Ordinal))
                    let matchingOwners =
                        state.Tables |> Seq.filter (fun owner ->
                            inScope owner && (owner.Indexes |> Seq.exists matchesIndex))
                        |> Seq.toList
                    // A modeled non-unique standalone index cannot be
                    // mistaken for an unmodeled PK / UNIQUE backing index.
                    // Reject the whole proof when implicit names are
                    // possible instead of guessing the collision behavior.
                    if possibleImplicit then
                        dependencyUnproven "Implicit constraint-backed index names in search_path are not proven."
                    elif namedTable then
                        fail ("DROP INDEX '" + indexName +
                              "' names a known table relation, not an index.")
                    else
                        match matchingOwners with
                        | [owner] ->
                            let remaining =
                                owner.Indexes |> Seq.filter (matchesIndex >> not)
                            RewriteCatalogModel.withIndexes state owner.QualifiedName remaining
                            |> exact owner.QualifiedName SqlCatalogOperation.DropIndex
                        | [] -> noChange
                        | _ ->
                            dependencyUnproven "Unqualified index identity has conflicting proven owners."
        | Recognized (DropIndex(indexName, target, _)) ->
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
                | [owner] when sourceDialect = SqlAgentToolType.Postgres
                               && Option.isNone target
                               && owner.QualifiedName.IsQualified
                               && (match scope with
                                   | RewriteCatalogSchemaResolution.Unproven -> true
                                   | _ -> false) ->
                    // A sole known qualified Catalog owner does not
                    // prove that unqualified DROP INDEX resolves there:
                    // actual search_path could point elsewhere.
                    dependencyUnproven "Unqualified DROP INDEX requires a proven search_path for its qualified Catalog owner."
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

            RewriteResourceBudget.withStage "ScriptAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                for syntax in parsed.Statements do
                    RewriteResourceBudget.checkpoint ()
                    let before = current
                    let transactionBefore = RewriteTransactionAnalyzer.status transaction
                    let sessionBefore = RewriteSessionAnalyzer.snapshot session
                    let mutable securityIntents : SqlSecurityIntent list = []
                    let mutable securityDependencies : SqlSecurityDependency list = []
                    let mutable utilityIntents : SqlUtilityIntent list = []
                    let mutable proceduralIntents : SqlProceduralIntent list = []
                    let mutable proceduralDefinitions : SqlProceduralDefinition list = []
                    let mutable sessionOperation = SqlStatementEffectOperation.Unknown
                    let mutable controlOperation = SqlTransactionControlOperation.None
                    let status, effects, certainty, bindingStatus, nameStatus, typeStatus, next, changes, diagnostics =
                        if syntax.Status = SqlScriptParseStatus.Invalid then
                            SqlStatementAnalysisStatus.Invalid, SqlEffectKind.None,
                            SqlCatalogKnowledge.Unknown, SqlCatalogBindingStatus.Rejected,
                            SqlCatalogBindingStatus.Rejected, SqlCatalogBindingStatus.NotApplicable, current, [],
                            (if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                             else [syntax.Diagnostic])
                        elif syntax.Kind = SqlScriptStatementKind.Transaction then
                            let status, certainty, next, nextSession, newTransaction, changes, diagnostics, parsedControl =
                                RewriteTransactionAnalyzer.analyze
                                    sourceDialect sourceProfile current session transaction syntax
                            controlOperation <- parsedControl
                            // A transaction boundary or savepoint rollback is
                            // responsible for BOTH catalog and session state.
                            // No separate coordinator-side rollback heuristic.
                            transaction <- newTransaction
                            session <- nextSession
                            let effects =
                                if changes.IsEmpty then SqlEffectKind.ChangeTransaction
                                else SqlEffectKind.ChangeTransaction ||| SqlEffectKind.ChangeSchema
                            status, effects, certainty, SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable, SqlCatalogBindingStatus.NotApplicable,
                            next, changes, diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Security then
                            let status, intents, dependencies, diagnostics =
                                RewriteResourceBudget.withStage "SecurityAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                                    RewriteSecurityAnalyzer.analyze sourceDialect sourceProfile syntax)
                            securityIntents <- intents
                            securityDependencies <- dependencies
                            status, SqlEffectKind.ChangePermissions, SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            current, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Utility then
                            let status, intents, next, diagnostics =
                                RewriteUtilityAnalyzer.analyze sourceDialect sourceProfile current syntax
                            utilityIntents <- intents
                            let flags =
                                intents |> List.fold (fun all item -> all ||| item.PotentialEffects)
                                    SqlEffectKind.Administrative
                            let flags =
                                if status = SqlStatementAnalysisStatus.Unsupported
                                   || status = SqlStatementAnalysisStatus.Invalid then
                                    flags ||| SqlEffectKind.UnknownEffects
                                else flags
                            status, flags, SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            next, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Procedural then
                            let status, intents, definitions, next, diagnostics =
                                RewriteResourceBudget.withStage "ProceduralAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                                    RewriteProceduralAnalyzer.analyze sourceDialect sourceProfile current syntax)
                            proceduralIntents <- intents
                            proceduralDefinitions <- definitions
                            // Stored procedures may change rows, schema, session,
                            // transaction boundaries or permissions. Neither CALL
                            // syntax nor a procedure header proves the body.
                            let flags =
                                SqlEffectKind.ReadData ||| SqlEffectKind.WriteData
                                ||| SqlEffectKind.ChangeSchema ||| SqlEffectKind.ChangePermissions
                                ||| SqlEffectKind.ChangeTransaction ||| SqlEffectKind.ChangeSession
                                ||| SqlEffectKind.Administrative ||| SqlEffectKind.UnknownEffects
                            let flags =
                                if not definitions.IsEmpty then
                                    // Creating a routine changes metadata;
                                    // statements in its body have NOT run.
                                    SqlEffectKind.ChangeSchema ||| SqlEffectKind.UnknownEffects
                                else flags
                            status, flags, SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            next, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Session then
                            let analyzedStatus, analyzedCertainty, analyzedSession, analyzedCatalog, diagnostics, operation =
                                RewriteSessionAnalyzer.analyze sourceDialect sourceProfile session current syntax
                            // A recognized SET/USE is syntax evidence, not proof
                            // that it took effect in an unproven transaction
                            // (for example, one that might already be aborted).
                            // Keep the typed operation but never re-mint a
                            // known Session/Catalog premise while Transaction
                            // remains Unknown. Unsupported syntax already
                            // invalidates these premises in the analyzer.
                            let status, certainty, nextSession, nextCatalog =
                                if transactionBefore = SqlTransactionState.Unknown
                                   && analyzedStatus <> SqlStatementAnalysisStatus.Unsupported then
                                    SqlStatementAnalysisStatus.PartiallyAnalyzed,
                                    SqlCatalogKnowledge.Unknown,
                                    RewriteSessionAnalyzer.unknown,
                                    RewriteCatalogModel.taintAll analyzedCatalog
                                else
                                    analyzedStatus, analyzedCertainty, analyzedSession, analyzedCatalog
                            session <- nextSession
                            sessionOperation <- operation
                            let sessionEffects =
                                if status = SqlStatementAnalysisStatus.PartiallyAnalyzed then
                                    SqlEffectKind.ChangeSession ||| SqlEffectKind.UnknownEffects
                                else SqlEffectKind.ChangeSession
                            status, sessionEffects, certainty,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            nextCatalog, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Schema
                             && sourceDialect = SqlAgentToolType.MySQL
                             && transactionBefore = SqlTransactionState.Active then
                            // MySQL non-temporary DDL implicitly commits, but
                            // temporary-table variants and storage engines
                            // have distinct rules. Do not run the generic DDL
                            // virtual-Catalog mutation then pretend an outer
                            // ROLLBACK can restore its original state.
                            let warning =
                                diagnostic syntax "SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN"
                                    SqlDiagnosticCategory.Capability
                                    "MySQL DDL may cause an implicit commit or use nontransactional storage; transaction/catalog rollback proof is invalid."
                            SqlStatementAnalysisStatus.Unsupported,
                            SqlEffectKind.ChangeSchema ||| SqlEffectKind.ChangeTransaction
                                ||| SqlEffectKind.UnknownEffects,
                            SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            RewriteCatalogModel.taintAll current,
                            ([]: SqlCatalogChange list), [warning]
                        elif syntax.Kind = SqlScriptStatementKind.Schema then
                            let status, certainty, next, changes, diagnostics =
                                analyzeDdl sourceDialect sourceProfile (RewriteSessionAnalyzer.scope session) current syntax
                            let effects =
                                if sourceDialect = SqlAgentToolType.Oracle
                                   || sourceDialect = SqlAgentToolType.MySQL then
                                    // Oracle and non-temporary MySQL DDL implicitly
                                    // commit even outside an explicit BEGIN.
                                    // The static Outside context cannot prove
                                    // that a MySQL session has autocommit enabled.
                                    // Runtime DML effects remain unproven.
                                    SqlEffectKind.ChangeSchema ||| SqlEffectKind.ChangeTransaction
                                else SqlEffectKind.ChangeSchema
                            status, effects, certainty, SqlCatalogBindingStatus.NotApplicable,
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
                    // Unrecognized or invalid SQL has no exhaustive effect proof.
                    // Apply the same conservative flag to every family before
                    // transaction provenance and structured details are derived.
                    // This never promotes a statement to Analyzed or authorizes SQL.
                    let effects =
                        if status = SqlStatementAnalysisStatus.Unsupported
                           || status = SqlStatementAnalysisStatus.Invalid then
                            effects ||| SqlEffectKind.UnknownEffects
                        else effects
                    // Only opaque, potentially executing families lose the
                    // static Outside premise. Unsupported DDL/DCL/Session and
                    // unknown SQL have unmodeled transaction behavior; routine
                    // calls and executing utilities may run transaction control
                    // indirectly. A syntactically invalid statement has no
                    // modeled execution, and planning-only EXPLAIN remains safe.
                    let outsideBoundaryUnproven =
                        if status = SqlStatementAnalysisStatus.Invalid then false
                        else
                            match syntax.Kind with
                            | SqlScriptStatementKind.Unknown
                            | SqlScriptStatementKind.Procedural -> true
                            | SqlScriptStatementKind.Session
                            | SqlScriptStatementKind.Schema
                            | SqlScriptStatementKind.Security ->
                                status = SqlStatementAnalysisStatus.Unsupported
                            | SqlScriptStatementKind.Utility ->
                                status = SqlStatementAnalysisStatus.Unsupported
                                || (utilityIntents |> List.exists (fun intent ->
                                    intent.ExecutionMode <> SqlUtilityExecutionMode.PlanningOnly))
                            | _ -> false
                    let nextTransaction =
                        RewriteTransactionAnalyzer.invalidateIfUnproven
                            sourceDialect transaction syntax.Kind status outsideBoundaryUnproven
                    let transactionAfter = RewriteTransactionAnalyzer.status nextTransaction
                    let stateBecameUnknown =
                        transactionBefore = SqlTransactionState.Active
                        && transactionAfter = SqlTransactionState.Unknown
                    let outsideProofLost =
                        transactionBefore = SqlTransactionState.Outside
                        && transactionAfter = SqlTransactionState.Unknown
                        && syntax.Kind <> SqlScriptStatementKind.Transaction
                    let transactionProofLost = stateBecameUnknown || outsideProofLost
                    transaction <- nextTransaction
                    // An Unsupported statement can change Session state even
                    // if its surface family is not Session (e.g. DISCARD ALL
                    // or ALTER SESSION). A procedure invocation and an
                    // executing utility also have opaque Session side effects.
                    // Pure EXPLAIN planning and parsed-but-invalid syntax do
                    // not invent a session transition.
                    let sessionUnproven =
                        status = SqlStatementAnalysisStatus.Unsupported
                        || (syntax.Kind = SqlScriptStatementKind.Procedural
                            && not proceduralIntents.IsEmpty)
                        || (syntax.Kind = SqlScriptStatementKind.Utility
                            && (utilityIntents |> List.exists (fun intent ->
                                intent.ExecutionMode <> SqlUtilityExecutionMode.PlanningOnly)))
                    if transactionProofLost || sessionUnproven then
                        session <- RewriteSessionAnalyzer.unknown
                    let after =
                        if transactionProofLost then RewriteCatalogModel.taintAll next else next
                    let additionalErrors =
                        if stateBecameUnknown then
                            [diagnostic syntax "SQL_TRANSACTION_CONTEXT_UNPROVEN" SqlDiagnosticCategory.Capability
                                "An unproven statement or failure has invalidated the transaction-state and Catalog proofs."]
                        else []
                    let allDiagnostics = diagnostics @ additionalErrors
                    // Semantic grammar/analysis may be recognized even while
                    // the input transaction state is unproven (notably Oracle
                    // COMMIT/ROLLBACK). Recognition does not re-establish
                    // certainty for the enclosing statement or its effects.
                    let runtimeCertainty =
                        if transactionProofLost
                           || transactionBefore = SqlTransactionState.Unknown then
                            SqlCatalogKnowledge.Unknown
                        else certainty
                    let provenance =
                        RewriteResourceBudget.withStage "TransactionProvenance" SqlDiagnosticStage.SemanticValidation (fun () ->
                            RewriteTransactionProvenance.describe
                                sourceDialect syntax status runtimeCertainty effects
                                transactionBefore (RewriteTransactionAnalyzer.status transaction)
                                controlOperation (readOnly allDiagnostics))
                    current <- after
                    for item in allDiagnostics do errors.Add item
                    let detailedEffects, effectCoverage =
                        RewriteResourceBudget.withStage "ScriptEffects" SqlDiagnosticStage.SemanticValidation (fun () ->
                            RewriteStatementEffects.describe sourceDialect sourceProfile syntax
                                status runtimeCertainty effects (readOnly changes)
                                (readOnly securityIntents) (readOnly securityDependencies)
                                (readOnly utilityIntents) (readOnly proceduralIntents)
                                (readOnly proceduralDefinitions) sessionOperation provenance)
                    results.Add(
                        SqlStatementAnalysis(
                            syntax, status, effects,
                            runtimeCertainty,
                            bindingStatus, nameStatus, typeStatus, before, after,
                            transactionBefore, RewriteTransactionAnalyzer.status transaction,
                            sessionBefore, RewriteSessionAnalyzer.snapshot session,
                            readOnly changes, detailedEffects, effectCoverage,
                            readOnly securityIntents, readOnly securityDependencies,
                            readOnly utilityIntents, readOnly proceduralIntents,
                            readOnly proceduralDefinitions,
                            provenance, readOnly allDiagnostics)))
            SqlScriptAnalysisResult(
                readOnly results, readOnly errors, current,
                RewriteTransactionAnalyzer.status transaction,
                RewriteSessionAnalyzer.snapshot session))
