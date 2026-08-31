namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewritePolicy =

    type ExecutableSql = private ExecutableSql of Document * TargetRuntime

    module Executable =
        let internal value (ExecutableSql(document, _)) = document
        let internal targetRuntime (ExecutableSql(_, targetRuntime)) = targetRuntime

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

    let private deny code (span: Span) message : 'T =
        let diagnostic =
            SqlDiagnostic(
                code,
                SqlDiagnosticStage.Policy,
                SqlDiagnosticCategory.Policy,
                message,
                SqlDiagnosticSpan(span.Start, span.Length))
        raise (SqlPolicyException(message, diagnostic))

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

    let private authorizeDocument policy document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(clampQueryLimit document.Span policy.QueryRows query)
            | UpdateStatement update when update.Where.IsNone && policy.UpdateSafety = RequirePredicate ->
                deny "SQL_POLICY_UPDATE_REQUIRES_WHERE" document.Span "Security policy denies UPDATE without WHERE."
            | DeleteStatement delete when delete.Where.IsNone && policy.DeleteSafety = RequirePredicate ->
                deny "SQL_POLICY_DELETE_REQUIRES_WHERE" document.Span "Security policy denies DELETE without WHERE."
            | statement -> statement
        { document with Statement = statement }

    let authorize policy (validated: RewriteStages.ValidatedSql) =
        let targetRuntime = RewriteStages.Validated.targetRuntime validated
        let document = RewriteStages.Validated.value validated
        ExecutableSql(authorizeDocument policy document, targetRuntime)
