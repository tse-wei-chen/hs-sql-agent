namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.SqlParsing

/// Static binding of an already parsed DQL/DML Script statement against
/// definite, closed-world virtual DDL facts. No policy, SQL rendering, schema
/// snapshot, compile evidence or database access is available at this boundary.
module internal RewriteCatalogBinding =

    let private report (syntax: SqlScriptStatementSyntax) code stage category message =
        SqlDiagnostic(code, stage, category, message, syntax.Span)

    let private statusForCode (code: string) =
        if code = "SQL_SCHEMA_SHAPE_UNPROVABLE"
           || code = "SQL_SCHEMA_MERGE_UNPROVABLE"
           || code = "SQL_SCHEMA_CONFLICT_UNPROVABLE" then
            SqlCatalogBindingStatus.Unverified
        else
            SqlCatalogBindingStatus.Rejected

    let private rejectedOrUnverified (syntax: SqlScriptStatementSyntax) (diagnostic: SqlDiagnostic) =
        let binding = statusForCode diagnostic.Code
        let analysis =
            if binding = SqlCatalogBindingStatus.Rejected then SqlStatementAnalysisStatus.Invalid
            else SqlStatementAnalysisStatus.PartiallyAnalyzed
        let offset =
            if Object.ReferenceEquals(diagnostic.Span, null) then syntax.Span
            else
                SqlDiagnosticSpan(
                    syntax.Span.Start + diagnostic.Span.Start,
                    diagnostic.Span.Length)
        let adjusted =
            SqlDiagnostic(
                diagnostic.Code,
                diagnostic.Stage,
                diagnostic.Category,
                diagnostic.Message,
                offset)
        binding, analysis, SqlCatalogKnowledge.Unknown, [adjusted]

    let verify sourceDialect (profile: SqlProviderCapabilityProfile | null)
               (state: SqlCatalogState) (syntax: SqlScriptStatementSyntax) =
        if sourceDialect <> SqlAgentToolType.Postgres then
            let diagnostic =
                report syntax "SQL_CATALOG_BINDING_DIALECT_UNPROVEN"
                    SqlDiagnosticStage.Binding SqlDiagnosticCategory.Capability
                    "Virtual Catalog DQL/DML binding is not yet proven for this source dialect."
            SqlCatalogBindingStatus.Unverified, SqlStatementAnalysisStatus.PartiallyAnalyzed,
            SqlCatalogKnowledge.Unknown, [diagnostic]
        elif not state.IsClosedWorld
             || (state.Tables |> Seq.exists (fun t -> t.Knowledge <> SqlCatalogKnowledge.Known)) then
            let diagnostic =
                report syntax "SQL_CATALOG_BINDING_INCOMPLETE"
                    SqlDiagnosticStage.Binding SqlDiagnosticCategory.Capability
                    "Catalog is open or incomplete; referenced names and types cannot be proven."
            SqlCatalogBindingStatus.Unverified, SqlStatementAnalysisStatus.PartiallyAnalyzed,
            SqlCatalogKnowledge.Unknown, [diagnostic]
        else
            try
                let parsed =
                    RewriteResourceBudget.withStage "CatalogParsing" SqlDiagnosticStage.Parse (fun () ->
                        RewriteFacadeAdapter.parseSourceValidated syntax.Text sourceDialect profile)
                let bound =
                    RewriteResourceBudget.withStage "CatalogBinding" SqlDiagnosticStage.Binding (fun () ->
                        RewriteBinder.bind sourceDialect parsed)
                let namesChecked =
                    RewriteResourceBudget.withStage "CatalogNameValidation" SqlDiagnosticStage.Binding (fun () ->
                        RewriteSchemaValidation.verifyVirtual sourceDialect state bound)
                RewriteResourceBudget.withStage "CatalogTypeValidation" SqlDiagnosticStage.SemanticValidation (fun () ->
                    RewriteSchemaTypeValidation.verifyVirtual sourceDialect state namesChecked)
                |> ignore
                SqlCatalogBindingStatus.Verified, SqlStatementAnalysisStatus.PartiallyAnalyzed,
                SqlCatalogKnowledge.Known, []
            with
            | :? SqlCompilationException as ex
                when not (Object.ReferenceEquals(ex.Diagnostic, null))
                     && ex.Diagnostic.Category = SqlDiagnosticCategory.Resource ->
                reraise()
            | :? SqlCompilationException as ex when not (Object.ReferenceEquals(ex.Diagnostic, null)) ->
                rejectedOrUnverified syntax ex.Diagnostic
            | :? InvalidOperationException as ex
                when ex.Data.Contains("HsSqlAgent.SqlCore.Diagnostic") ->
                match ex.Data["HsSqlAgent.SqlCore.Diagnostic"] with
                | :? SqlDiagnostic as d -> rejectedOrUnverified syntax d
                | _ -> reraise()
            | :? SqlParseException as ex
                when not (Object.ReferenceEquals(ex.Diagnostic, null))
                     && ex.Diagnostic.Category = SqlDiagnosticCategory.Resource ->
                reraise()
            | :? SqlParseException as ex ->
                let diagnostic =
                    report syntax "SQL_CATALOG_REPARSE_INVALID"
                        SqlDiagnosticStage.Parse SqlDiagnosticCategory.Syntax ex.Message
                SqlCatalogBindingStatus.Rejected, SqlStatementAnalysisStatus.Invalid,
                SqlCatalogKnowledge.Unknown, [diagnostic]
