namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// Bounded, provider-specific *static* utility analysis only. A recognized
/// syntax neither executes SQL nor establishes effective permissions. For
/// EXPLAIN ANALYZE, the nested SQL passes the same strict source parser used
/// by the executable compiler; unsupported forms must never be reinterpreted
/// as a harmless planner-only EXPLAIN.
module internal RewriteUtilityAnalyzer =

    type private ParsedUtility =
        | Recognized of SqlUtilityIntent
        | Unsupported of string
        | InvalidInner of string

    let private word (token: Token) =
        match token.Kind with
        | Keyword value
        | Identifier(value, false) -> value.ToUpperInvariant()
        | _ -> ""

    let private isSymbol c token =
        match token.Kind with
        | Symbol value -> c = value
        | _ -> false

    let private targetPart sourceDialect sourceProfile (token: Token) =
        match token.Kind with
        | Identifier(name, quoted) when
            not (String.IsNullOrWhiteSpace name)
            && (quoted || RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier name) ->
            Some (RewriteCatalogIdentifierProof.canonicalName sourceDialect sourceProfile name quoted)
        | _ -> None

    let private potential operation mode kind target flags syntax =
        Recognized(SqlUtilityIntent(operation, mode, kind, target, flags, syntax.Span))

    let private unsupported message = Unsupported message

    let private planSyntax sourceDialect sourceProfile (syntax: SqlScriptStatementSyntax)
                           (tokens: Token[]) =
        let name index = if index < tokens.Length then word tokens[index] else ""
        let symbol index value = index < tokens.Length && isSymbol value tokens[index]
        let allowed =
            sourceDialect = SqlAgentToolType.Postgres
            || sourceDialect = SqlAgentToolType.MySQL
            || sourceDialect = SqlAgentToolType.Sqlite
        if not allowed then
            unsupported "EXPLAIN is not a proven portable utility grammar for this source dialect."
        else
            let mutable index = 1
            let mutable execute = false
            let mutable validOptions = true
            if sourceDialect = SqlAgentToolType.Sqlite then
                // SQLite has EXPLAIN QUERY PLAN, but not PostgreSQL's ANALYZE.
                if name index = "QUERY" && name (index + 1) = "PLAN" then
                    index <- index + 2
                else validOptions <- false
            elif name index = "ANALYZE" then
                execute <- true
                index <- index + 1
            elif sourceDialect = SqlAgentToolType.Postgres && symbol index '(' then
                // Only one explicitly proven boolean option. All other
                // options and mixed option lists remain fail-closed.
                if name (index + 1) = "ANALYZE" then
                    if symbol (index + 2) ')' then
                        execute <- true
                        index <- index + 3
                    elif symbol (index + 3) ')' then
                        match name (index + 2) with
                        | "TRUE" | "ON" ->
                            execute <- true
                            index <- index + 4
                        | "FALSE" | "OFF" ->
                            execute <- false
                            index <- index + 4
                        | _ -> validOptions <- false
                    else validOptions <- false
                else validOptions <- false
            elif symbol index '(' then validOptions <- false

            if not validOptions then
                unsupported "EXPLAIN options or dialect-specific plan grammar are outside the proven subset."
            elif index >= tokens.Length then
                unsupported "EXPLAIN has no analyzable statement."
            else
                let head = name index
                let kind =
                    match head with
                    | "SELECT" | "WITH" -> Some SqlScriptStatementKind.Query
                    | "UPDATE" | "DELETE" when sourceDialect = SqlAgentToolType.Postgres ->
                        Some SqlScriptStatementKind.Mutation
                    | "UPDATE" | "DELETE" when not execute && sourceDialect = SqlAgentToolType.MySQL ->
                        Some SqlScriptStatementKind.Mutation
                    | _ -> None
                match kind with
                | None ->
                    unsupported "EXPLAIN nested command is unsupported; no read-only plan assertion can be made."
                | Some innerKind ->
                    let sql = syntax.Text.Substring(tokens[index].Start)
                    try
                        RewriteResourceBudget.withStage "UtilityInnerParse" SqlDiagnosticStage.Parse (fun () ->
                            RewriteFacadeAdapter.parseSourceValidated sql sourceDialect sourceProfile
                            |> ignore)
                        let flags =
                            if execute then
                                SqlEffectKind.Administrative
                                ||| SqlEffectKind.UnknownEffects
                                ||| (if innerKind = SqlScriptStatementKind.Mutation then
                                        SqlEffectKind.WriteData else SqlEffectKind.ReadData)
                            else SqlEffectKind.Administrative
                        potential
                            (if execute then SqlUtilityOperation.ExplainExecute else SqlUtilityOperation.ExplainPlan)
                            (if execute then SqlUtilityExecutionMode.ExecutesStatement
                             else SqlUtilityExecutionMode.PlanningOnly)
                            innerKind null flags syntax
                    with
                    | :? SqlParseException as ex ->
                        // Preserve resource errors at their original stage.
                        if not (isNull ex.Diagnostic)
                           && ex.Diagnostic.Category = SqlDiagnosticCategory.Resource then
                            reraise()
                        InvalidInner "EXPLAIN nested SQL is not accepted by the strict source parser."

    let private maintenance sourceDialect sourceProfile
                            (syntax: SqlScriptStatementSyntax) (tokens: Token[]) =
        if sourceDialect <> SqlAgentToolType.Postgres then
            unsupported "This provider's maintenance command grammar is not modeled."
        else
            // Explicit single table only: unqualified names depend on an
            // unknown search path, bare ANALYZE/VACUUM can affect every table,
            // and options/columns have semantics not admitted here.
            let parts =
                if tokens.Length = 4 && isSymbol '.' tokens[2] then
                    match targetPart sourceDialect sourceProfile tokens[1],
                          targetPart sourceDialect sourceProfile tokens[3] with
                    | Some schema, Some table -> Some (schema + "." + table)
                    | _ -> None
                else None
            match parts with
            | None ->
                unsupported "Only an explicitly schema-qualified, single-table PostgreSQL utility is modeled."
            | Some target ->
                let operation =
                    if word tokens[0] = "ANALYZE" then SqlUtilityOperation.AnalyzeStatistics
                    else SqlUtilityOperation.VacuumMaintenance
                potential operation SqlUtilityExecutionMode.Maintenance
                    SqlScriptStatementKind.Unknown target
                    (SqlEffectKind.Administrative ||| SqlEffectKind.UnknownEffects)
                    syntax

    let private diagnostic (syntax: SqlScriptStatementSyntax) code category message =
        SqlDiagnostic(code, SqlDiagnosticStage.SemanticValidation, category, message, syntax.Span)

    let analyze sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                (catalog: SqlCatalogState) (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "UtilityLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
            |> List.filter (fun token -> token.Kind <> End)
            |> List.toArray
        let result =
            if tokens.Length = 0 then unsupported "Utility command is empty."
            else
                match word tokens[0] with
                | "EXPLAIN" -> planSyntax sourceDialect sourceProfile syntax tokens
                | "ANALYZE" | "VACUUM" -> maintenance sourceDialect sourceProfile syntax tokens
                | _ -> unsupported "Unmodeled or provider-specific utility statement."

        match result with
        | Recognized intent ->
            // Planning-only EXPLAIN does not execute the nested mutation. All
            // actually executed or maintenance forms conservatively taint the
            // Virtual Catalog (opaque triggers/functions/DDL or system state).
            let next =
                if intent.ExecutionMode = SqlUtilityExecutionMode.PlanningOnly then catalog
                else RewriteCatalogModel.taintAll catalog
            SqlStatementAnalysisStatus.PartiallyAnalyzed, [intent], next, []
        | Unsupported message ->
            SqlStatementAnalysisStatus.Unsupported, [],
            RewriteCatalogModel.taintAll catalog,
            [diagnostic syntax "SQL_UTILITY_UNSUPPORTED" SqlDiagnosticCategory.Capability message]
        | InvalidInner message ->
            SqlStatementAnalysisStatus.Invalid, [],
            RewriteCatalogModel.taintAll catalog,
            [diagnostic syntax "SQL_UTILITY_INNER_INVALID" SqlDiagnosticCategory.Syntax message]
