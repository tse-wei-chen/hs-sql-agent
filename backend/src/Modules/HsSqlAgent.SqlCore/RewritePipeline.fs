namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewriteParser
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy
open HsSqlAgent.SqlCore.Rewrite.RewriteRenderer
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Single rewrite compiler entry point. Stage order is fixed by typestate signatures.
module internal RewritePipeline =

    /// Source dialect identity, semantics, and profile travel as one verified compiler input.
    /// The private representation prevents callers from record-copying one field independently.
    type VerifiedSource =
        private
            { Dialect: SourceDialect
              Semantics: SourceSemantics
              Profile: SqlProviderCapabilityProfile option }

    module VerifiedSource =
        let internal create dialect semantics (profile: SqlProviderCapabilityProfile | null) =
            { Dialect = dialect
              Semantics = semantics
              Profile = Option.ofObj profile }

        let internal dialect source = source.Dialect
        let internal semantics source = source.Semantics
        let internal profile source = source.Profile |> Option.toObj

    /// Target runtime identity and every target capability proof travel as one value.
    /// Renderer identity is derived from Runtime, so proofs can no longer be detached from
    /// the target context by CompileOptions record updates.
    type VerifiedTarget =
        private
            { Runtime: TargetRuntime
              Profile: SqlProviderCapabilityProfile option
              Expressions: ExpressionProofs
              Joins: JoinProofs
              Ordering: TargetNullOrdering
              Dml: DmlProofs }

    module VerifiedTarget =
        let internal create runtime (profile: SqlProviderCapabilityProfile | null) expressions joins ordering dml =
            { Runtime = runtime
              Profile = Option.ofObj profile
              Expressions = expressions
              Joins = joins
              Ordering = ordering
              Dml = dml }

        let internal runtime target = target.Runtime
        let internal profile target = target.Profile |> Option.toObj
        let internal expressions target = target.Expressions
        let internal joins target = target.Joins
        let internal ordering target = target.Ordering
        let internal dml target = target.Dml

    type CompileOptions =
        private
            { Source: VerifiedSource
              Target: VerifiedTarget
              ConflictProofs: ConflictProofs
              Policy: ExecutionPolicy
              AllowedTables: string list option
              SchemaSnapshot: SqlSchemaSnapshot option }

    let internal createOptions source target conflictProofs policy allowedTables (schemaSnapshot: SqlSchemaSnapshot | null) =
        { Source = source
          Target = target
          ConflictProofs = conflictProofs
          Policy = policy
          AllowedTables = allowedTables
          SchemaSnapshot = Option.ofObj schemaSnapshot }

    let private sourceProvider = function
        | SourceDialect.PostgreSql -> SqlAgentToolType.Postgres
        | SourceDialect.MySql -> SqlAgentToolType.MySQL
        | SourceDialect.SqlServer -> SqlAgentToolType.MsSqlServer
        | SourceDialect.SQLite -> SqlAgentToolType.Sqlite
        | SourceDialect.Oracle -> SqlAgentToolType.Oracle
        | SourceDialect.Firebird -> SqlAgentToolType.Firebird

    let private diagnosticDataKey = "HsSqlAgent.SqlCore.Diagnostic"

    let private renderWithDiagnostic executable =
        let document = RewritePolicy.Executable.value executable
        let diagnosticSpan =
            if document.Span.Start < 0 || document.Span.Length < 0 then null
            else SqlDiagnosticSpan(document.Span.Start, document.Span.Length)
        try
            RewriteRenderer.render executable
        with
        | :? SqlCompilationException as ex when isNull ex.Diagnostic ->
            let diagnostic =
                SqlDiagnostic(
                    "SQL_RENDERING_INVARIANT",
                    SqlDiagnosticStage.RenderingInvariant,
                    SqlDiagnosticCategory.Invariant,
                    ex.Message,
                    diagnosticSpan)
            raise (SqlCompilationException(ex.Message, ex, diagnostic))
        | :? InvalidOperationException as ex ->
            let diagnostic =
                SqlDiagnostic(
                    "SQL_RENDERING_INVARIANT",
                    SqlDiagnosticStage.RenderingInvariant,
                    SqlDiagnosticCategory.Invariant,
                    ex.Message,
                    diagnosticSpan)
            ex.Data[diagnosticDataKey] <- diagnostic
            reraise()

    let private bind options parsed =
        let sourceProviderType =
            options.Source
            |> VerifiedSource.dialect
            |> sourceProvider
        let bound =
            RewriteResourceBudget.withStage "Binding" SqlDiagnosticStage.Binding (fun () ->
                parsed |> RewriteBinder.bind sourceProviderType)
        match options.SchemaSnapshot with
        | None -> bound
        | Some snapshot ->
            let schemaChecked =
                RewriteResourceBudget.withStage "SchemaBinding" SqlDiagnosticStage.Binding (fun () ->
                    RewriteSchemaValidation.verify sourceProviderType snapshot bound)
            RewriteResourceBudget.withStage "SchemaType" SqlDiagnosticStage.SemanticValidation (fun () ->
                RewriteSchemaTypeValidation.verify sourceProviderType snapshot schemaChecked)

    /// Shared semantic engine, but without family-agnostic authorization.
    /// Domain wrappers verify kind at Parse, Bind, Canonical and Validated
    /// transitions; no Query policy can consume a Mutation stage (or vice versa).
    let private normalize options bound =
        let source = options.Source
        let target = options.Target
        let sourceDialect = VerifiedSource.dialect source
        let sourceSemantics = VerifiedSource.semantics source
        let targetRuntime = VerifiedTarget.runtime target
        RewriteResourceBudget.withStage "Normalization" SqlDiagnosticStage.SemanticValidation (fun () ->
            RewriteStages.normalize
                sourceSemantics.EnforceDialectSyntax
                sourceDialect targetRuntime
                sourceSemantics.Expressions.RegexMatch
                sourceSemantics.Ordering
                sourceSemantics.MySqlPipes
                (VerifiedSource.profile source)
                (VerifiedTarget.profile target)
                bound)

    let private validate options canonical =
        let sourceSemantics = VerifiedSource.semantics options.Source
        let target = options.Target
        RewriteResourceBudget.withStage "Validation" SqlDiagnosticStage.SemanticValidation (fun () ->
            RewriteStages.validate
                options.AllowedTables (VerifiedTarget.runtime target)
                (VerifiedTarget.profile target)
                sourceSemantics.Expressions (VerifiedTarget.expressions target)
                sourceSemantics.Joins (VerifiedTarget.joins target)
                (VerifiedTarget.ordering target)
                sourceSemantics.Dml (VerifiedTarget.dml target)
                options.ConflictProofs canonical)

    let private finishQueryBound options (bound: RewriteQueryPipeline.BoundQuery) =
        let canonical = RewriteQueryPipeline.normalize (normalize options) bound
        let validated = RewriteQueryPipeline.validate (validate options) canonical
        let authorized =
            RewriteResourceBudget.withStage "Policy" SqlDiagnosticStage.Policy (fun () ->
                RewriteQueryPipeline.authorize options.Policy.QueryRows validated)
        RewriteResourceBudget.withStage "Rendering" SqlDiagnosticStage.RenderingInvariant (fun () ->
            RewriteQueryPipeline.render renderWithDiagnostic authorized)

    let private finishMutationBound options (bound: RewriteMutationPipeline.BoundMutation) =
        let canonical = RewriteMutationPipeline.normalize (normalize options) bound
        let validated = RewriteMutationPipeline.validate (validate options) canonical
        let authorized =
            RewriteResourceBudget.withStage "Policy" SqlDiagnosticStage.Policy (fun () ->
                RewriteMutationPipeline.authorize
                    options.Policy.UpdateSafety
                    options.Policy.DeleteSafety
                    validated)
        RewriteResourceBudget.withStage "Rendering" SqlDiagnosticStage.RenderingInvariant (fun () ->
            RewriteMutationPipeline.render renderWithDiagnostic authorized)

    /// Compatibility entry for the Query Inspection path, which has already
    /// bound the AST to collect facts before running normalization/validation.
    let private finishBound options bound =
        match (Bound.value bound).Statement with
        | QueryStatement _ ->
            RewriteQueryPipeline.fromBound bound
            |> finishQueryBound options
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            RewriteMutationPipeline.fromBound bound
            |> finishMutationBound options

    let private finish options parsed =
        match (Parsed.value parsed).Statement with
        | QueryStatement _ ->
            RewriteQueryPipeline.fromParsed parsed
            |> RewriteQueryPipeline.bind (bind options)
            |> finishQueryBound options
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            RewriteMutationPipeline.fromParsed parsed
            |> RewriteMutationPipeline.bind (bind options)
            |> finishMutationBound options

    let private parse options sql =
        let source = options.Source
        RewriteResourceBudget.withStage "Parsing" SqlDiagnosticStage.Parse (fun () ->
            RewriteParser.parseForWith
                (VerifiedSource.semantics source)
                (VerifiedSource.dialect source)
                sql)

    let compileParsed options parsed =
        RewriteResourceBudget.withCompilation (fun () -> finish options parsed)

    let compileWithParsed options sql =
        RewriteResourceBudget.withCompilation (fun () ->
            let parsed = parse options sql
            parsed, finish options parsed)

    let compileWithQueryFacts options sql =
        RewriteResourceBudget.withCompilation (fun () ->
            let parsed = parse options sql
            let bound = bind options parsed
            let facts =
                RewriteResourceBudget.withStage "Inspection" SqlDiagnosticStage.Binding (fun () ->
                    RewriteInspection.inspectBound bound)
            try
                parsed, facts, finishBound options bound
            with ex ->
                RewriteInspection.attachToException facts ex
                reraise())

    let compile options sql =
        compileWithParsed options sql |> snd
