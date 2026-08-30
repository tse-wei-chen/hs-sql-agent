namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewritePolicy =

    type MutationSafety = RequirePredicate | AllowAllRows
    type RowCap = Unlimited | MaxRows of PositiveRowCount

    type ExecutionPolicy =
        { UpdateSafety: MutationSafety
          DeleteSafety: MutationSafety
          QueryRows: RowCap }
        member this.AllowUnboundedUpdate = this.UpdateSafety = AllowAllRows
        member this.AllowUnboundedDelete = this.DeleteSafety = AllowAllRows
        member this.QueryMaxRows = match this.QueryRows with Unlimited -> 0 | MaxRows count -> PositiveRowCount.value count

    let safeDefaults = { UpdateSafety = RequirePredicate; DeleteSafety = RequirePredicate; QueryRows = Unlimited }

    let private clampQueryLimit (rowCap: RowCap) (query: Query) =
        match rowCap with
        | Unlimited -> query
        | MaxRows maxRows ->
            let maxValue = PositiveRowCount.value maxRows
            let limit =
                match query.Limit with
                | None -> Some(NonNegativeRowCount.create maxValue)
                | Some value -> Some(NonNegativeRowCount.create (min (NonNegativeRowCount.value value) maxValue))
            { query with Limit = limit }

    let private authorizeDocument policy document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(clampQueryLimit policy.QueryRows query)
            | UpdateStatement update when update.Where.IsNone && policy.UpdateSafety = RequirePredicate -> raise (UnauthorizedAccessException("Security policy denies UPDATE without WHERE."))
            | DeleteStatement delete when delete.Where.IsNone && policy.DeleteSafety = RequirePredicate -> raise (UnauthorizedAccessException("Security policy denies DELETE without WHERE."))
            | statement -> statement
        { document with Statement = statement }

    let authorize policy validated = Transition.authorize (authorizeDocument policy) validated
