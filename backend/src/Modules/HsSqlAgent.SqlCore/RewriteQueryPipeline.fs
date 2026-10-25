namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy

/// Domain-owned typestates: Query and Mutation authorization cannot be interchanged.
/// The constructors are private and the only entry is a semantically validated SQL plan.
module internal RewriteQueryPipeline =

    type ValidatedQuery = private ValidatedQuery of RewriteStages.ValidatedSql
    type AuthorizedQuery = private AuthorizedQuery of RewritePolicy.ExecutableSql

    let internal fromValidated (validated: RewriteStages.ValidatedSql) =
        match (RewriteStages.Validated.value validated).Statement with
        | QueryStatement _ -> ValidatedQuery validated
        | _ -> invalidOp "Mutation statement cannot enter the query compiler policy."

    let internal authorize (rowCap: RowCap) (ValidatedQuery validated) =
        AuthorizedQuery(RewriteQueryPolicy.apply rowCap validated)

    /// The renderer receives a token produced only by this family's policy.
    let internal render renderAuthorized (AuthorizedQuery executable) =
        renderAuthorized executable
