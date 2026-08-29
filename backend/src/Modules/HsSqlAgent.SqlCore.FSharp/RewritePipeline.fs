namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.RewritePolicy
open HsSqlAgent.SqlCore.Rewrite.RewriteRenderer

/// Single rewrite compiler entry point. Stage order is fixed by typestate signatures.
module internal RewritePipeline =

    type CompileOptions =
        { Provider: Provider
          Policy: ExecutionPolicy
          AllowedTables: string list option }

    let compile options sql =
        sql
        |> RewriteParser.parse
        |> RewriteBinder.bind
        |> RewriteStages.normalize
        |> RewriteStages.validate options.AllowedTables
        |> RewritePolicy.authorize options.Policy
        |> RewriteRenderer.render options.Provider
