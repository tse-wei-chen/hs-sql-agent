namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteCatalogSchemaResolution
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// Ordered, immutable and non-executable provider-specific session premises.
/// Search paths, database context and result-message options are deliberately
/// separate: USE database is never evidence of SQL Server's default schema.
module internal RewriteSessionAnalyzer =

    type SessionContext = private {
        Scope: SchemaScope
        CurrentDatabase: string | null
        NoCount: Nullable<bool>
    }

    let initial dialect (profile: SqlProviderCapabilityProfile | null) = {
        Scope = RewriteCatalogSchemaResolution.scope dialect profile
        CurrentDatabase = null
        NoCount = Nullable()
    }

    let scope context = context.Scope

    let unknown = {
        Scope = Unproven
        CurrentDatabase = null
        NoCount = Nullable()
    }

    let snapshot context =
        let path : string | null =
            match context.Scope with
            | SingleSchema schema -> schema
            | _ -> null
        let databaseProven =
            match context.Scope with
            | SingleDatabase _ -> true
            | _ -> false
        SqlSessionAnalysisState(
            path, context.CurrentDatabase, databaseProven, context.NoCount)

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

    let private plainDatabase token =
        match token.Kind with
        | Identifier(value, false) when
            RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier value ->
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
            message, syntax.Span)

    /// An unsupported setting may change parsing, identity, authorization
    /// and transaction behavior. The previous Catalog/scope proofs cannot
    /// safely carry across it.
    let analyze sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                (context: SessionContext) (catalog: SqlCatalogState)
                (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "SessionLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
            |> List.filter (fun t -> t.Kind <> End)

        let success status certainty newContext next operation =
            status, certainty, newContext, next, [],
            operation

        match tokens with
        | [command; setting; assign; schema] when
            sourceDialect = SqlAgentToolType.Postgres
            && word command = "SET"
            && word setting = "SEARCH_PATH"
            && isAssignment assign ->
            match plainSchema schema with
            | Some name when
                not (name.StartsWith("pg_", StringComparison.OrdinalIgnoreCase))
                && name <> "information_schema" ->
                success SqlStatementAnalysisStatus.Analyzed SqlCatalogKnowledge.Known
                    { context with Scope = SingleSchema name }
                    catalog SqlStatementEffectOperation.SetSearchPath
            | _ ->
                SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
                unknown, RewriteCatalogModel.taintAll catalog,
                [diagnostic syntax "PostgreSQL SEARCH_PATH is outside the proven single ordinary user schema subset."],
                SqlStatementEffectOperation.Unknown

        | [command; database] when
            word command = "USE"
            && (sourceDialect = SqlAgentToolType.MySQL
                || sourceDialect = SqlAgentToolType.MsSqlServer) ->
            match plainDatabase database with
            | Some name ->
                let normalized =
                    RewriteCatalogIdentifierProof.canonicalName
                        sourceDialect sourceProfile name false
                if sourceDialect = SqlAgentToolType.MySQL then
                    // Only a declared lower_case_table_names=1 proof allows
                    // table identities from the Virtual Catalog to be
                    // projected into the new default database.
                    let newScope = SingleDatabase normalized
                    let provable =
                        RewriteCatalogIdentifierProof.mode sourceDialect sourceProfile =
                            RewriteCatalogIdentifierProof.FoldAscii
                        && RewriteCatalogSchemaResolution.canProject newScope catalog
                    let nextContext = {
                        context with
                            Scope = if provable then newScope else Unproven
                            CurrentDatabase = normalized
                    }
                    success
                        (if provable then SqlStatementAnalysisStatus.Analyzed
                         else SqlStatementAnalysisStatus.PartiallyAnalyzed)
                        (if provable then SqlCatalogKnowledge.Known
                         else SqlCatalogKnowledge.Unknown)
                        nextContext
                        (if provable then catalog else RewriteCatalogModel.taintAll catalog)
                        SqlStatementEffectOperation.UseDatabase
                else
                    // SQL Server USE is database-context switching, never a
                    // default-schema premise. Catalog facts from the old DB
                    // must not authorize a query in the newly selected DB.
                    success SqlStatementAnalysisStatus.PartiallyAnalyzed
                        SqlCatalogKnowledge.Unknown
                        { context with
                            Scope = Unproven
                            CurrentDatabase = normalized }
                        (RewriteCatalogModel.taintAll catalog)
                        SqlStatementEffectOperation.UseDatabase
            | None ->
                SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
                unknown, RewriteCatalogModel.taintAll catalog,
                [diagnostic syntax "USE requires a single plain ASCII database identifier."],
                SqlStatementEffectOperation.Unknown

        | [command; optionName; value] when
            sourceDialect = SqlAgentToolType.MsSqlServer
            && word command = "SET"
            && word optionName = "NOCOUNT"
            && (word value = "ON" || word value = "OFF") ->
            success SqlStatementAnalysisStatus.Analyzed SqlCatalogKnowledge.Known
                { context with NoCount = Nullable(word value = "ON") }
                catalog SqlStatementEffectOperation.SetNoCount

        | _ ->
            SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
            unknown, RewriteCatalogModel.taintAll catalog,
            [diagnostic syntax
                "Session command or provider-specific semantics are outside the proven session capability subset."],
            SqlStatementEffectOperation.Unknown
