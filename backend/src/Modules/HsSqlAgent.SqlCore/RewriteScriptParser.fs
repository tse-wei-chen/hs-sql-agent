namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Script-only parsing layer. Never produces executable plans or authorization proofs.
/// The existing parser remains the single owner of recognized Query/Mutation grammar.
module internal RewriteScriptParser =

    type private StatementFamily =
        | Query
        | Mutation
        | Schema
        | Security
        | Transaction
        | Session
        | Utility
        | Procedural
        | Unknown

    let private word (tokens: Token[]) index =
        if index >= tokens.Length then ""
        else
            match tokens.[index].Kind with
            | Keyword value
            | TokenKind.Identifier(value, false) -> value.ToUpperInvariant()
            | _ -> ""

    let private classify (tokens: Token[]) index sourceDialect =
        let first = word tokens index
        let second = word tokens (index + 1)
        match first with
        | "SELECT" | "WITH" -> Query
        | "INSERT" | "UPDATE" | "DELETE" | "MERGE" -> Mutation
        | "CREATE" ->
            let keywordPosition =
                if second = "OR"
                   && (word tokens (index + 2) = "REPLACE"
                       || (sourceDialect = SqlAgentToolType.MsSqlServer
                           && word tokens (index + 2) = "ALTER")) then index + 3
                else index + 1
            match word tokens keywordPosition with
            | "PROCEDURE" | "PROC" | "FUNCTION" | "TRIGGER" -> Procedural
            | _ -> Schema
        | "ALTER" when second = "PROCEDURE" || second = "PROC"
                         || second = "FUNCTION" || second = "TRIGGER" -> Procedural
        | "RECREATE" when sourceDialect = SqlAgentToolType.Firebird && second = "TABLE" -> Schema
        | "ALTER" | "DROP" | "TRUNCATE" | "RENAME" -> Schema
        | "GRANT" | "REVOKE" | "DENY" -> Security
        | "BEGIN" when sourceDialect = SqlAgentToolType.MsSqlServer
                       && second <> "TRAN" && second <> "TRANSACTION"
                       && second <> "DISTRIBUTED" -> Procedural
        | "BEGIN" when sourceDialect = SqlAgentToolType.Oracle
                       || sourceDialect = SqlAgentToolType.Firebird -> Procedural
        | "BEGIN" | "START" | "COMMIT" | "ROLLBACK" | "SAVEPOINT" | "RELEASE" -> Transaction
        | "SAVE" when sourceDialect = SqlAgentToolType.MsSqlServer
                      && (second = "TRAN" || second = "TRANSACTION") -> Transaction
        | "SET" | "RESET" | "USE" | "SHOW" -> Session
        | "EXPLAIN" | "VACUUM" | "ANALYZE" | "DESCRIBE" | "DESC" | "PRAGMA" -> Utility
        | "CALL" | "EXEC" | "EXECUTE" | "DO" | "DECLARE" -> Procedural
        | _ -> Unknown

    let private publicKind = function
        | Query -> SqlScriptStatementKind.Query
        | Mutation -> SqlScriptStatementKind.Mutation
        | Schema -> SqlScriptStatementKind.Schema
        | Security -> SqlScriptStatementKind.Security
        | Transaction -> SqlScriptStatementKind.Transaction
        | Session -> SqlScriptStatementKind.Session
        | Utility -> SqlScriptStatementKind.Utility
        | Procedural -> SqlScriptStatementKind.Procedural
        | Unknown -> SqlScriptStatementKind.Unknown

    /// Opaque procedural bodies have dialect-specific internal semicolons.
    /// Until there is a dedicated procedural parser, never split their bodies
    /// into apparent (and falsely validated) standalone SELECT/DML statements.
    let private requiresOpaqueRemainder (tokens: Token[]) start dialect =
        match classify tokens start dialect with
        | Procedural ->
            let first = word tokens start
            // PostgreSQL's SQL-language string/dollar-quoted definitions
            // have their internal semicolons in a single lexical literal.
            // A normal terminating semicolon can therefore safely split
            // them from following statements. Bare BEGIN ATOMIC and other
            // unquoted procedural bodies stay opaque.
            let quotedDefinition =
                dialect = SqlAgentToolType.Postgres && first = "CREATE"
                && (tokens
                    |> Seq.skip start
                    |> Seq.takeWhile (fun token ->
                        token.Kind <> Symbol ';' && token.Kind <> End)
                    |> Seq.exists (fun token ->
                        match token.Kind with
                        | StringLiteral _ -> true
                        | _ -> false))
            ((first = "CREATE" && not quotedDefinition)
             || first = "ALTER"
             || first = "DO" || first = "DECLARE" || first = "BEGIN"
             || (dialect = SqlAgentToolType.Firebird
                 && first = "EXECUTE" && word tokens (start + 1) = "BLOCK"))
        | _ -> false

    /// GO is SQL Server's client-side batch separator only when its physical
    /// line contains trivia on both sides. CR and LF terminate physical lines,
    /// matching RewriteLexer, and naturally covering CRLF scripts.
    let private goLineTriviaOnly (sql: string) fromInclusive toExclusive =
        let mutable cursor = fromInclusive
        let mutable valid = true
        while valid && cursor < toExclusive do
            if Char.IsWhiteSpace(sql.[cursor]) then
                cursor <- cursor + 1
            elif cursor + 1 < toExclusive
                 && sql.[cursor] = '-'
                 && sql.[cursor + 1] = '-' then
                cursor <- toExclusive
            elif cursor + 1 < toExclusive
                 && sql.[cursor] = '/'
                 && sql.[cursor + 1] = '*' then
                // Only comments closed before this physical line ends count.
                let closeAt =
                    sql.IndexOf("*/", cursor + 2,
                                toExclusive - (cursor + 2),
                                StringComparison.Ordinal)
                if closeAt < 0 then valid <- false
                else cursor <- closeAt + 2
            else
                valid <- false
        valid

    let private isSqlServerGoLine (sql: string) (token: Token) lineStart lineEnd =
        goLineTriviaOnly sql lineStart token.Start
        && goLineTriviaOnly sql (token.Start + token.Length) lineEnd

    let private invalidDiagnostic (ex: SqlParseException) absoluteStart statementLength =
        match ex.Diagnostic with
        | null ->
            SqlDiagnostic(
                "SQL_SCRIPT_STATEMENT_PARSE_ERROR",
                SqlDiagnosticStage.Parse,
                SqlDiagnosticCategory.Syntax,
                ex.Message,
                SqlDiagnosticSpan(absoluteStart, statementLength))
        | original ->
            let span =
                if isNull original.Span then SqlDiagnosticSpan(absoluteStart, statementLength)
                else SqlDiagnosticSpan(absoluteStart + original.Span.Start, original.Span.Length)
            SqlDiagnostic(original.Code, original.Stage, original.Category, original.Message, span)

    /// The standalone ParseScript API publishes only its original immutable syntax DTO.
    /// AnalyzeScript additionally keeps typed statements privately, by visible ordinal.
    let private parseCore captureTyped (sql: string) sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null) =
        ArgumentNullException.ThrowIfNull(sql)
        RewriteResourceBudget.withCompilation (fun () ->
            let tokens =
                RewriteResourceBudget.withStage "ScriptLexical" SqlDiagnosticStage.Lexical (fun () ->
                    RewriteFacadeAdapter.tokenizeScriptValidated sql sourceDialect sourceProfile)
                |> List.toArray
            let result = ResizeArray<SqlScriptStatementSyntax>()
            let diagnostics = ResizeArray<SqlDiagnostic>()
            let captured =
                if captureTyped then Some(ResizeArray<ParsedSql option>()) else None
            // Visible batch ordinals exclude empty physical GO segments.
            // Only a segment that emitted at least one statement advances the index.
            let mutable batch = 0
            let mutable batchHasStatements = false
            let mutable start = -1
            let mutable nesting = 0
            let mutable opaque = false
            let mutable index = 0

            // Line boundaries are cached across ascending GO candidates. A
            // previous LastIndexOf/IndexOf pair per GO token could rescan the
            // same physical line quadratically for a long identifier list.
            // This is lazy for scripts without GO and for other dialects.
            let goLineBreaks = [| '\r'; '\n' |]
            let mutable goLineStart = 0
            let mutable goLineEnd = -1
            let mutable goLinePrefixValid = true

            let isGoSeparator (token: Token) =
                match token.Kind with
                | TokenKind.Identifier(value, false)
                    when sourceDialect = SqlAgentToolType.MsSqlServer
                         && String.Equals(value, "GO", StringComparison.OrdinalIgnoreCase) ->
                    while goLineEnd < token.Start do
                        goLineStart <- goLineEnd + 1
                        let nextLineBreak = sql.IndexOfAny(goLineBreaks, goLineStart)
                        goLineEnd <-
                            if nextLineBreak < 0 then sql.Length else nextLineBreak
                        goLinePrefixValid <- true
                        // Include the scanned source in the existing per-attempt
                        // ScriptDispatch work budget; never bypass stage limits.
                        RewriteResourceBudget.charge (int64 (goLineEnd - goLineStart))

                    // A second GO token on the same line cannot be standalone,
                    // no matter how much trivia precedes or follows it.
                    let isolated =
                        goLinePrefixValid
                        && isSqlServerGoLine sql token goLineStart goLineEnd
                    goLinePrefixValid <- false
                    isolated
                | _ -> false

            // End is a sentinel, never part of a source statement.
            let tokenCount = tokens.Length - 1

            let emit stopExclusive =
                if start >= 0 && stopExclusive > start then
                    let first = tokens.[start]
                    let last = tokens.[stopExclusive - 1]
                    let absoluteStart = first.Start
                    let length = last.Start + last.Length - absoluteStart
                    let text = sql.Substring(absoluteStart, length)
                    let family = classify tokens start sourceDialect
                    let mutable typed: ParsedSql option = None
                    let actualFamily, status, diagnostic =
                        match family with
                        | Query | Mutation ->
                            try
                                let parsed =
                                    RewriteResourceBudget.withStage "ScriptStatementParse" SqlDiagnosticStage.Parse (fun () ->
                                        RewriteFacadeAdapter.parseSourceValidated text sourceDialect sourceProfile)
                                // WITH is a lexical prefix, not reliable Query
                                // evidence. Only the closed, parsed F# AST
                                // determines the public DQL/DML statement kind.
                                if captureTyped then typed <- Some parsed
                                let parsedFamily =
                                    match (Typestate.Parsed.value parsed).Statement with
                                    | Statement.QueryStatement _ -> Query
                                    | Statement.InsertStatement _
                                    | Statement.UpdateStatement _
                                    | Statement.DeleteStatement _
                                    | Statement.MergeStatement _ -> Mutation
                                parsedFamily, SqlScriptParseStatus.Parsed, None
                            with
                            | :? SqlParseException as ex
                                when not (Object.ReferenceEquals(ex.Diagnostic, null))
                                     && ex.Diagnostic.Category = SqlDiagnosticCategory.Resource ->
                                reraise()
                            | :? SqlParseException as ex ->
                                family, SqlScriptParseStatus.Invalid,
                                Some(invalidDiagnostic ex absoluteStart length)
                        | _ ->
                            family, SqlScriptParseStatus.Unsupported,
                            Some(
                                SqlDiagnostic(
                                    "SQL_SCRIPT_UNSUPPORTED_STATEMENT",
                                    SqlDiagnosticStage.Parse,
                                    SqlDiagnosticCategory.Capability,
                                    "Statement family recognized, but semantic analysis is not implemented.",
                                    SqlDiagnosticSpan(absoluteStart, length)))
                    diagnostic |> Option.iter diagnostics.Add
                    result.Add(
                        SqlScriptStatementSyntax(
                            publicKind actualFamily,
                            status,
                            batch,
                            SqlDiagnosticSpan(absoluteStart, length),
                            text,
                            Option.defaultValue Unchecked.defaultof<SqlDiagnostic> diagnostic))
                    match captured with
                    | Some statements -> statements.Add typed
                    | None -> ()
                    batchHasStatements <- true
                start <- -1
                nesting <- 0
                opaque <- false

            RewriteResourceBudget.withStage "ScriptDispatch" SqlDiagnosticStage.Parse (fun () ->
                while index < tokenCount do
                    RewriteResourceBudget.checkpoint ()
                    let token = tokens.[index]
                    // GO is a physical client-side batch boundary even when a
                    // prior malformed statement has unmatched parentheses.
                    // Keep error isolation across independent SQL Server batches.
                    if isGoSeparator token then
                        emit index
                        // An empty GO segment is not an analyzable batch;
                        // trailing and adjacent GO cannot create phantom counts.
                        if batchHasStatements then
                            batch <- batch + 1
                            batchHasStatements <- false
                    else
                        if start < 0 then
                            match token.Kind with
                            | Symbol ';' -> ()
                            | _ ->
                                start <- index
                                opaque <- requiresOpaqueRemainder tokens start sourceDialect
                        match token.Kind with
                        | Symbol '(' -> nesting <- nesting + 1
                        | Symbol ')' ->
                            // Dispatch depth represents *open* grouping only.
                            // An extra ')' makes this statement invalid in the
                            // strict parser; it must not turn depth negative
                            // and swallow later semicolon-separated statements.
                            // Opaque procedure bodies still ignore ';', and an
                            // unmatched opening '(' still blocks recovery.
                            nesting <- max 0 (nesting - 1)
                        | Symbol ';' when nesting = 0 && not opaque ->
                            emit index
                        | _ -> ()
                    index <- index + 1

                emit tokenCount)
            let statements : IReadOnlyList<SqlScriptStatementSyntax> =
                Array.AsReadOnly(result.ToArray())
            let errors : IReadOnlyList<SqlDiagnostic> =
                Array.AsReadOnly(diagnostics.ToArray())
            let batchCount = batch + (if batchHasStatements then 1 else 0)
            SqlScriptParseResult(batchCount, statements, errors),
            (captured |> Option.map (fun statements -> statements.ToArray())))

    let parse (sql: string) sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null) =
        parseCore false sql sourceDialect sourceProfile |> fst

    /// Private AST sidecar; never an executable token, public DTO or cross-call cache.
    let parseForAnalysis (sql: string) sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null) =
        let result, documents = parseCore true sql sourceDialect sourceProfile
        result, Option.get documents
