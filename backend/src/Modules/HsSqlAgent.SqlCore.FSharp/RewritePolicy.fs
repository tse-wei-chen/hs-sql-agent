namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Execution policy is applied only after semantic validation.
/// Producing ExecutableSql is therefore impossible without a ValidatedSql value.
module internal RewritePolicy =

    type ExecutionPolicy =
        { AllowUnboundedUpdate: bool
          AllowUnboundedDelete: bool
          QueryMaxRows: int }

    let safeDefaults =
        { AllowUnboundedUpdate = false
          AllowUnboundedDelete = false
          QueryMaxRows = 0 }

    let private clampQueryLimit (maxRows: int) (query: Query) =
        if maxRows <= 0 then query
        else
            let limit =
                match query.Limit with
                | None -> Some maxRows
                | Some value -> Some(min value maxRows)
            { query with Limit = limit }

    let private authorizeDocument policy document =
        let statement =
            match document.Statement with
            | QueryStatement query ->
                QueryStatement(clampQueryLimit policy.QueryMaxRows query)
            | UpdateStatement update when update.Where.IsNone && not policy.AllowUnboundedUpdate ->
                invalidOp "Execution policy rejects UPDATE without a WHERE predicate."
            | DeleteStatement delete when delete.Where.IsNone && not policy.AllowUnboundedDelete ->
                invalidOp "Execution policy rejects DELETE without a WHERE predicate."
            | statement -> statement
        { document with Statement = statement }

    let authorize policy validated =
        Transition.authorize (authorizeDocument policy) validated
