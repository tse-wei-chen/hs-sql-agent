namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

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
            | Identifier(value, false) -> value.ToUpperInvariant()
            | _ -> ""

    let private classify (tokens: Token[]) index sourceDialect =
        let first = word tokens index
        let second = word tokens (index + 1)
        match first with
        | "SELECT" | "WITH" -> Query
        | "INSERT" | "UPDATE" | "DELETE" | "MERGE" -> Mutation
        | "CREATE" ->
            let keywordPosition =
                if second = "OR" && word tokens (index + 2) = "REPLACE" then index + 3
                else index + 1
            match word tokens keywordPosition with
            | "PROCEDURE" | "PROC" | "FUNCTION" | "TRIGGER" -> Procedural
            | _ -> Schema
        | "ALTER" | "DROP" | "TRUNCATE" | "RENAME" -> Schema
        | "GRANT" | "REVOKE" | "DENY" -> Security
        | "BEGIN" when sourceDialect = SqlAgentToolType.MsSqlServer
                       && second <> "TRAN" && second <> "TRANSACTION"
                       && second <> "DISTRIBUTED" -> Procedural
        | "BEGIN" | "START" | "COMMIT" | "ROLLBACK" | "SAVEPOINT" | "RELEASE" -> Transaction
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
            first = "CREATE" || first = "DO" || first = "DECLARE" || first = "BEGIN"
        | _ -> false

    /// GO is a SQL Server client-side batch separator ONLY on a line by itself.
    /// A token inside a quoted literal/comment is not an Identifier, so cannot
    /// be confused with GO. Repetition-count forms are not silently accepted.
    let private isSqlServerGoLine (sql: string) (token: Token) dialect =
        if dialect <> SqlAgentToolType.MsSqlServer then false
        else
            match token.Kind with
            | Identifier(value, false) when String.Equals(value, "GO", StringComparison.OrdinalIgnoreCase) ->
                let previousNewline =
                    if token.Start = 0 then -1
                    else sql.LastIndexOf('\n', token.Start - 1)
                let lineStart = previousNewline + 1
                let afterToken = token.Start + token.Length
                let nextNewline = sql.IndexOf('\n', afterToken)
                let lineEnd = if nextNewline < 0 then sql.Length else nextNewline
                String.IsNullOrWhiteSpace(sql.Substring(lineStart, token.Start - lineStart))
                && String.IsNullOrWhiteSpace(sql.Substring(afterToken, lineEnd - afterToken))
            | _ -> false

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

    let parse (sql: string) sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null) =
        ArgumentNullException.ThrowIfNull(sql)
        RewriteResourceBudget.withCompilation (fun () ->
            let tokens =
                RewriteResourceBudget.withStage "ScriptLexical" SqlDiagnosticStage.Lexical (fun () ->
                    RewriteFacadeAdapter.tokenizeSourceValidated sql sourceDialect sourceProfile)
                |> List.toArray
            let result = ResizeArray<SqlScriptStatementSyntax>()
            let diagnostics = ResizeArray<SqlDiagnostic>()
            let mutable batch = 0
            let mutable start = -1
            let mutable nesting = 0
            let mutable opaque = false
            let mutable index = 0
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
                    let status, diagnostic =
                        match family with
                        | Query | Mutation ->
                            try
                                RewriteResourceBudget.withStage "ScriptStatementParse" SqlDiagnosticStage.Parse (fun () ->
                                    RewriteFacadeAdapter.parseSourceValidated text sourceDialect sourceProfile
                                    |> ignore)
                                SqlScriptParseStatus.Parsed, None
                            with
                            | :? SqlParseException as ex
                                when not (Object.ReferenceEquals(ex.Diagnostic, null))
                                     && ex.Diagnostic.Category = SqlDiagnosticCategory.Resource ->
                                reraise()
                            | :? SqlParseException as ex ->
                                SqlScriptParseStatus.Invalid,
                                Some(invalidDiagnostic ex absoluteStart length)
                        | _ ->
                            SqlScriptParseStatus.Unsupported,
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
                            publicKind family,
                            status,
                            batch,
                            SqlDiagnosticSpan(absoluteStart, length),
                            text,
                            Option.defaultValue Unchecked.defaultof<SqlDiagnostic> diagnostic))
                start <- -1
                nesting <- 0
                opaque <- false

            RewriteResourceBudget.withStage "ScriptDispatch" SqlDiagnosticStage.Parse (fun () ->
                while index < tokenCount do
                    RewriteResourceBudget.checkpoint ()
                    let token = tokens.[index]
                    if nesting = 0 && isSqlServerGoLine sql token sourceDialect then
                        emit index
                        batch <- batch + 1
                    else
                        if start < 0 then
                            match token.Kind with
                            | Symbol ';' -> ()
                            | _ ->
                                start <- index
                                opaque <- requiresOpaqueRemainder tokens start sourceDialect
                        match token.Kind with
                        | Symbol '(' -> nesting <- nesting + 1
                        | Symbol ')' -> nesting <- nesting - 1
                        | Symbol ';' when nesting = 0 && not opaque ->
                            emit index
                        | _ -> ()
                    index <- index + 1

                emit tokenCount)
            let statements : IReadOnlyList<SqlScriptStatementSyntax> =
                Array.AsReadOnly(result.ToArray())
            let errors : IReadOnlyList<SqlDiagnostic> =
                Array.AsReadOnly(diagnostics.ToArray())
            let batchCount = if result.Count = 0 then 0 else batch + 1
            SqlScriptParseResult(batchCount, statements, errors))
