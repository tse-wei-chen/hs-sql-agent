namespace HsSqlAgent.SqlCore.Internal

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Pipeline

/// Query execution-policy rewrite implemented in F#.
module internal FunctionalExecutionPolicyRewriter =

    let private clampLimit
        (requested: Nullable<int>)
        maxRows =

        if requested.HasValue then
            if requested.Value = 0 then
                0
            elif requested.Value > 0 then
                Math.Min(
                    requested.Value,
                    maxRows)
            else
                maxRows
        else
            maxRows

    let private applyMaxRows
        (statement: SqlStatement)
        maxRows =

        if maxRows <= 0 then
            statement
        else
            match statement with
            | :? SelectStatement as select ->
                SelectStatement(
                    select.Ctes,
                    select.Distinct,
                    select.Select,
                    select.From,
                    select.Joins,
                    select.Where,
                    select.GroupBy,
                    select.Having,
                    select.OrderBy,
                    Nullable<int>(
                        clampLimit
                            select.Limit
                            maxRows),
                    select.Offset,
                    select.Span)
                :> SqlStatement

            | :? QueryStatement as query ->
                QueryStatement(
                    query.Head,
                    query.SetOperations,
                    query.OrderBy,
                    Nullable<int>(
                        clampLimit
                            query.Limit
                            maxRows),
                    query.Offset,
                    query.Span)
                :> SqlStatement

            | other ->
                raise (InvalidOperationException(
                    $"Unsupported statement during execution policy rewrite: {other.GetType().Name}"))

    let rewrite
        (plan: ValidatedSqlPlan)
        (policy: SqlExecutionPlanPolicy) =

        let statement =
            if policy.QueryMaxRows > 0 then
                applyMaxRows
                    plan.Statement
                    policy.QueryMaxRows
            else
                plan.Statement

        ExecutableSqlPlan(
            statement,
            plan.Facts,
            plan.SourceDialect,
            plan.TargetProvider,
            plan.PolicyVersion)
