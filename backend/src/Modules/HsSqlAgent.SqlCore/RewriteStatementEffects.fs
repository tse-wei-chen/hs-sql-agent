namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Non-executable, conservative static effect descriptions. A parsed AST is
/// never an authorization proof, and an unmodeled effect is never suppressed.
module internal RewriteStatementEffects =

    let private readOnly (values: seq<'T>) : IReadOnlyList<'T> =
        (values |> Seq.toArray |> Array.AsReadOnly) :> IReadOnlyList<'T>

    let private noDependencies : IReadOnlyList<string> =
        (Array.empty<string> |> Array.AsReadOnly) :> IReadOnlyList<string>

    let private effect kind operation (objectName: string | null)
                       (identity: SqlCatalogQualifiedName | null) certainty span dependencies =
        SqlStatementEffect(kind, operation, objectName, identity, certainty, span, dependencies)

    let private catalogOperation = function
        | SqlCatalogOperation.CreateTable -> SqlStatementEffectOperation.CreateTable
        | SqlCatalogOperation.DropTable -> SqlStatementEffectOperation.DropTable
        | SqlCatalogOperation.AddColumn -> SqlStatementEffectOperation.AddColumn
        | SqlCatalogOperation.DropColumn -> SqlStatementEffectOperation.DropColumn
        | SqlCatalogOperation.RenameColumn -> SqlStatementEffectOperation.RenameColumn
        | SqlCatalogOperation.CreateIndex -> SqlStatementEffectOperation.CreateIndex
        | SqlCatalogOperation.DropIndex -> SqlStatementEffectOperation.DropIndex
        | SqlCatalogOperation.AddConstraint -> SqlStatementEffectOperation.AddConstraint
        | SqlCatalogOperation.DropConstraint -> SqlStatementEffectOperation.DropConstraint
        | SqlCatalogOperation.CreateView -> SqlStatementEffectOperation.CreateView
        | SqlCatalogOperation.DropView -> SqlStatementEffectOperation.DropView
        | SqlCatalogOperation.ReplaceView -> SqlStatementEffectOperation.ReplaceView
        | SqlCatalogOperation.Indeterminate -> SqlStatementEffectOperation.Unknown
        | _ -> SqlStatementEffectOperation.Unknown

    let private catalogEffect (change: SqlCatalogChange) =
        let unknown = change.Operation = SqlCatalogOperation.Indeterminate
        let certainty =
            if unknown then SqlCatalogKnowledge.Unknown else change.Certainty
        let objectName : string | null =
            if unknown || change.TableName = "*" then null else change.TableName
        let identity =
            if certainty <> SqlCatalogKnowledge.Known then null else change.TableIdentity
        // An indeterminate Catalog delta already represents the opaque
        // schema effect. Preserve its single structured entry while marking
        // that other potential effects remain unknown.
        let kind =
            if unknown && change.Certainty <> SqlCatalogKnowledge.Known then
                SqlEffectKind.ChangeSchema ||| SqlEffectKind.UnknownEffects
            else SqlEffectKind.ChangeSchema
        SqlStatementEffect(kind, catalogOperation change.Operation,
            objectName, identity, certainty, change.Span,
            noDependencies, change.ConstraintName)

    let private physicalReferences document =
        let facts = RewriteInspection.inspectDocument document
        facts.ReferencedTables
        |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(a, b))
        |> Seq.toList

    let private astEffects sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                           (syntax: SqlScriptStatementSyntax) =
        // The Script parser already required the very same source parser to
        // accept this statement. Reparse only to inspect the closed F# AST.
        let parsed = RewriteFacadeAdapter.parseSourceValidated
                         syntax.Text sourceDialect sourceProfile
        let document = Typestate.Parsed.value parsed
        let references = physicalReferences document
        let potential kind operation name dependencies =
            effect kind operation name null SqlCatalogKnowledge.Unknown
                syntax.Span (readOnly dependencies)
        let reads values =
            values |> List.map (fun name ->
                potential SqlEffectKind.ReadData SqlStatementEffectOperation.ReadTable name [])
        match document.Statement with
        | Statement.QueryStatement _ ->
            // SELECT without a physical FROM still has potentially opaque
            // function behavior; do not advertise a proven empty effect set.
            match reads references with
            | [] -> [potential SqlEffectKind.ReadData SqlStatementEffectOperation.Unknown null []]
            | values -> values
        | Statement.InsertStatement insert ->
            let target = Identifier.text insert.Target
            let sources =
                references |> List.filter (fun name ->
                    not (String.Equals(name, target, StringComparison.OrdinalIgnoreCase)))
            potential SqlEffectKind.WriteData SqlStatementEffectOperation.Insert
                target sources :: reads sources
        | Statement.UpdateStatement update ->
            let target = Identifier.text update.Target
            let sources =
                references |> List.filter (fun name ->
                    not (String.Equals(name, target, StringComparison.OrdinalIgnoreCase)))
            potential SqlEffectKind.WriteData SqlStatementEffectOperation.Update
                target sources :: reads sources
        | Statement.DeleteStatement delete ->
            let target = Identifier.text delete.Target
            let sources =
                references |> List.filter (fun name ->
                    not (String.Equals(name, target, StringComparison.OrdinalIgnoreCase)))
            potential SqlEffectKind.WriteData SqlStatementEffectOperation.Delete
                target sources :: reads sources
        | Statement.MergeStatement merge ->
            let target = Identifier.text merge.Target
            let sources =
                references |> List.filter (fun name ->
                    not (String.Equals(name, target, StringComparison.OrdinalIgnoreCase)))
            potential SqlEffectKind.WriteData SqlStatementEffectOperation.Merge
                target sources :: reads sources

    let describe sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                 (syntax: SqlScriptStatementSyntax)
                 (status: SqlStatementAnalysisStatus) (certainty: SqlCatalogKnowledge)
                 (legacyEffects: SqlEffectKind)
                 (changes: IReadOnlyList<SqlCatalogChange>)
                 (securityIntents: IReadOnlyList<SqlSecurityIntent>)
                 (securityDependencies: IReadOnlyList<SqlSecurityDependency>)
                 (utilityIntents: IReadOnlyList<SqlUtilityIntent>)
                 (proceduralIntents: IReadOnlyList<SqlProceduralIntent>)
                 (proceduralDefinitions: IReadOnlyList<SqlProceduralDefinition>)
                 (sessionOperation: SqlStatementEffectOperation)
                 (provenance: SqlTransactionEffectProvenance) =
        let coverage =
            match syntax.Kind, status, certainty with
            | SqlScriptStatementKind.Session, SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known ->
                SqlEffectCoverage.Complete
            | SqlScriptStatementKind.Session, SqlStatementAnalysisStatus.PartiallyAnalyzed, _
                when sessionOperation <> SqlStatementEffectOperation.Unknown ->
                SqlEffectCoverage.Partial
            | SqlScriptStatementKind.Security, SqlStatementAnalysisStatus.PartiallyAnalyzed, _
                when securityIntents.Count > 0 || securityDependencies.Count > 0 -> SqlEffectCoverage.Partial
            | SqlScriptStatementKind.Utility, SqlStatementAnalysisStatus.PartiallyAnalyzed, _
                when utilityIntents.Count > 0 -> SqlEffectCoverage.Partial
            | SqlScriptStatementKind.Procedural, SqlStatementAnalysisStatus.PartiallyAnalyzed, _
                when proceduralIntents.Count > 0 || proceduralDefinitions.Count > 0 ->
                SqlEffectCoverage.Partial
            | SqlScriptStatementKind.Schema, SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known ->
                SqlEffectCoverage.Complete
            | (SqlScriptStatementKind.Query | SqlScriptStatementKind.Mutation),
              SqlStatementAnalysisStatus.PartiallyAnalyzed, _ when syntax.Status = SqlScriptParseStatus.Parsed ->
                SqlEffectCoverage.Partial
            | _ -> SqlEffectCoverage.Unknown

        let catalog = changes |> Seq.map catalogEffect |> Seq.toList
        // DDL can implicitly COMMIT even when no virtual Catalog delta exists
        // (for example, MySQL CREATE TABLE IF NOT EXISTS on a known table).
        // Reuse the single provider-aware provenance classification rather than
        // guessing from SQL text or claiming that a server COMMIT occurred.
        let schema =
            if provenance.Origin = SqlTransactionEffectOrigin.PossibleImplicitCommit then
                catalog @ [effect SqlEffectKind.ChangeTransaction
                    SqlStatementEffectOperation.PossibleImplicitCommit
                    null null SqlCatalogKnowledge.Unknown syntax.Span noDependencies]
            else catalog
        let all =
            if syntax.Status = SqlScriptParseStatus.Invalid
               || status = SqlStatementAnalysisStatus.Invalid then
                // Invalid SQL does not prove execution; preserve uncertainty via coverage.
                []
            else
                match syntax.Kind with
                | SqlScriptStatementKind.Query
                | SqlScriptStatementKind.Mutation when syntax.Status = SqlScriptParseStatus.Parsed ->
                    astEffects sourceDialect sourceProfile syntax
                | SqlScriptStatementKind.Schema -> schema
                | SqlScriptStatementKind.Security when securityIntents.Count > 0 ->
                    securityIntents
                    |> Seq.map (fun intent ->
                        let operation =
                            match intent.Action with
                            | SqlSecurityAction.Grant -> SqlStatementEffectOperation.GrantPrivilege
                            | SqlSecurityAction.Revoke -> SqlStatementEffectOperation.RevokePrivilege
                            | SqlSecurityAction.Deny -> SqlStatementEffectOperation.DenyPrivilege
                            | _ -> SqlStatementEffectOperation.Unknown
                        effect SqlEffectKind.ChangePermissions operation
                            intent.Target.DisplayName null SqlCatalogKnowledge.Unknown
                            intent.Span noDependencies)
                    |> Seq.toList
                | SqlScriptStatementKind.Security when securityDependencies.Count > 0 ->
                    securityDependencies
                    |> Seq.map (fun dependency ->
                        let operation =
                            match dependency.Action with
                            | SqlSecurityAction.Grant -> SqlStatementEffectOperation.GrantRoleMembership
                            | SqlSecurityAction.Revoke -> SqlStatementEffectOperation.RevokeRoleMembership
                            | _ -> SqlStatementEffectOperation.Unknown
                        effect SqlEffectKind.ChangePermissions operation
                            dependency.Role null SqlCatalogKnowledge.Unknown
                            dependency.Span noDependencies)
                    |> Seq.toList
                | SqlScriptStatementKind.Utility when utilityIntents.Count > 0 ->
                    utilityIntents
                    |> Seq.map (fun intent ->
                        let operation =
                            match intent.Operation with
                            | SqlUtilityOperation.ExplainPlan -> SqlStatementEffectOperation.ExplainPlan
                            | SqlUtilityOperation.ExplainExecute -> SqlStatementEffectOperation.ExplainExecute
                            | SqlUtilityOperation.AnalyzeStatistics -> SqlStatementEffectOperation.AnalyzeStatistics
                            | SqlUtilityOperation.VacuumMaintenance -> SqlStatementEffectOperation.VacuumMaintenance
                            | _ -> SqlStatementEffectOperation.Unknown
                        effect intent.PotentialEffects operation intent.TargetName null
                            SqlCatalogKnowledge.Unknown intent.Span noDependencies)
                    |> Seq.toList
                | SqlScriptStatementKind.Procedural when proceduralDefinitions.Count > 0 ->
                    proceduralDefinitions
                    |> Seq.map (fun definition ->
                        let operation =
                            if definition.Kind = SqlProceduralDefinitionKind.Function then
                                SqlStatementEffectOperation.DefineFunction
                            else SqlStatementEffectOperation.DefineProcedure
                        effect (SqlEffectKind.ChangeSchema ||| SqlEffectKind.UnknownEffects)
                            operation
                            definition.Routine.DisplayName null SqlCatalogKnowledge.Unknown
                            definition.Span noDependencies)
                    |> Seq.toList
                | SqlScriptStatementKind.Procedural when proceduralIntents.Count > 0 ->
                    proceduralIntents
                    |> Seq.map (fun intent ->
                        effect intent.PotentialEffects
                            SqlStatementEffectOperation.InvokeProcedure
                            intent.Routine.DisplayName null SqlCatalogKnowledge.Unknown
                            intent.Span noDependencies)
                    |> Seq.toList
                | SqlScriptStatementKind.Session when
                    sessionOperation <> SqlStatementEffectOperation.Unknown ->
                    [effect legacyEffects sessionOperation
                        null null (if status = SqlStatementAnalysisStatus.Analyzed then
                                       SqlCatalogKnowledge.Known else SqlCatalogKnowledge.Unknown)
                        syntax.Span noDependencies]
                | SqlScriptStatementKind.Transaction ->
                    // Typed transaction effects inherit the Script-level
                    // certainty fence. Recognized Oracle COMMIT / ROLLBACK
                    // after an opaque statement is not proven to have
                    // modified the live connection or restored its Catalog.
                    let control =
                        effect SqlEffectKind.ChangeTransaction
                            SqlStatementEffectOperation.TransactionControl
                            null null certainty syntax.Span noDependencies
                    control :: catalog
                | _ ->
                    // An unmodeled statement might have effects beyond its family.
                    [effect (legacyEffects ||| SqlEffectKind.UnknownEffects)
                        SqlStatementEffectOperation.Unknown null null
                        SqlCatalogKnowledge.Unknown syntax.Span noDependencies]
        // Known Catalog operations and explicit TCL controls must not hide
        // possible unmodeled side effects when their enclosing statement is
        // Unsupported. Keep the original typed entries and append one opaque
        // unknown entry only if the projection did not already carry one.
        // Invalid SQL deliberately has no detailed objects/effects: the
        // Unknown Effects flag and Unknown coverage carry its uncertainty.
        let all =
            if status = SqlStatementAnalysisStatus.Unsupported
               && not (all |> List.exists (fun item ->
                   (item.Kind &&& SqlEffectKind.UnknownEffects) <> SqlEffectKind.None)) then
                all @ [effect SqlEffectKind.UnknownEffects
                    SqlStatementEffectOperation.Unknown
                    null null SqlCatalogKnowledge.Unknown syntax.Span noDependencies]
            else all
        readOnly all, coverage
