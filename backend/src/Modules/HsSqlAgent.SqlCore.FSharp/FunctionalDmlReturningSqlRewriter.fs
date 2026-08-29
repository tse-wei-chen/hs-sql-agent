namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Execution
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

module private FunctionalDmlReturningSqlRewriter =

    let private returningItems (statement: SqlStatement) =
        match statement with
        | :? InsertStatement as insert -> insert.Returning
        | :? UpdateStatement as update -> update.Returning
        | :? DeleteStatement as delete -> delete.Returning
        | _ -> ImmutableArray<DmlReturningItem>.Empty

    let private restoreConflictClauseOrder (sql: string) provider =
        let returningToken = " RETURNING "
        let returningIndex = sql.IndexOf(returningToken, StringComparison.OrdinalIgnoreCase)
        if returningIndex < 0 then
            sql
        else
            let trailingClause : string | null =
                match provider with
                | SqlAgentToolType.Postgres
                | SqlAgentToolType.Sqlite -> " ON CONFLICT "
                | SqlAgentToolType.Firebird -> " MATCHING "
                | _ -> null

            match trailingClause with
            | null -> sql
            | clause ->
                let clauseIndex =
                    sql.IndexOf(
                        clause,
                        returningIndex + returningToken.Length,
                        StringComparison.OrdinalIgnoreCase)

                if clauseIndex < 0 then
                    sql
                else
                    let beforeReturning = sql[.. returningIndex - 1]
                    let returning = sql[returningIndex .. clauseIndex - 1]
                    let conflict = sql[clauseIndex..]
                    beforeReturning + conflict + returning

    let private cloneCommand
        (command: CompiledSqlCommand)
        sql
        returnsRows
        planFingerprint =

        CompiledSqlCommand(
            sql,
            command.Parameters,
            command.Kind,
            planFingerprint,
            command.TargetProvider,
            ReturnsRows = returnsRows)

    let apply
        (command: CompiledSqlCommand)
        (statement: SqlStatement)
        (targetProfile: SqlProviderCapabilityProfile | null)
        (policyVersion: string) =

        ArgumentNullException.ThrowIfNull(command)
        ArgumentNullException.ThrowIfNull(statement)
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion)

        let returning = returningItems statement
        if returning.IsDefaultOrEmpty then
            command
        else
            match SqlDmlReturningCapabilityRules.TargetValidationError(command.TargetProvider, targetProfile) with
            | null -> ()
            | error -> raise (SqlCompilationException(error))

            if not (command.Sql.Contains(" RETURNING ", StringComparison.OrdinalIgnoreCase)) then
                raise (SqlCompilationException(
                    "Native DML lowering did not render the canonical RETURNING projection before parameter finalization."))

            let reordered =
                cloneCommand
                    command
                    (restoreConflictClauseOrder command.Sql command.TargetProvider)
                    true
                    String.Empty

            let fingerprint =
                DmlFingerprintService.ComputePlanFingerprint(reordered, policyVersion)

            cloneCommand reordered reordered.Sql true fingerprint

[<AbstractClass; Sealed>]
type internal CoreDmlReturningSqlRewriter private () =
    static member Apply(
        command: CompiledSqlCommand,
        statement: SqlStatement,
        targetProfile: SqlProviderCapabilityProfile | null,
        policyVersion: string) =
        FunctionalDmlReturningSqlRewriter.apply
            command
            statement
            targetProfile
            policyVersion
