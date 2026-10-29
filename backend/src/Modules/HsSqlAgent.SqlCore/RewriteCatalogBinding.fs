namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.SqlParsing

/// Non-executable strict AST name and type proofs against the *ordered* virtual
/// catalog. Source-specific identifier/collation and affinity assumptions are
/// explicit: a query must not receive a complete Verified result from a
/// partially-modeled provider.
module internal RewriteCatalogBinding =

    let private fallbackMatch (syntax: SqlScriptStatementSyntax) =
        if syntax.Kind = SqlScriptStatementKind.Mutation then
            SqlMutationSourceMatchCardinality.Unverified
        else SqlMutationSourceMatchCardinality.NotApplicable

    let private report (syntax: SqlScriptStatementSyntax) code stage category message =
        SqlDiagnostic(code, stage, category, message, syntax.Span)

    let private statusForCode (code: string) =
        if code = "SQL_SCHEMA_SHAPE_UNPROVABLE"
           || code = "SQL_SCHEMA_MERGE_UNPROVABLE"
           || code = "SQL_SCHEMA_CONFLICT_UNPROVABLE" then
            SqlCatalogBindingStatus.Unverified
        else SqlCatalogBindingStatus.Rejected

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
        let nameStatus, typeStatus =
            if diagnostic.Code = "SQL_SCHEMA_TYPE_MISMATCH" then
                SqlCatalogBindingStatus.Verified, binding
            else
                binding, SqlCatalogBindingStatus.NotApplicable
        binding, nameStatus, typeStatus, analysis, SqlCatalogKnowledge.Unknown, [adjusted],
        fallbackMatch syntax

    let private unverified syntax code message =
        let d = report syntax code SqlDiagnosticStage.Binding SqlDiagnosticCategory.Capability message
        SqlCatalogBindingStatus.Unverified, SqlCatalogBindingStatus.Unverified,
        SqlCatalogBindingStatus.Unverified, SqlStatementAnalysisStatus.PartiallyAnalyzed,
        SqlCatalogKnowledge.Unknown, [d], fallbackMatch syntax

    let private typeUnverified syntax code message =
        let d = report syntax code SqlDiagnosticStage.SemanticValidation SqlDiagnosticCategory.Capability message
        SqlCatalogBindingStatus.Unverified, SqlCatalogBindingStatus.Verified,
        SqlCatalogBindingStatus.Unverified, SqlStatementAnalysisStatus.PartiallyAnalyzed,
        SqlCatalogKnowledge.Unknown, [d], fallbackMatch syntax

    let private verified sourceMatch =
        SqlCatalogBindingStatus.Verified, SqlCatalogBindingStatus.Verified,
        SqlCatalogBindingStatus.Verified, SqlStatementAnalysisStatus.PartiallyAnalyzed,
        SqlCatalogKnowledge.Known, [], sourceMatch

    let verify sourceDialect (profile: SqlProviderCapabilityProfile | null)
               (scoped: RewriteCatalogSchemaResolution.SchemaScope)
               (state: SqlCatalogState) (syntax: SqlScriptStatementSyntax) =
        if not state.IsClosedWorld
           || (state.Tables |> Seq.exists (fun t -> t.Knowledge <> SqlCatalogKnowledge.Known)) then
            unverified syntax "SQL_CATALOG_BINDING_INCOMPLETE"
                "Catalog is open or incomplete; referenced names and types cannot be proven."
        else
            try
                let parsed =
                    RewriteResourceBudget.withStage "CatalogParsing" SqlDiagnosticStage.Parse (fun () ->
                        RewriteFacadeAdapter.parseSourceValidated syntax.Text sourceDialect profile)
                let bound =
                    RewriteResourceBudget.withStage "CatalogBinding" SqlDiagnosticStage.Binding (fun () ->
                        RewriteBinder.bind sourceDialect parsed)
                // The Source inspection AST visitor is iterative, does not
                // recurse through CTEs/subqueries and includes Mutation targets.
                let schemaScopeProven =
                    match scoped with
                    | RewriteCatalogSchemaResolution.SingleSchema _
                    | RewriteCatalogSchemaResolution.SingleDatabase _ -> true
                    | _ -> false
                let catalogForBinding =
                    if schemaScopeProven && RewriteCatalogSchemaResolution.canProject scoped state then
                        RewriteCatalogSchemaResolution.projectForBinding scoped state
                    else state
                let inspected = RewriteInspection.inspectBound bound
                let physicalTableCount = inspected.ReferencedTables.Count
                let defaultSchemaAmbiguity =
                    inspected.ReferencedTables
                    |> Seq.exists (fun reference ->
                        not (reference.Contains(".", StringComparison.Ordinal))
                        && (state.Tables |> Seq.exists (fun table ->
                            table.QualifiedName.IsQualified
                            && String.Equals(
                                table.QualifiedName.Parts[table.QualifiedName.Parts.Count - 1],
                                reference, StringComparison.OrdinalIgnoreCase))))
                // MySQL table casing is governed by lower_case_table_names /
                // server filesystem; SQL Server identifiers by DB collation.
                // SqlProviderCapabilityProfile has neither proof. A generic
                // uppercasing schema binder cannot safely certify such names.
                if schemaScopeProven && not (RewriteCatalogSchemaResolution.canProject scoped state) then
                    unverified syntax "SQL_CATALOG_SCHEMA_SCOPE_UNPROVEN"
                        "Declared search path cannot be safely projected over caller-provided unqualified identities."
                elif schemaScopeProven &&
                     (inspected.ReferencedTables |> Seq.exists (fun t ->
                         not (t.Contains(".", StringComparison.Ordinal))
                         && t.StartsWith("pg_", StringComparison.OrdinalIgnoreCase))) then
                    unverified syntax "SQL_CATALOG_SCHEMA_SYSTEM_UNPROVEN"
                        "PostgreSQL system-catalog implicit search scope is not modeled."
                elif defaultSchemaAmbiguity && not schemaScopeProven then
                    unverified syntax "SQL_CATALOG_DEFAULT_SCHEMA_UNPROVEN"
                        "Unqualified physical table reference may resolve through an undeclared search_path or default schema."
                elif physicalTableCount > 0
                     && not (RewriteCatalogIdentifierProof.canProvePhysicalNames
                                sourceDialect profile state syntax.Text) then
                    unverified syntax "SQL_CATALOG_BINDING_IDENTIFIER_RULES_UNPROVEN"
                        "Provider identifier case/collation proof is absent or outside the restricted ASCII-safe subset; a normalized match is not authoritative."
                else
                    let namesChecked =
                        RewriteResourceBudget.withStage "CatalogNameValidation" SqlDiagnosticStage.Binding (fun () ->
                            RewriteSchemaValidation.verifyVirtual sourceDialect catalogForBinding bound)
                    if sourceDialect = SqlAgentToolType.Sqlite && physicalTableCount > 0 then
                        // SQLite storage classes have dynamic affinity: an
                        // INTEGER column does not establish static Numeric-only
                        // assignment semantics. Its names can still be proven.
                        typeUnverified syntax "SQL_CATALOG_TYPE_SQLITE_AFFINITY_UNPROVEN"
                            "SQLite column affinity is dynamic; table/column names are verified but assignment types are not."
                    else
                        RewriteResourceBudget.withStage "CatalogTypeValidation" SqlDiagnosticStage.SemanticValidation (fun () ->
                            RewriteSchemaTypeValidation.verifyVirtual sourceDialect catalogForBinding namesChecked)
                        |> ignore
                        verified (RewriteMutationSourceMatchProof.classify
                                      sourceDialect catalogForBinding bound)
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
                SqlCatalogBindingStatus.Rejected, SqlCatalogBindingStatus.Rejected,
                SqlCatalogBindingStatus.NotApplicable, SqlStatementAnalysisStatus.Invalid,
                SqlCatalogKnowledge.Unknown, [diagnostic], fallbackMatch syntax
