namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Execution
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

module private FunctionalDmlConflictSqlRewriter =

    let private cloneCommand (command: CompiledSqlCommand) sql fingerprint =
        CompiledSqlCommand(
            sql,
            command.Parameters,
            command.Kind,
            fingerprint,
            command.TargetProvider,
            ReturnsRows = command.ReturnsRows)

    let private recomputeFingerprint (command: CompiledSqlCommand) (policyVersion: string) =
        cloneCommand
            command
            command.Sql
            (DmlFingerprintService.ComputePlanFingerprint(command, policyVersion))

    let private requireSinglePart (identifier: SqlIdentifier) =
        if identifier.Parts.Length <> 1
           || (identifier.Parts[0].Value = "*" && not identifier.Parts[0].WasQuoted) then
            raise (SqlCompilationException(
                "Portable INSERT conflict columns must be unqualified non-wildcard identifiers."))
        identifier.Parts[0].Value

    let private identifierSet (identifiers: seq<SqlIdentifier>) =
        let result = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for identifier in identifiers do
            result.Add(requireSinglePart identifier) |> ignore
        result

    let private stringSet (values: seq<string>) =
        HashSet<string>(values, StringComparer.OrdinalIgnoreCase)

    let private validateTargetContract provider targetProfile =
        match SqlDmlUpsertCapabilityRules.DirectTargetValidationError(provider, targetProfile) with
        | null -> ()
        | error -> raise (SqlCompilationException(error))

    let private validateInsertSelectUpdateAssurance
        (conflict: InsertConflictClause)
        (assurance: DmlConflictTargetAssurance | null) =

        if isNull assurance || assurance.SourceRowsUniqueByInsertColumns.IsDefaultOrEmpty then
            raise (SqlCompilationException(
                "PostgreSQL INSERT ... SELECT ON CONFLICT DO UPDATE remains fail-closed without explicit source-row uniqueness/cardinality assurance for the complete conflict target."))

        let target = identifierSet conflict.TargetColumns
        let proven = stringSet assurance.SourceRowsUniqueByInsertColumns
        if target.Count <> conflict.TargetColumns.Length
           || proven.Count <> assurance.SourceRowsUniqueByInsertColumns.Length
           || not (target.SetEquals(proven)) then
            raise (SqlCompilationException(
                "INSERT ... SELECT conflict DO UPDATE requires source-row uniqueness assurance to match the complete explicit conflict target exactly."))

    let private validatePortableShape
        (insert: InsertStatement)
        (targetProvider: SqlAgentToolType)
        (assurance: DmlConflictTargetAssurance | null) =

        let conflict =
            match insert.Conflict with
            | null -> raise (SqlCompilationException("INSERT conflict contract is missing."))
            | value -> value

        let source =
            match insert.Source with
            | :? InsertValuesSource as values -> Choice1Of2 values
            | :? InsertQuerySource as querySource -> Choice2Of2 querySource
            | _ -> raise (SqlCompilationException("Unsupported INSERT source for conflict handling."))

        match source with
        | Choice2Of2 _ when targetProvider <> SqlAgentToolType.Postgres ->
            raise (SqlCompilationException(
                "INSERT ... SELECT conflict handling is currently proven only for PostgreSQL targets; other targets remain fail-closed."))
        | _ -> ()

        if conflict.TargetColumns.IsDefaultOrEmpty then
            raise (SqlCompilationException(
                "INSERT conflict handling requires at least one explicit target column."))

        let insertColumns = identifierSet insert.Columns
        let conflictColumns = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for target in conflict.TargetColumns do
            let name = requireSinglePart target
            if not (conflictColumns.Add(name)) then
                raise (SqlCompilationException(
                    $"INSERT conflict target column '{name}' is declared more than once."))
            if not (insertColumns.Contains(name)) then
                raise (SqlCompilationException(
                    $"INSERT conflict target column '{name}' must be explicitly present in the INSERT column list so Core does not depend on provider-default conflict-key values."))

        if conflict.Action = InsertConflictActionKind.DoNothing then
            if not conflict.Assignments.IsDefaultOrEmpty then
                raise (SqlCompilationException(
                    "INSERT conflict DO NOTHING cannot carry update assignments."))
        else
            if conflict.Action <> InsertConflictActionKind.UpdateProposedValues then
                raise (SqlCompilationException($"Unsupported INSERT conflict action {conflict.Action}."))
            if conflict.Assignments.IsDefaultOrEmpty then
                raise (SqlCompilationException(
                    "INSERT conflict DO UPDATE requires at least one assignment."))

            match source with
            | Choice2Of2 _ -> validateInsertSelectUpdateAssurance conflict assurance
            | Choice1Of2 values when values.Rows.Length <> 1 ->
                raise (SqlCompilationException(
                    "Portable INSERT conflict DO UPDATE currently requires exactly one proposed VALUES row. Multi-row proposed values require explicit source-row uniqueness/cardinality assurance."))
            | _ -> ()

            let assigned = HashSet<string>(StringComparer.OrdinalIgnoreCase)
            for assignment in conflict.Assignments do
                let target = requireSinglePart assignment.Column
                let proposed = requireSinglePart assignment.ProposedColumn
                if not (assigned.Add(target)) then
                    raise (SqlCompilationException(
                        $"INSERT conflict DO UPDATE assigns column '{target}' more than once."))
                if not (insertColumns.Contains(proposed)) then
                    raise (SqlCompilationException(
                        $"Proposed-row column '{proposed}' must be explicitly present in the INSERT column list; portable upsert does not depend on target-provider default values."))

    let private validateMySqlUniqueKeyTarget
        (conflict: InsertConflictClause)
        (assurance: DmlConflictTargetAssurance | null) =

        if isNull assurance || assurance.MatchedUniqueKeyColumns.IsDefaultOrEmpty then
            raise (SqlCompilationException(
                "MySQL ON DUPLICATE KEY UPDATE requires metadata-backed statement assurance proving the explicit conflict target matches a complete enforced unique key and is the sole enforced native conflict source."))
        if not assurance.IsSoleEnforcedUniqueKey then
            raise (SqlCompilationException(
                "MySQL ON DUPLICATE KEY UPDATE can react to any UNIQUE or PRIMARY KEY conflict. Core requires the matched conflict target to be the sole enforced native unique-conflict source, including no additional richer expression, prefix, partial, or otherwise unsupported enforced unique keys."))

        let target = identifierSet conflict.TargetColumns
        let matchedKey = stringSet assurance.MatchedUniqueKeyColumns
        if target.Count <> conflict.TargetColumns.Length
           || matchedKey.Count <> assurance.MatchedUniqueKeyColumns.Length
           || not (target.SetEquals(matchedKey)) then
            raise (SqlCompilationException(
                "MySQL conflict lowering requires the canonical explicit conflict target to match the complete metadata-resolved unique key exactly."))

    let private createMySqlProposedRowAlias (insert: InsertStatement) =
        let preferred = "__core_proposed"
        let tableName = insert.Target.Name.Parts[insert.Target.Name.Parts.Length - 1].Value
        if String.Equals(tableName, preferred, StringComparison.OrdinalIgnoreCase) then
            preferred + "_row"
        else
            preferred

    let private validateFirebirdPrimaryKeyTarget
        (conflict: InsertConflictClause)
        (assurance: DmlConflictTargetAssurance | null) =

        if isNull assurance || assurance.PrimaryKeyColumns.IsDefaultOrEmpty then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT requires metadata-backed conflict-target assurance proving MATCHING equals the resolved primary key; absent assurance remains fail-closed because non-unique MATCHING can update multiple rows."))

        let target = identifierSet conflict.TargetColumns
        let primaryKey = stringSet assurance.PrimaryKeyColumns
        if target.Count <> conflict.TargetColumns.Length
           || primaryKey.Count <> assurance.PrimaryKeyColumns.Length
           || not (target.SetEquals(primaryKey)) then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT requires the canonical conflict target to match the complete resolved primary key exactly; general UNIQUE-key and non-unique MATCHING metadata are not represented yet."))

    let private validateFirebirdFullProposedRowUpdate
        (insert: InsertStatement)
        (conflict: InsertConflictClause) =

        if conflict.Assignments.Length <> insert.Columns.Length then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT updates every supplied INSERT column on a match. Core therefore requires one same-column proposed-row assignment for every INSERT column so partial-update semantics cannot drift."))

        let insertColumns = identifierSet insert.Columns
        let assigned = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for assignment in conflict.Assignments do
            let target = requireSinglePart assignment.Column
            let proposed = requireSinglePart assignment.ProposedColumn
            if not (String.Equals(target, proposed, StringComparison.OrdinalIgnoreCase)) then
                raise (SqlCompilationException(
                    "Firebird UPDATE OR INSERT can mirror the portable conflict contract only when each assignment is target = proposed-row target for the same column."))
            if not (assigned.Add(target)) || not (insertColumns.Contains(target)) then
                raise (SqlCompilationException(
                    $"Firebird UPDATE OR INSERT assignment column '{target}' must occur exactly once in the INSERT column list."))

        if not (assigned.SetEquals(insertColumns)) then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT requires conflict assignments to cover the complete INSERT column set."))

    let private renderIdentifier identifier provider =
        CoreIdentifierSqlRenderer.Render(identifier, provider, allowWildcard = false)

    let private renderOnConflictSuffix (conflict: InsertConflictClause) provider =
        let targets =
            conflict.TargetColumns
            |> Seq.map (fun column -> renderIdentifier column provider)
            |> String.concat ", "

        if conflict.Action = InsertConflictActionKind.DoNothing then
            $" ON CONFLICT ({targets}) DO NOTHING"
        else
            let assignments =
                conflict.Assignments
                |> Seq.map (fun assignment ->
                    renderIdentifier assignment.Column provider
                    + " = EXCLUDED."
                    + renderIdentifier assignment.ProposedColumn provider)
                |> String.concat ", "
            $" ON CONFLICT ({targets}) DO UPDATE SET {assignments}"

    let private rewriteFirebird
        (command: CompiledSqlCommand)
        (insert: InsertStatement)
        (assurance: DmlConflictTargetAssurance | null)
        (policyVersion: string) =

        let conflict =
            match insert.Conflict with
            | null -> raise (SqlCompilationException("INSERT conflict contract is missing."))
            | value -> value

        if conflict.Action <> InsertConflictActionKind.UpdateProposedValues then
            raise (SqlCompilationException(
                "Firebird UPDATE OR INSERT has update-or-insert semantics and cannot represent portable ON CONFLICT DO NOTHING; a separate MERGE no-match contract is required."))

        validateFirebirdPrimaryKeyTarget conflict assurance
        validateFirebirdFullProposedRowUpdate insert conflict

        let sql = command.Sql.TrimEnd().TrimEnd(';')
        if not (sql.StartsWith("INSERT INTO ", StringComparison.OrdinalIgnoreCase)) then
            raise (SqlCompilationException(
                "Firebird conflict lowering expected the Core INSERT backend to emit an INSERT INTO statement."))

        let matching =
            conflict.TargetColumns
            |> Seq.map (fun column -> renderIdentifier column SqlAgentToolType.Firebird)
            |> String.concat ", "

        cloneCommand command ("UPDATE OR " + sql + $" MATCHING ({matching})") String.Empty
        |> fun rewritten -> recomputeFingerprint rewritten policyVersion

    let private rewriteMySql
        (command: CompiledSqlCommand)
        (insert: InsertStatement)
        (targetProfile: SqlProviderCapabilityProfile | null)
        (assurance: DmlConflictTargetAssurance | null)
        (policyVersion: string) =

        let conflict =
            match insert.Conflict with
            | null -> raise (SqlCompilationException("INSERT conflict contract is missing."))
            | value -> value

        if conflict.Action <> InsertConflictActionKind.UpdateProposedValues then
            raise (SqlCompilationException(
                "MySQL INSERT IGNORE is not a portable ON CONFLICT DO NOTHING equivalent because it can suppress errors beyond the explicit conflict target; MySQL DO NOTHING therefore remains fail-closed."))

        validateMySqlUniqueKeyTarget conflict assurance

        match SqlDmlUpsertCapabilityRules.MySqlConditionalTargetValidationError(targetProfile) with
        | null -> ()
        | error -> raise (SqlCompilationException(error))

        let aliasName = createMySqlProposedRowAlias insert
        let alias =
            CoreIdentifierSqlRenderer.Render(
                SqlIdentifier.Unquoted(aliasName, SourceSpan.Unknown),
                SqlAgentToolType.MySQL,
                allowWildcard = false)

        let assignments =
            conflict.Assignments
            |> Seq.map (fun assignment ->
                renderIdentifier assignment.Column SqlAgentToolType.MySQL
                + " = " + alias + "."
                + renderIdentifier assignment.ProposedColumn SqlAgentToolType.MySQL)
            |> String.concat ", "

        let sql = command.Sql.TrimEnd().TrimEnd(';')
        cloneCommand command (sql + $" AS {alias} ON DUPLICATE KEY UPDATE {assignments}") String.Empty
        |> fun rewritten -> recomputeFingerprint rewritten policyVersion

    let apply
        (command: CompiledSqlCommand)
        (insert: InsertStatement)
        (targetProfile: SqlProviderCapabilityProfile | null)
        (conflictTargetAssurance: DmlConflictTargetAssurance | null)
        (policyVersion: string) =

        ArgumentNullException.ThrowIfNull(command)
        ArgumentNullException.ThrowIfNull(insert)
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion)

        match insert.Conflict with
        | null -> command
        | conflict ->
            validatePortableShape insert command.TargetProvider conflictTargetAssurance
            match command.TargetProvider with
            | SqlAgentToolType.Firebird ->
                rewriteFirebird command insert conflictTargetAssurance policyVersion
            | SqlAgentToolType.MySQL ->
                rewriteMySql command insert targetProfile conflictTargetAssurance policyVersion
            | provider ->
                validateTargetContract provider targetProfile
                let suffix = renderOnConflictSuffix conflict provider
                cloneCommand
                    command
                    (command.Sql.TrimEnd().TrimEnd(';') + suffix)
                    String.Empty
                |> fun rewritten -> recomputeFingerprint rewritten policyVersion

[<AbstractClass; Sealed>]
type internal CoreDmlConflictSqlRewriter private () =
    static member Apply(
        command: CompiledSqlCommand,
        insert: InsertStatement,
        targetProfile: SqlProviderCapabilityProfile | null,
        conflictTargetAssurance: DmlConflictTargetAssurance | null,
        policyVersion: string) =
        FunctionalDmlConflictSqlRewriter.apply
            command
            insert
            targetProfile
            conflictTargetAssurance
            policyVersion
