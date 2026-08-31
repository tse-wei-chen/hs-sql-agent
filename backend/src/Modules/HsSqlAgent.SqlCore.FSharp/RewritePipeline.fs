namespace HsSqlAgent.SqlCore.Rewrite

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
              Profile: SqlProviderCapabilityProfile | null }

    module VerifiedSource =
        let internal create dialect semantics profile =
            { Dialect = dialect
              Semantics = semantics
              Profile = profile }

        let internal dialect source = source.Dialect
        let internal semantics source = source.Semantics
        let internal profile source = source.Profile

    /// Target runtime identity and every target capability proof travel as one value.
    /// Renderer identity is derived from Runtime, so proofs can no longer be detached from
    /// the target context by CompileOptions record updates.
    type VerifiedTarget =
        private
            { Runtime: TargetRuntime
              Profile: SqlProviderCapabilityProfile | null
              Expressions: ExpressionProofs
              Joins: JoinProofs
              Ordering: TargetNullOrdering
              Dml: DmlProofs }

    module VerifiedTarget =
        let internal create runtime profile expressions joins ordering dml =
            { Runtime = runtime
              Profile = profile
              Expressions = expressions
              Joins = joins
              Ordering = ordering
              Dml = dml }

        let internal runtime target = target.Runtime
        let internal profile target = target.Profile
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
        |> RewriteRenderer.render

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
