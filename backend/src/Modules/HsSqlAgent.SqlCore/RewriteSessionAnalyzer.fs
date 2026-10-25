namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteCatalogSchemaResolution
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// Scoped, ordered, static session transitions. Only a single ordinary
/// PostgreSQL search_path is modeled; all remaining session grammar is
/// deliberately unproven and invalidates the old Catalog/scope premises.
module internal RewriteSessionAnalyzer =

    type SessionContext = private SessionContext of SchemaScope

    let initial dialect (profile: SqlProviderCapabilityProfile | null) =
        SessionContext(RewriteCatalogSchemaResolution.scope dialect profile)

    let scope (SessionContext value) = value

    let unknown = SessionContext Unproven

    let snapshot (SessionContext context) =
        let value : string | null =
            match context with
            | SingleSchema schema -> schema
            | Unproven -> null
        SqlSessionAnalysisState(value)

    let private word token =
        match token.Kind with
        | Keyword value
        | Identifier(value, false) -> value.ToUpperInvariant()
        | _ -> ""

    let private plainSchema token =
        match token.Kind with
        | Identifier(value, false)
        | Keyword value when
            String.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
            && RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier value ->
            Some value
        | _ -> None

    let private isAssignment token =
        word token = "TO"
        || (match token.Kind with
            | Symbol '='
            | Operator "=" -> true
            | _ -> false)

    let private diagnostic (syntax: SqlScriptStatementSyntax) message =
        SqlDiagnostic(
            "SQL_SESSION_UNSUPPORTED",
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Capability,
            message,
            syntax.Span)

    /// Parsing this subset never executes SQL or changes the source profile.
    /// An unsupported setting may affect name resolution, authorization,
    /// parsing or transaction behavior; therefore old Catalog proofs are lost.
    let analyze sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                (context: SessionContext) (catalog: SqlCatalogState)
                (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "SessionLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
            |> List.filter (fun t -> t.Kind <> End)
        let newScope =
            match tokens with
            | [command; setting; assign; schema] when
                sourceDialect = SqlAgentToolType.Postgres
                && word command = "SET"
                && word setting = "SEARCH_PATH"
                && isAssignment assign ->
                match plainSchema schema with
                | Some name ->
                    // Reuse the same restricted proof as declared profile
                    // scope: pg_*, information_schema, multi-path, $user and
                    // case-sensitive/quoted names cannot be certified.
                    if name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
                       || name = "information_schema" then None
                    else Some(SingleSchema name)
                | None -> None
            | _ -> None
        match newScope with
        | Some known ->
            SqlStatementAnalysisStatus.Analyzed,
            SqlCatalogKnowledge.Known,
            SessionContext known,
            catalog,
            []
        | None ->
            SqlStatementAnalysisStatus.Unsupported,
            SqlCatalogKnowledge.Unknown,
            unknown,
            RewriteCatalogModel.taintAll catalog,
            [diagnostic syntax
                "Session command or source-dialect semantics are outside the proven single-schema PostgreSQL search_path subset."]
