namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Single owner of lexical family candidates and typed AST family decisions.
/// Candidates and unsupported script classifications are never execution proof.
module internal RewriteStatementDispatch =
    type StatementFamily =
        | Query | Mutation | Schema | Security | Transaction
        | Session | Utility | Procedural | Unknown

    // A lexical classification is not authority and should not allocate on
    // the per-statement ScriptDispatch hot path. Keep parsed/bound proof DUs
    // reference-backed with private constructors to preserve typestate safety.
    [<Struct>]
    type LexicalCandidate =
        | Parseable of StatementFamily
        | WithCandidate
        | Recognized of StatementFamily
        | Unrecognized

    /// Constructors are private: these tokens come only from parsed F# AST.
    type ParsedFamily =
        private
        | ProvenQuerySyntax of ParsedSql
        | ProvenMutationSyntax of ParsedSql

    type BoundFamily =
        private
        | ProvenQueryBinding of BoundSql
        | ProvenMutationBinding of BoundSql

    let word (tokens: Token[]) index =
        if index >= tokens.Length then ""
        else
            match tokens.[index].Kind with
            | Keyword value
            | TokenKind.Identifier(value, false) -> value.ToUpperInvariant()
            | _ -> ""

    let candidate (tokens: Token[]) index sourceDialect =
        let first = word tokens index
        let second = word tokens (index + 1)
        match first with
        | "SELECT" -> Parseable Query
        | "WITH" -> WithCandidate
        | "INSERT" | "UPDATE" | "DELETE" | "MERGE" -> Parseable Mutation
        | "CREATE" ->
            let keywordPosition =
                if second = "OR"
                   && (word tokens (index + 2) = "REPLACE"
                       || ((sourceDialect = SqlAgentToolType.MsSqlServer
                             || sourceDialect = SqlAgentToolType.Firebird)
                           && word tokens (index + 2) = "ALTER")) then index + 3
                else index + 1
            match word tokens keywordPosition with
            | "PROCEDURE" | "PROC" | "FUNCTION" | "TRIGGER" -> Recognized Procedural
            | _ -> Recognized Schema
        | "ALTER" when second = "PROCEDURE" || second = "PROC"
                         || second = "FUNCTION" || second = "TRIGGER" ->
            Recognized Procedural
        | "RECREATE" when sourceDialect = SqlAgentToolType.Firebird ->
            match second with
            | "TABLE" -> Recognized Schema
            | "PROCEDURE" | "FUNCTION" | "TRIGGER" -> Recognized Procedural
            | _ -> Unrecognized
        | "ALTER" | "DROP" | "TRUNCATE" | "RENAME" -> Recognized Schema
        | "GRANT" | "REVOKE" | "DENY" -> Recognized Security
        | "BEGIN" when sourceDialect = SqlAgentToolType.MsSqlServer
                       && second <> "TRAN" && second <> "TRANSACTION"
                       && second <> "DISTRIBUTED" -> Recognized Procedural
        | "BEGIN" when sourceDialect = SqlAgentToolType.Oracle
                       || sourceDialect = SqlAgentToolType.Firebird -> Recognized Procedural
        | "BEGIN" | "START" | "COMMIT" | "ROLLBACK" | "SAVEPOINT" | "RELEASE" ->
            Recognized Transaction
        | "SAVE" when sourceDialect = SqlAgentToolType.MsSqlServer
                      && (second = "TRAN" || second = "TRANSACTION") ->
            Recognized Transaction
        | "SET" when second = "TRANSACTION" -> Recognized Transaction
        | "SET" | "RESET" | "USE" | "SHOW" -> Recognized Session
        | "EXPLAIN" | "VACUUM" | "ANALYZE" | "DESCRIBE" | "DESC" | "PRAGMA" ->
            Recognized Utility
        | "CALL" | "EXEC" | "EXECUTE" | "DO" | "DECLARE" -> Recognized Procedural
        | _ -> Unrecognized

    let candidateFamily = function
        | Parseable family | Recognized family -> family
        | WithCandidate | Unrecognized -> Unknown

    let publicKind = function
        | Query -> SqlScriptStatementKind.Query
        | Mutation -> SqlScriptStatementKind.Mutation
        | Schema -> SqlScriptStatementKind.Schema
        | Security -> SqlScriptStatementKind.Security
        | Transaction -> SqlScriptStatementKind.Transaction
        | Session -> SqlScriptStatementKind.Session
        | Utility -> SqlScriptStatementKind.Utility
        | Procedural -> SqlScriptStatementKind.Procedural
        | Unknown -> SqlScriptStatementKind.Unknown

    let proveParsed (parsed: ParsedSql) =
        match (Parsed.value parsed).Statement with
        | QueryStatement _ -> ProvenQuerySyntax parsed
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            ProvenMutationSyntax parsed

    let proveBound (bound: BoundSql) =
        match (Bound.value bound).Statement with
        | QueryStatement _ -> ProvenQueryBinding bound
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            ProvenMutationBinding bound

    /// Only the Query and Mutation grammars are currently compile-capable.
    /// The closed two-case result lets callers exhaustively match without
    /// exposing private proof constructors or admitting analyze-only families.
    type CompilableFamily = QueryGrammar | MutationGrammar

    let compiledFamily = function
        | ProvenQuerySyntax _ -> QueryGrammar
        | ProvenMutationSyntax _ -> MutationGrammar

    let boundFamily = function
        | ProvenQueryBinding _ -> QueryGrammar
        | ProvenMutationBinding _ -> MutationGrammar

    let parsedValue = function
        | ProvenQuerySyntax parsed | ProvenMutationSyntax parsed -> parsed

    let boundValue = function
        | ProvenQueryBinding bound | ProvenMutationBinding bound -> bound

    let provenFamily = function
        | ProvenQuerySyntax _ -> Query
        | ProvenMutationSyntax _ -> Mutation
