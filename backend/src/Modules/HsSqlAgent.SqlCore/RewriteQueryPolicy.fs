namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewritePolicy

/// Query-only row-limit policy: cannot receive a Mutation AST as a valid query.
module internal RewriteQueryPolicy =

    let private deny code span message : 'T = RewritePolicy.deny code span message

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


    // A table whitelist cannot see SQL executed inside PostgreSQL functions.
    // Only a small set of SQL-native, argument-local built-ins is admitted and
    // pinned to pg_catalog to prevent search_path/overload resolution to user UDFs.
    // No name blacklist can close this boundary (query_to_xml is one example).
    let private safePostgresFunctions =
        set [
            "ABS"; "ACOS"; "ASIN"; "ATAN"; "ATAN2"; "CEIL"; "CEILING"
            "COS"; "COT"; "DEGREES"; "EXP"; "FLOOR"; "LN"; "LOG"
            "MOD"; "POWER"; "RADIANS"; "ROUND"; "SIGN"; "SIN"
            "SQRT"; "TAN"; "TRUNC"
            "LOWER"; "UPPER"; "LENGTH"; "CHAR_LENGTH"; "OCTET_LENGTH"
            "BIT_LENGTH"; "BTRIM"; "LTRIM"; "RTRIM"; "TRIM"
            "SUBSTR"; "SUBSTRING"; "LEFT"; "RIGHT"; "REPLACE"
            "SPLIT_PART"; "STRPOS"; "CONCAT"; "CONCAT_WS"
            "MD5"; "ENCODE"; "DECODE"
            "DATE_TRUNC"; "DATE_PART"; "MAKE_DATE"; "MAKE_TIME"
            "TO_CHAR"; "TO_DATE"; "TO_TIMESTAMP"; "NOW"
            "COUNT"; "SUM"; "AVG"; "MIN"; "MAX"; "ARRAY_AGG"
            "STRING_AGG"; "BOOL_AND"; "BOOL_OR"; "EVERY"
            "STDDEV"; "STDDEV_POP"; "STDDEV_SAMP"
            "VARIANCE"; "VAR_POP"; "VAR_SAMP"
            "JSON_AGG"; "JSONB_AGG"; "JSON_BUILD_OBJECT"; "JSONB_BUILD_OBJECT"
            "JSON_ARRAY_LENGTH"; "JSONB_ARRAY_LENGTH"
            "JSON_EXTRACT_PATH"; "JSONB_EXTRACT_PATH"
        ]

    // PostgreSQL parses these as special SQL expressions, not overloadable
    // user-defined functions. They cannot be prefixed with pg_catalog.
    let private specialPostgresExpressions =
        set [ "COALESCE"; "NULLIF"; "GREATEST"; "LEAST" ]

    let private provePostgresFunctions (span: Span) query =
        let reject name =
            deny
                "SQL_POLICY_POSTGRES_QUERY_FUNCTION_NOT_PROVEN"
                span
                ("PostgreSQL Query with a table whitelist cannot authorize function '"
                 + name + "': SQL inside functions is invisible to table binding. "
                 + "Use a proven pg_catalog built-in or a database role restricted to authorized relations.")

        let mapExpr expression =
            match expression with
            | FunctionCall call ->
                let parts = FunctionName.parts call.Name
                let unquoted (part: IdentifierPart) =
                    not part.WasQuoted && not part.PreserveSpelling
                let functionName = FunctionName.value call.Name
                let nativeName =
                    match parts with
                    | [name] when unquoted name -> name.Value.ToUpperInvariant()
                    | [schema; name] when unquoted schema && unquoted name
                                          && String.Equals(schema.Value, "pg_catalog", StringComparison.OrdinalIgnoreCase) ->
                        name.Value.ToUpperInvariant()
                    | _ -> reject functionName
                if Set.contains nativeName safePostgresFunctions then
                    FunctionCall { call with Name = FunctionName.create ("pg_catalog." + nativeName) }
                elif parts.Length = 1 && Set.contains nativeName specialPostgresExpressions then
                    expression
                else
                    reject functionName
            | _ -> expression

        RewriteAstTraversal.mapQueryGraphPostOrder
            ignore mapExpr ignore id query

    let apply (rowCap: RowCap) requirePostgresFunctionProof (validated: RewriteStages.ValidatedSql) =
        RewriteResourceBudget.checkpoint ()
        let document = RewriteStages.Validated.value validated
        match document.Statement with
        | QueryStatement query ->
            let bounded = clampQueryLimit document.Span rowCap query
            let guarded = if requirePostgresFunctionProof then provePostgresFunctions document.Span bounded else bounded
            QueryStatement guarded
            |> RewritePolicy.Executable.create validated
        | _ -> invalidOp "Query policy cannot authorize a mutation statement."
