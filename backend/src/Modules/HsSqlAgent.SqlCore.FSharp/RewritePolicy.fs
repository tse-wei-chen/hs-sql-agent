namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Execution policy is applied only after semantic validation.
/// Producing ExecutableSql is therefore impossible without a ValidatedSql value.
module internal RewritePolicy =

    type ExecutionPolicy =
        { AllowUnboundedUpdate: bool
          AllowUnboundedDelete: bool }

    let safeDefaults =
        { AllowUnboundedUpdate = false
          AllowUnboundedDelete = false }

    let private authorizeDocument policy document =
        match document.Statement with
        | UpdateStatement update when update.Where.IsNone && not policy.AllowUnboundedUpdate ->
            invalidOp "Execution policy rejects UPDATE without a WHERE predicate."
        | DeleteStatement delete when delete.Where.IsNone && not policy.AllowUnboundedDelete ->
            invalidOp "Execution policy rejects DELETE without a WHERE predicate."
        | _ -> ()
        document

    let authorize policy validated =
        Transition.authorize (authorizeDocument policy) validated
