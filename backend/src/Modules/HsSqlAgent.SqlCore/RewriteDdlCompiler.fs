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
        | _ ->
            policy "Executable DDL subset is additive CREATE TABLE, nullable ADD COLUMN, or ordinary CREATE INDEX only."

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
            let sql = "CREATE INDEX " + q indexName + " ON " + qualified table
                      + " (" + (columns |> List.map q |> String.concat ", ") + ")"
            sql, table, DdlSchemaOperation.CreateIndex,
            DdlPrecondition.RequireTableAndColumnsPresentAndIndexAbsent,
            columns, indexName, DdlExecutionRisk.PotentialLock, DdlSchemaImpact(true, false), []
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
