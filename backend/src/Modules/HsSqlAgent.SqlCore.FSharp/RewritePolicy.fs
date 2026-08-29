namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Execution policy is applied only after semantic validation.
/// Producing ExecutableSql is therefore impossible without a ValidatedSql value.
module internal RewritePolicy =

    type MutationSafety =
        | RequirePredicate
        | AllowAllRows

    type RowCap =
        | Unlimited
        | MaxRows of PositiveRowCount

    type ExecutionPolicy =
        { UpdateSafety: MutationSafety
          DeleteSafety: MutationSafety
          QueryRows: RowCap }
        member this.AllowUnboundedUpdate =
            match this.UpdateSafety with
            | AllowAllRows -> true
            | RequirePredicate -> false
        member this.AllowUnboundedDelete =
            match this.DeleteSafety with
            | AllowAllRows -> true
            | RequirePredicate -> false
        member this.QueryMaxRows =
            match this.QueryRows with
            | Unlimited -> 0
            | MaxRows count -> PositiveRowCount.value count

    let safeDefaults =
        { UpdateSafety = RequirePredicate
          DeleteSafety = RequirePredicate
          QueryRows = Unlimited }

    let private clampQueryLimit (rowCap: RowCap) (query: Query) =
        match rowCap with
        | Unlimited -> query
        | MaxRows maxRows ->
            let maxValue = PositiveRowCount.value maxRows
            let limit =
                match query.Limit with
                | None -> Some maxValue
                | Some value -> Some(min value maxValue)
            { query with Limit = limit }

    let private authorizeDocument policy document =
        let statement =
            match document.Statement with
            | QueryStatement query ->
                QueryStatement(clampQueryLimit policy.QueryRows query)
            | UpdateStatement update when update.Where.IsNone && policy.UpdateSafety = RequirePredicate ->
                invalidOp "Execution policy rejects UPDATE without a WHERE predicate."
            | DeleteStatement delete when delete.Where.IsNone && policy.DeleteSafety = RequirePredicate ->
                invalidOp "Execution policy rejects DELETE without a WHERE predicate."
            | statement -> statement
        { document with Statement = statement }

    let authorize policy validated =
        Transition.authorize (authorizeDocument policy) validated
