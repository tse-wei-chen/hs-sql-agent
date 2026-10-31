namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Ddl
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.RewriteSchemaAnalyzer
open HsSqlAgent.SqlCore.Script

/// A third, strictly separate executable COMPILATION domain. Static DDL
/// parsing and Catalog analysis alone can never construct PolicyCheckedDdl.
module internal RewriteDdlCompiler =

    type private ParsedDdl = private ParsedDdl of DdlOperation
    type private BoundDdl = private BoundDdl of DdlOperation
    type private ValidatedDdl = private ValidatedDdl of DdlOperation
    type private PolicyCheckedDdl = private PolicyCheckedDdl of DdlOperation

    let private reject stage category code message : 'T =
        let diagnostic = SqlDiagnostic(code, stage, category, message, null)
        raise (SqlCompilationException(message, diagnostic))

    let private capability message : 'T =
        reject SqlDiagnosticStage.TargetCapability SqlDiagnosticCategory.Capability
            "SQL_DDL_TARGET_UNPROVEN" message

    let private policy message : 'T =
        reject SqlDiagnosticStage.Policy SqlDiagnosticCategory.Policy
            "SQL_DDL_POLICY_DENIED" message

    let private sourceSyntax message : 'T =
        reject SqlDiagnosticStage.Parse SqlDiagnosticCategory.DialectSyntax
            "SQL_DDL_UNSUPPORTED_SYNTAX" message

    let private ensureSimpleName name =
        if not (RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier name) then
            policy "DDL executable-plan identifiers must be simple ASCII names."
        name

    let private ensureIdentity (table: SqlCatalogQualifiedName) =
        if isNull table || table.Parts.Count < 1 || table.Parts.Count > 2 then
            policy "DDL requires one exact one- or two-part physical object identity."
        table.Parts |> Seq.iter (ensureSimpleName >> ignore)

    /// Source parsing is owned exclusively by the existing F# lexer and
    /// RewriteSchemaAnalyzer's closed DdlOperation grammar. No regex text
    /// surrogate or second DDL AST/parser is constructed.
    let private parse (options: DdlCompilationOptions) (sql: string) =
        if String.IsNullOrWhiteSpace(sql) then sourceSyntax "Empty DDL input."
        let text = sql.Trim()
        // One optional terminal semicolon is a statement terminator, NOT a
        // license to accept other statements. Internal semicolons are rejected
        // by the existing closed grammar's endOfStatement check.
        let sqlText =
            if text.EndsWith(";", StringComparison.Ordinal) then
                text.Substring(0, text.Length - 1).TrimEnd()
            else text
        let tokens =
            RewriteFacadeAdapter.tokenizeSourceValidated sqlText options.SourceDialect options.SourceProfile
            |> List.toArray
        match RewriteSchemaAnalyzer.parseForWith options.SourceDialect options.SourceProfile tokens with
        | Recognized operation -> ParsedDdl operation
        | Unsupported -> sourceSyntax "DDL is outside the proven schema compilation grammar."
        | Invalid message -> sourceSyntax message

    /// DDL binder accepts only one fully typed additive physical operation;
    /// constraints, conditionals, cascading/partial schema updates, DDL
    /// batches and destructive operations have no construction path.
    let private bind (ParsedDdl operation) =
        let boundedColumns (columns: SqlCatalogColumn list) =
            if List.isEmpty columns || List.length columns > 128 then
                policy "DDL column count exceeds the explicitly bounded subset."
            for column in columns do
                RewriteResourceBudget.checkpoint()
                ensureSimpleName column.Name |> ignore
            if columns |> List.map (fun col -> col.Name) |> List.distinct |> List.length
                <> List.length columns then policy "Duplicate DDL columns."
        match operation with
        | Create(table, columns, [], false) ->
            ensureIdentity table
            boundedColumns columns
            BoundDdl operation
        | AlterColumns(table, [Add column]) when column.Nullable ->
            ensureIdentity table
            boundedColumns [column]
            BoundDdl operation
        | CreateIndex(table, indexName, columns, false) ->
            ensureIdentity table
            ensureSimpleName indexName |> ignore
            if List.isEmpty columns || List.length columns > 16 then
                policy "DDL physical index key count exceeds the proven bound."
            for name in columns do
                RewriteResourceBudget.checkpoint()
                ensureSimpleName name |> ignore
            if List.distinct columns |> List.length <> List.length columns then
                policy "Duplicate physical columns in DDL index."
            BoundDdl operation
        | Drop(table, false) ->
            ensureIdentity table
            if table.Parts.Count <> 2 then
                policy "Destructive DROP requires an exact qualified schema.table identity."
            BoundDdl operation
        | _ ->
            policy "DDL must be additive or a separately proven qualified DROP TABLE RESTRICT."

    let private validate (options: DdlCompilationOptions) (BoundDdl operation) =
        if options.SourceDialect <> options.TargetDialect then
            capability "Cross-dialect DDL translation requires a distinct proven native semantic mapping."
        if not (isNull options.SourceProfile) then
            if options.SourceProfile.Provider <> options.SourceDialect then
                capability "DDL source capability profile does not match the source provider."
        if not (isNull options.TargetProfile) then
            if options.TargetProfile.Provider <> options.TargetDialect then
                capability "DDL target capability profile does not match the target provider."
        let sourceVersion =
            if isNull options.SourceProfile then null else options.SourceProfile.ServerVersion
        let targetVersion =
            if isNull options.TargetProfile then null else options.TargetProfile.ServerVersion
        if not (isNull sourceVersion)
           && (isNull targetVersion || targetVersion.CompareTo(sourceVersion) < 0) then
            capability "Source-version-dependent DDL cannot be lowered without an equal or newer verified target profile."
        match operation with
        | Drop _ when options.SourceDialect <> SqlAgentToolType.Postgres ->
            capability "Destructive DROP TABLE requires PostgreSQL-specific transactional proof."
        | _ -> ()
        // The parser owns the same-provider type whitelist and version gates;
        // preserve the strict typed operation rather than reconstructing type
        // interpretation from raw SQL or pretending catalog Known is live.
        ValidatedDdl operation

    let private authorize (ValidatedDdl operation) =
        // This is *compile policy* only. Every output still needs physical
        // identity, schema freshness, permission and human approval at Host.
        PolicyCheckedDdl operation

    let private quote provider (name: string) =
        let valid = ensureSimpleName name
        match provider with
        | SqlAgentToolType.MsSqlServer -> "[" + valid + "]"
        | SqlAgentToolType.MySQL -> "`" + valid + "`"
        | SqlAgentToolType.Postgres | SqlAgentToolType.Sqlite
        | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> "\"" + valid + "\""
        | _ -> capability "Unknown native DDL provider."

    let private render (options: DdlCompilationOptions) (PolicyCheckedDdl operation) =
        let q = quote options.TargetDialect
        let qualified (identity: SqlCatalogQualifiedName) =
            identity.Parts |> Seq.map q |> String.concat "."
        let columnDefinition (column: SqlCatalogColumn) =
            q column.Name + " " + column.TypeName
            + (if column.Nullable then " NULL" else " NOT NULL")
        match operation with
        | Create(table, columns, [], false) ->
            let sql = "CREATE TABLE " + qualified table + " ("
                      + (columns |> List.map columnDefinition |> String.concat ", ") + ")"
            sql, table, DdlSchemaOperation.CreateTable, DdlPrecondition.RequireObjectAbsent,
            (columns |> List.map (fun c -> c.Name)), null,
            DdlExecutionRisk.Additive, DdlSchemaImpact(true, false), columns
        | AlterColumns(table, [Add column]) when column.Nullable ->
            let definition = columnDefinition column
            let addition =
                match options.TargetDialect with
                | SqlAgentToolType.Oracle -> " ADD (" + definition + ")"
                | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Firebird -> " ADD " + definition
                | _ -> " ADD COLUMN " + definition
            let sql = "ALTER TABLE " + qualified table + addition
            sql, table, DdlSchemaOperation.AddNullableColumn,
            DdlPrecondition.RequireTablePresentAndColumnAbsent,
            [column.Name], null, DdlExecutionRisk.PotentialLock, DdlSchemaImpact(true, false), [column]
        | CreateIndex(table, indexName, columns, false) ->
            // SQLite: schema-qualified ON table is syntactically illegal.
            // The index itself must carry the database schema; ON uses the
            // unqualified table name from the same selected schema.
            let indexIdentity, tableIdentity =
                if options.TargetDialect = SqlAgentToolType.Sqlite
                   && table.Parts.Count = 2 then
                    q table.Parts.[0] + "." + q indexName, q table.Parts.[1]
                else q indexName, qualified table
            let sql = "CREATE INDEX " + indexIdentity + " ON " + tableIdentity
                      + " (" + (columns |> List.map q |> String.concat ", ") + ")"
            sql, table, DdlSchemaOperation.CreateIndex,
            DdlPrecondition.RequireTableAndColumnsPresentAndIndexAbsent,
            columns, indexName, DdlExecutionRisk.PotentialLock, DdlSchemaImpact(true, false), []
        | Drop(table, false) when options.TargetDialect = SqlAgentToolType.Postgres ->
            let sql = "DROP TABLE " + qualified table + " RESTRICT"
            sql, table, DdlSchemaOperation.DropTable,
            DdlPrecondition.RequireTablePresentAndEmpty,
            [], null, DdlExecutionRisk.Destructive, DdlSchemaImpact(true, true), []
        | _ ->
            reject SqlDiagnosticStage.RenderingInvariant SqlDiagnosticCategory.Invariant
                "SQL_DDL_RENDER_INVARIANT" "DDL rendering received an operation that did not pass policy."

    let compile (options: DdlCompilationOptions) (sql: string) =
        ArgumentNullException.ThrowIfNull(options)
        ArgumentNullException.ThrowIfNull(sql)
        RewriteResourceBudget.withCompilation (fun () ->
            let parsed =
                RewriteResourceBudget.withStage "DdlParsing" SqlDiagnosticStage.Parse
                    (fun () -> parse options sql)
            let bound =
                RewriteResourceBudget.withStage "DdlBinding" SqlDiagnosticStage.Binding
                    (fun () -> bind parsed)
            let validated =
                RewriteResourceBudget.withStage "DdlValidation" SqlDiagnosticStage.TargetCapability
                    (fun () -> validate options bound)
            let authorized =
                RewriteResourceBudget.withStage "DdlPolicy" SqlDiagnosticStage.Policy
                    (fun () -> authorize validated)
            let nativeSql, identity, operation, precondition, columns, indexName, risk, impact, definitions =
                RewriteResourceBudget.withStage "DdlRendering" SqlDiagnosticStage.RenderingInvariant
                    (fun () -> render options authorized)
            let sourceVersion =
                if isNull options.SourceProfile then null else options.SourceProfile.ServerVersion
            let targetVersion =
                if isNull options.TargetProfile then null else options.TargetProfile.ServerVersion
            let versionText (version: Version | null) =
                if isNull version then "unknown" else version.ToString()
            let fingerprintInput =
                String.concat "\n"
                    ["ddl-compiler-v1"
                     string options.SourceDialect; string options.TargetDialect
                     versionText sourceVersion; versionText targetVersion
                     string operation; string precondition
                     identity.Key; String.concat "," columns
                     (if isNull indexName then "" else indexName)
                     nativeSql; string risk]
            let fingerprint =
                SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))
                |> Convert.ToHexString
            let changes : IReadOnlyList<DdlSchemaChange> =
                Array.AsReadOnly([|DdlSchemaChange(
                    identity, operation, precondition,
                    Array.AsReadOnly(List.toArray columns), indexName,
                    Array.AsReadOnly(List.toArray definitions))|])
            let transaction =
                match options.TargetDialect with
                | SqlAgentToolType.MySQL | SqlAgentToolType.Oracle ->
                    DdlTransactionBehavior.ImplicitCommit
                | _ -> DdlTransactionBehavior.Unknown
            CompiledDdlPlan(
                DdlCompiledCommand(nativeSql, options.TargetDialect),
                options.TargetDialect, changes, impact, risk, transaction,
                DdlCompileEvidence(options.SourceDialect, options.TargetDialect,
                                   sourceVersion, targetVersion), fingerprint))

    /// The existing Script lexer/dispatcher owns all statement boundaries.
    /// The existing closed single-statement DDL compiler validates EVERY
    /// item; a batch is not an authorization shortcut or a second parser.
    let compileBatch (options: DdlCompilationOptions) (sql: string) =
        ArgumentNullException.ThrowIfNull(options)
        ArgumentNullException.ThrowIfNull(sql)
        RewriteResourceBudget.withCompilation (fun () ->
            if options.SourceDialect <> SqlAgentToolType.Postgres
               || options.TargetDialect <> SqlAgentToolType.Postgres then
                capability "DDL batch compilation requires independently proven PostgreSQL transactional semantics."
            let script = RewriteScriptParser.parse sql options.SourceDialect options.SourceProfile
            if script.BatchCount <> 1
               || script.Statements.Count < 2 || script.Statements.Count > 4 then
                policy "DDL batch requires exactly 2–4 statements in one physical script batch."
            let plans =
                script.Statements
                |> Seq.map (fun statement ->
                    RewriteResourceBudget.checkpoint()
                    if statement.Kind <> SqlScriptStatementKind.Schema then
                        sourceSyntax "DDL batch contains a non-schema or procedural statement."
                    let plan = compile options statement.Text
                    let change = plan.Changes.[0]
                    if plan.Changes.Count <> 1
                       || change.ObjectIdentity.Parts.Count <> 2
                       || plan.Risk = DdlExecutionRisk.Destructive then
                        policy "DDL batch requires one bounded, qualified additive schema operation."
                    plan)
                |> Seq.toArray

            let changes = plans |> Array.map (fun p -> p.Changes.[0])
            let identities = changes |> Array.map (fun change -> change.ObjectIdentity.Key)
            let schemas = changes |> Array.map (fun c -> c.ObjectIdentity.Parts.[0])
            if schemas |> Array.exists (fun schema -> schema <> schemas.[0]) then
                policy "DDL batch cannot cross schema boundaries."

            let topology =
                if changes |> Array.forall (fun c -> c.Operation = DdlSchemaOperation.CreateTable) then
                    if (identities |> Array.distinct |> Array.length) <> changes.Length then
                        policy "Independent CREATE batch contains a repeated physical object identity."
                    DdlBatchTopology.IndependentCreates
                else
                    // Closed, ordered effect proof: a freshly created table
                    // may gain one or two nullable columns, then at most one
                    // ordinary INDEX on *proven* materialized columns.
                    // Existing-table ALTER, reordered effects, multiple
                    // CREATE roots and interleaved independent objects have
                    // no executable batch construction path.
                    if changes.[0].Operation <> DdlSchemaOperation.CreateTable
                       || (identities |> Array.exists (fun key -> key <> identities.[0])) then
                        policy "Dependent batch requires one new CREATE root and a single exact physical table identity."
                    let known = HashSet<string>(changes.[0].Columns, StringComparer.Ordinal)
                    let mutable addCount = 0
                    let mutable indexCount = 0
                    for i = 1 to changes.Length - 1 do
                        RewriteResourceBudget.checkpoint()
                        let change = changes.[i]
                        match change.Operation with
                        | DdlSchemaOperation.AddNullableColumn ->
                            if indexCount <> 0 || addCount = 2
                               || change.Columns.Count <> 1
                               || change.ColumnDefinitions.Count <> 1
                               || not change.ColumnDefinitions.[0].Nullable
                               || not (known.Add change.Columns.[0]) then
                                policy "Dependent ADD must be unique, nullable, and precede the optional index."
                            addCount <- addCount + 1
                        | DdlSchemaOperation.CreateIndex ->
                            if addCount = 0 || indexCount <> 0
                               || i <> changes.Length - 1
                               || String.IsNullOrEmpty(change.IndexName)
                               || change.IndexName = changes.[0].ObjectIdentity.Parts.[1]
                               || (change.Columns |> Seq.exists (fun name -> not (known.Contains name))) then
                                policy "Dependent INDEX must be last and reference only materialized columns."
                            indexCount <- indexCount + 1
                        | _ ->
                            policy "Dependent batch permits only CREATE, nullable ADD and final ordinary INDEX."
                    if addCount = 0 then
                        policy "Dependent batch requires a nullable ADD after the new CREATE."
                    DdlBatchTopology.DependentCreateChain

            let ordered =
                plans
                |> Array.mapi (fun i plan ->
                    string i + ":" + plan.PlanFingerprint + ":" + plan.Command.Sql)
            let fingerprint =
                String.concat "\n" (Array.append
                    [| "pg-atomic-ddl-batch-v2"; string topology |] ordered)
                |> Encoding.UTF8.GetBytes
                |> SHA256.HashData
                |> Convert.ToHexString
            CompiledDdlBatchPlan(
                Array.AsReadOnly(plans) :> IReadOnlyList<CompiledDdlPlan>,
                Array.AsReadOnly(
                    script.Statements |> Seq.map (fun item -> item.Span) |> Seq.toArray)
                    :> IReadOnlyList<SqlDiagnosticSpan>,
                fingerprint, topology))
