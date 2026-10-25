namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy

/// Query-only row-limit policy: cannot receive a Mutation AST as a valid query.
module internal RewriteQueryPolicy =

    let private deny = RewritePolicy.deny

    let private clampQueryLimit span (rowCap: RowCap) (query: Query) =
        match rowCap with
        | Unlimited -> query
        | MaxRows _ when query.FetchPercent.IsSome ->
            deny
                "SQL_POLICY_QUERY_MAX_ROWS_UNPROVABLE"
                span
                ("Security policy QueryMaxRows is a hard row cap, but FETCH ... PERCENT is relative to the query result cardinality. "
                 + "SQL capability 'select.fetch_percent' therefore remains policy-denied when QueryMaxRows is enabled.")
        | MaxRows _ when query.FetchWithTies ->
            deny
                "SQL_POLICY_QUERY_MAX_ROWS_UNPROVABLE"
                span
                ("Security policy QueryMaxRows is a hard row cap, but FETCH ... WITH TIES can return more rows than its FETCH count. "
                 + "SQL capability 'select.fetch_with_ties' therefore remains policy-denied when QueryMaxRows is enabled.")
        | MaxRows maxRows ->
            let maxValue = PositiveRowCount.value maxRows
            let limit =
                match query.Limit with
                | None -> Some(NonNegativeRowCount.create maxValue)
                | Some value -> Some(NonNegativeRowCount.create (min (NonNegativeRowCount.value value) maxValue))
            { query with Limit = limit }


    let apply (rowCap: RowCap) (validated: RewriteStages.ValidatedSql) =
        RewriteResourceBudget.checkpoint ()
        let document = RewriteStages.Validated.value validated
        match document.Statement with
        | QueryStatement query ->
            QueryStatement(clampQueryLimit document.Span rowCap query)
            |> RewritePolicy.Executable.create validated
        | _ -> invalidOp "Query policy cannot authorize a mutation statement."
