namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.RewriteParser
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy
open HsSqlAgent.SqlCore.Rewrite.RewriteRenderer
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Single rewrite compiler entry point. Stage order is fixed by typestate signatures.
module internal RewritePipeline =

    type CompileOptions =
        { SourceDialect: SourceDialect
          SourceSemantics: SourceSemantics
          Provider: Provider
          TargetRuntime: TargetRuntime
          TargetExpressions: ExpressionProofs
          TargetJoins: JoinProofs
          TargetOrdering: TargetNullOrdering
          TargetDml: DmlProofs
          ConflictProofs: ConflictProofs
          Policy: ExecutionPolicy
          AllowedTables: string list option }

    let private finish options parsed =
        parsed
        |> RewriteBinder.bind
        |> RewriteStages.normalize options.SourceDialect options.TargetRuntime options.SourceSemantics.Expressions.RegexMatch
        |> RewriteStages.validate options.AllowedTables options.TargetRuntime options.SourceSemantics.Expressions options.TargetExpressions options.TargetJoins options.TargetOrdering options.TargetDml options.ConflictProofs
        |> RewritePolicy.authorize options.Policy
        |> RewriteRenderer.render options.Provider

    let compileParsed options parsed =
        finish options parsed

    let compile options sql =
        sql
        |> RewriteParser.parseForWith options.SourceSemantics options.SourceDialect
        |> finish options
