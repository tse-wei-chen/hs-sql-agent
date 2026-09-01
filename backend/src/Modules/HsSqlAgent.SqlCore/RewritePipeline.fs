namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
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
              AllowedTables: string list option }

    let internal createOptions source target conflictProofs policy allowedTables =
        { Source = source
          Target = target
          ConflictProofs = conflictProofs
          Policy = policy
          AllowedTables = allowedTables }

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

    let private finish options parsed =
        let source = options.Source
        let target = options.Target
        let sourceDialect = VerifiedSource.dialect source
        let sourceSemantics = VerifiedSource.semantics source
        let targetRuntime = VerifiedTarget.runtime target

        parsed
        |> RewriteBinder.bind (sourceProvider sourceDialect)
        |> RewriteStages.normalize
            sourceSemantics.EnforceDialectSyntax
            sourceDialect
            targetRuntime
            sourceSemantics.Expressions.RegexMatch
            sourceSemantics.Ordering
            sourceSemantics.MySqlPipes
            (VerifiedSource.profile source)
            (VerifiedTarget.profile target)
        |> RewriteStages.validate
            options.AllowedTables
            targetRuntime
            sourceSemantics.Expressions
            (VerifiedTarget.expressions target)
            sourceSemantics.Joins
            (VerifiedTarget.joins target)
            (VerifiedTarget.ordering target)
            sourceSemantics.Dml
            (VerifiedTarget.dml target)
            options.ConflictProofs
        |> RewritePolicy.authorize options.Policy
        |> renderWithDiagnostic

    let compileParsed options parsed =
        finish options parsed

    let compileWithParsed options sql =
        let source = options.Source
        let parsed =
            RewriteParser.parseForWith
                (VerifiedSource.semantics source)
                (VerifiedSource.dialect source)
                sql
        parsed, finish options parsed

    let compile options sql =
        compileWithParsed options sql |> snd
