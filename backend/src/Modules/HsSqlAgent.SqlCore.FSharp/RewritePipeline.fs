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
          TargetJoins: JoinProofs
          ConflictProofs: ConflictProofs
          Policy: ExecutionPolicy
          AllowedTables: string list option }

    let compile options sql =
        sql
        |> RewriteParser.parseForWith options.SourceSemantics options.SourceDialect
        |> RewriteBinder.bind
        |> RewriteStages.normalize
        |> RewriteStages.validate options.AllowedTables options.TargetRuntime options.TargetJoins options.ConflictProofs
        |> RewritePolicy.authorize options.Policy
        |> RewriteRenderer.render options.Provider
