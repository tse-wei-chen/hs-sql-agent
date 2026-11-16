namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate
open HsSqlAgent.SqlCore.Rewrite.RewriteSchemaAnalyzer

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
    type CandidateFamily =
        | Parseable of StatementFamily
        | WithCandidate
        | Recognized of StatementFamily
        | Unrecognized

    /// Constructors are private: these tokens come only from parsed F# AST.
    type VerifiedFamily =
        private
        | ProvenQuerySyntax of ParsedSql
        | ProvenMutationSyntax of ParsedSql

    /// A Query/Mutation parser proof is never interchangeable with the
    /// Schema parser proof, and neither includes an authorization decision.
    type VerifiedSchema = private VerifiedSchema of DdlOperation

    type SchemaVerification =
        | ProvenSchema of VerifiedSchema
        | UnsupportedSchema
        | InvalidSchema of string

    /// Script parsing deliberately preserves Unsupported for analyze-only
    /// domains, even where a separate domain grammar can analyze them.
    type ScriptDecision =
        | Verified of VerifiedFamily
        | InvalidCandidate of StatementFamily * SqlDiagnostic
        | UnsupportedCandidate of StatementFamily * SqlDiagnostic
        | UnknownCandidate of SqlDiagnostic

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

    /// Only the existing closed Schema grammar may mint a schema proof.
    /// The compiler supplies already validated tokens, avoiding any second lex.
    let verifySchema dialect profile tokens =
        match RewriteSchemaAnalyzer.parseForWith dialect profile tokens with
        | DdlSyntax.Recognized operation -> ProvenSchema(VerifiedSchema operation)
        | DdlSyntax.Unsupported -> UnsupportedSchema
        | DdlSyntax.Invalid message -> InvalidSchema message

    let schemaOperation (VerifiedSchema operation) = operation

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

    /// Exactly the two grammars that can produce a compiled AST.
    /// Internal exhaustive matching remains owned by this module.
    type CompilableFamily = QueryGrammar | MutationGrammar

    let private grammarOfParsed (parsed: ParsedSql) =
        match (Parsed.value parsed).Statement with
        | QueryStatement _ -> QueryGrammar
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            MutationGrammar

    /// ParseScript needs the family but not an extra allocated proof token.
    /// It still derives the decision solely from the fully parsed closed AST.
    let parsedFamily parsed =
        match grammarOfParsed parsed with
        | QueryGrammar -> Query
        | MutationGrammar -> Mutation

    let proveParsed parsed : VerifiedFamily =
        match grammarOfParsed parsed with
        | QueryGrammar -> ProvenQuerySyntax parsed
        | MutationGrammar -> ProvenMutationSyntax parsed

    let projectScriptDecision = function
        | Verified proof ->
            let kind =
                match proof with
                | ProvenQuerySyntax _ -> Query
                | ProvenMutationSyntax _ -> Mutation
            publicKind kind, SqlScriptParseStatus.Parsed, None
        | InvalidCandidate(family, diagnostic) ->
            publicKind family, SqlScriptParseStatus.Invalid, Some diagnostic
        | UnsupportedCandidate(family, diagnostic) ->
            publicKind family, SqlScriptParseStatus.Unsupported, Some diagnostic
        | UnknownCandidate diagnostic ->
            SqlScriptStatementKind.Unknown, SqlScriptParseStatus.Unsupported, Some diagnostic

    let proveBound (bound: BoundSql) =
        match (Bound.value bound).Statement with
        | QueryStatement _ -> ProvenQueryBinding bound
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            ProvenMutationBinding bound

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

