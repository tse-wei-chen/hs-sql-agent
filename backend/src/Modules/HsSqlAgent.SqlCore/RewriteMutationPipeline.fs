namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy

/// Domain-owned typestates: Query and Mutation authorization cannot be interchanged.
/// The constructors are private and the only entry is a semantically validated SQL plan.
module internal RewriteMutationPipeline =

    type ValidatedMutation = private ValidatedMutation of RewriteStages.ValidatedSql
    type AuthorizedMutation = private AuthorizedMutation of RewritePolicy.ExecutableSql

    let internal fromValidated (validated: RewriteStages.ValidatedSql) =
        match (RewriteStages.Validated.value validated).Statement with
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _ | MergeStatement _ ->
            ValidatedMutation validated
        | QueryStatement _ -> invalidOp "Query statement cannot enter the mutation compiler policy."

    let internal authorize (updateSafety: MutationSafety) (deleteSafety: MutationSafety) (ValidatedMutation validated) =
        AuthorizedMutation(RewriteMutationPolicy.apply updateSafety deleteSafety validated)

    /// The renderer receives a token produced only by this family's policy.
    let internal render renderAuthorized (AuthorizedMutation executable) =
        renderAuthorized executable
