namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// Bounded provider-specific transactional Catalog/Sessions snapshot machine.
/// Only PostgreSQL SET search_path is modeled as rollbackable session state.
/// None of the snapshots prove actual server execution or authorization.
module internal RewriteTransactionAnalyzer =

    type SavepointMark = {
        Name: string
        Catalog: SqlCatalogState
        Session: RewriteSessionAnalyzer.SessionContext
    }

    // Most-recent savepoint first. OuterSavepoint only represents SQLite's
    // standalone SAVEPOINT, which itself begins a transaction.
    type TransactionContext =
        | Outside
        | Active of original: SqlCatalogState *
                    originalSession: RewriteSessionAnalyzer.SessionContext *
                    marks: SavepointMark list * outerSavepoint: string option
        | Unknown

    let status = function
        | Outside -> SqlTransactionState.Outside
        | Active _ -> SqlTransactionState.Active
        | Unknown -> SqlTransactionState.Unknown

    type private Command =
        | Begin
        | Commit
        | Rollback
        | Savepoint of string
        | RollbackTo of string
        | Release of string
        | Unsupported

    let private nameOf sourceDialect (token: Token) =
        match token.Kind with
        | Identifier(value, false) when
            RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier value
            && value.Length <= (if sourceDialect = SqlAgentToolType.MsSqlServer then 32 else 63) ->
            let name =
                if sourceDialect = SqlAgentToolType.Postgres then value.ToLowerInvariant()
                elif sourceDialect = SqlAgentToolType.Sqlite
                     || sourceDialect = SqlAgentToolType.MySQL then value.ToUpperInvariant()
                else value
            Some name
        | _ -> None

    let private command sourceDialect (tokens: Token list) =
        let values = tokens |> List.filter (fun t -> t.Kind <> End) |> List.toArray
        let words =
            values |> Array.map (fun token ->
                match token.Kind with
                | Keyword value
                | Identifier(value, false) -> value.ToUpperInvariant()
                | _ -> "<other>")
            |> Array.toList
        let idAt index =
            if index < values.Length then nameOf sourceDialect values[index]
            else None
        let supported = sourceDialect = SqlAgentToolType.Postgres
                        || sourceDialect = SqlAgentToolType.MySQL
                        || sourceDialect = SqlAgentToolType.Sqlite
                        || sourceDialect = SqlAgentToolType.MsSqlServer
                        || sourceDialect = SqlAgentToolType.Oracle
        if not supported then Unsupported
        else
            match sourceDialect, words with
            // Oracle starts DML transactions implicitly. BEGIN TRANSACTION is
            // not Oracle SQL and there is no tracked transactional DDL
            // snapshot: successful DDL commits before and after execution.
            // COMMIT / ROLLBACK therefore cannot undo proven Catalog DDL.
            | SqlAgentToolType.Oracle, (["COMMIT"] | ["COMMIT"; "WORK"]) -> Commit
            | SqlAgentToolType.Oracle, (["ROLLBACK"] | ["ROLLBACK"; "WORK"]) -> Rollback
            | SqlAgentToolType.Oracle, _ -> Unsupported
            | SqlAgentToolType.MsSqlServer, (["BEGIN"; "TRAN"] | ["BEGIN"; "TRANSACTION"]) -> Begin
            | SqlAgentToolType.MsSqlServer, (["COMMIT"; "TRAN"] | ["COMMIT"; "TRANSACTION"] | ["COMMIT"]) -> Commit
            | SqlAgentToolType.MsSqlServer, (["ROLLBACK"; "TRAN"] | ["ROLLBACK"; "TRANSACTION"] | ["ROLLBACK"]) -> Rollback
            | SqlAgentToolType.MsSqlServer, ["SAVE"; ("TRAN" | "TRANSACTION"); _] ->
                match idAt 2 with Some name -> Savepoint name | _ -> Unsupported
            | SqlAgentToolType.MsSqlServer, ["ROLLBACK"; ("TRAN" | "TRANSACTION"); _] ->
                match idAt 2 with Some name -> RollbackTo name | _ -> Unsupported
            | SqlAgentToolType.MsSqlServer, _ -> Unsupported
            | _, (["BEGIN"] | ["BEGIN"; "WORK"] | ["BEGIN"; "TRANSACTION"] | ["START"; "TRANSACTION"]) -> Begin
            | _, (["COMMIT"] | ["COMMIT"; "WORK"] | ["COMMIT"; "TRANSACTION"]) -> Commit
            | _, (["ROLLBACK"] | ["ROLLBACK"; "WORK"] | ["ROLLBACK"; "TRANSACTION"]) -> Rollback
            | _, ["SAVEPOINT"; _] ->
                match idAt 1 with Some name -> Savepoint name | _ -> Unsupported
            | _, (["ROLLBACK"; "TO"; _] | ["ROLLBACK"; "TO"; "SAVEPOINT"; _]) ->
                let index = if values.Length = 3 then 2 else 3
                match idAt index with Some name -> RollbackTo name | _ -> Unsupported
            | _, (["RELEASE"; _] | ["RELEASE"; "SAVEPOINT"; _]) ->
                let index = if values.Length = 2 then 1 else 2
                match idAt index with Some name -> Release name | _ -> Unsupported
            | _ -> Unsupported

    let private controlOperation = function
        | Begin -> SqlTransactionControlOperation.Begin
        | Commit -> SqlTransactionControlOperation.Commit
        | Rollback -> SqlTransactionControlOperation.Rollback
        | Savepoint _ -> SqlTransactionControlOperation.Savepoint
        | RollbackTo _ -> SqlTransactionControlOperation.RollbackToSavepoint
        | Release _ -> SqlTransactionControlOperation.ReleaseSavepoint
        | Unsupported -> SqlTransactionControlOperation.Unknown

    let private identical (left: SqlCatalogState) (right: SqlCatalogState) =
        let sameColumn (a: SqlCatalogColumn) (b: SqlCatalogColumn) =
            String.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && String.Equals(a.TypeName, b.TypeName, StringComparison.Ordinal)
            && a.Nullable = b.Nullable
        let sameConstraint (a: SqlCatalogConstraint) (b: SqlCatalogConstraint) =
            String.Equals(a.Name, b.Name, StringComparison.Ordinal)
            && a.Kind = b.Kind
            && (if isNull a.ReferencedIdentity || isNull b.ReferencedIdentity then
                    isNull a.ReferencedIdentity && isNull b.ReferencedIdentity
                else String.Equals(a.ReferencedIdentity.Key, b.ReferencedIdentity.Key, StringComparison.Ordinal))
            && a.Columns.Count = b.Columns.Count
            && Seq.forall2 (fun x y -> String.Equals(x, y, StringComparison.Ordinal))
                a.Columns b.Columns
            && a.ReferencedColumns.Count = b.ReferencedColumns.Count
            && Seq.forall2 (fun x y -> String.Equals(x, y, StringComparison.Ordinal))
                a.ReferencedColumns b.ReferencedColumns
        let sameTable (a: SqlCatalogTable) (b: SqlCatalogTable) =
            String.Equals(a.QualifiedName.Key, b.QualifiedName.Key, StringComparison.Ordinal)
            && a.Knowledge = b.Knowledge
            && a.Columns.Count = b.Columns.Count
            && Seq.forall2 sameColumn a.Columns b.Columns
            && a.Constraints.Count = b.Constraints.Count
            && Seq.forall2 sameConstraint a.Constraints b.Constraints
            && a.Indexes.Count = b.Indexes.Count
            && Seq.forall2 (fun (x: SqlCatalogIndex) (y: SqlCatalogIndex) ->
                String.Equals(x.Name, y.Name, StringComparison.Ordinal)
                && x.Columns.Count = y.Columns.Count
                && Seq.forall2 (fun a b ->
                    String.Equals(a, b, StringComparison.Ordinal)) x.Columns y.Columns)
                a.Indexes b.Indexes
        left.IsClosedWorld = right.IsClosedWorld
        && left.Tables.Count = right.Tables.Count
        && Seq.forall2 sameTable left.Tables right.Tables

    let private error (syntax: SqlScriptStatementSyntax) code message =
        SqlDiagnostic(
            code,
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Capability,
            message,
            syntax.Span)

    let private delta (syntax: SqlScriptStatementSyntax) certainty =
        SqlCatalogChange("*", SqlCatalogOperation.Indeterminate, certainty, syntax.Span)

    /// Static transactional-Catalog subset only: no DML execution, storage
    /// engine or server runtime is attested. This is deliberately stricter
    /// than native SQL for uncertain implicit commits/distributed operations.
    let analyze sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                (current: SqlCatalogState) (currentSession: RewriteSessionAnalyzer.SessionContext)
                (context: TransactionContext) (syntax: SqlScriptStatementSyntax) =
        let parsed =
            RewriteResourceBudget.withStage "TransactionLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
        let operation = command sourceDialect parsed
        let uncertain code message =
            let state = RewriteCatalogModel.taintAll current
            SqlStatementAnalysisStatus.Unsupported,
            SqlCatalogKnowledge.Unknown,
            state,
            RewriteSessionAnalyzer.unknown,
            Unknown,
            [delta syntax SqlCatalogKnowledge.Unknown],
            [error syntax code message]
        let success next nextSession newContext changes =
            SqlStatementAnalysisStatus.Analyzed,
            SqlCatalogKnowledge.Known, next, nextSession, newContext, changes, []
        let rollbackEffects target =
            if identical current target then [] else [delta syntax SqlCatalogKnowledge.Known]
        let maxSavepoints = 32
        let result =
            match operation, context with
            | (Commit | Rollback), Outside when sourceDialect = SqlAgentToolType.Oracle ->
                // No Oracle explicit BEGIN or DDL rollback snapshot is modeled.
                // The current virtual Catalog describes committed DDL, not
                // rows changed by triggers or DML; leave schema facts intact.
                success current currentSession Outside []
            | Begin, Outside ->
                success current currentSession (Active(current, currentSession, [], None)) []
            | Commit, Active _ ->
                // PostgreSQL SET without LOCAL survives COMMIT; all other session
                // operations remain independent of the Catalog rollback model.
                success current currentSession Outside []
            | Rollback, Active(original, originalSession, _, _) ->
                // PostgreSQL SET (SESSION) is transaction-scoped until COMMIT.
                // MySQL USE and SQL Server NOCOUNT are NOT proven rollbackable.
                let restoredSession =
                    if sourceDialect = SqlAgentToolType.Postgres then originalSession
                    else currentSession
                success original restoredSession Outside (rollbackEffects original)
            | Savepoint name, Outside when sourceDialect = SqlAgentToolType.Sqlite ->
                success current currentSession
                    (Active(current, currentSession,
                            [{Name=name; Catalog=current; Session=currentSession}], Some name)) []
            | Savepoint name, Active(original, originalSession, marks, outer) ->
                if List.length marks >= maxSavepoints then
                    uncertain "SQL_TRANSACTION_SAVEPOINT_BUDGET"
                        "Savepoint nesting exceeds the static analysis budget."
                else
                    let mark = {Name=name; Catalog=current; Session=currentSession}
                    success current currentSession
                        (Active(original, originalSession, mark::marks, outer)) []
            | RollbackTo name, Active(original, originalSession, marks, outer) ->
                // Most recent matching savepoint wins. The mark itself remains
                // active, while newer marks are discarded. The PostgreSQL
                // session snapshot is restored atomically with the Catalog.
                match marks |> List.tryFindIndex (fun mark -> mark.Name = name) with
                | Some index ->
                    let remaining = marks |> List.skip index
                    let target = remaining.Head
                    let restoredSession =
                        if sourceDialect = SqlAgentToolType.Postgres then target.Session
                        else currentSession
                    success target.Catalog restoredSession
                        (Active(original, originalSession, remaining, outer))
                        (rollbackEffects target.Catalog)
                | None ->
                    uncertain "SQL_TRANSACTION_SAVEPOINT_UNKNOWN"
                        "ROLLBACK target is not a modeled active savepoint."
            | Release name, Active(original, originalSession, marks, outer) ->
                if sourceDialect = SqlAgentToolType.MsSqlServer then
                    uncertain "SQL_TRANSACTION_RELEASE_UNSUPPORTED"
                        "SQL Server SAVE TRAN has no RELEASE; only COMMIT or rollback is modeled."
                else
                    match marks |> List.tryFindIndex (fun mark -> mark.Name = name) with
                    | Some index ->
                        let remaining = marks |> List.skip (index + 1)
                        if outer.IsSome && remaining.IsEmpty then
                            // SQLite outermost RELEASE commits the transaction.
                            success current currentSession Outside []
                        else success current currentSession
                                (Active(original, originalSession, remaining, outer)) []
                    | None ->
                        uncertain "SQL_TRANSACTION_SAVEPOINT_UNKNOWN"
                            "RELEASE target is not a modeled active savepoint."
            | Unsupported, _ ->
                uncertain "SQL_TRANSACTION_UNSUPPORTED"
                    "Transaction grammar or source-dialect semantics are outside the proven subset."
            | _, Unknown ->
                uncertain "SQL_TRANSACTION_STATE_UNKNOWN"
                    "Transaction state is unknown; this command cannot re-establish schema certainty."
            | Begin, Active _ ->
                uncertain "SQL_TRANSACTION_NESTED_UNPROVEN"
                    "A nested BEGIN has provider-specific implicit COMMIT or transaction-count behavior."
            | (Commit | Rollback), Outside ->
                uncertain "SQL_TRANSACTION_WITHOUT_BEGIN"
                    "COMMIT or ROLLBACK has no proven preceding transaction."
            | (RollbackTo _ | Release _), Outside ->
                uncertain "SQL_TRANSACTION_SAVEPOINT_WITHOUT_BEGIN"
                    "Savepoint control has no proven active transaction."
            | Savepoint _, Outside ->
                uncertain "SQL_TRANSACTION_SAVEPOINT_WITHOUT_BEGIN"
                    "SAVEPOINT requires an active transaction for this provider."

        let status, certainty, catalog, session, nextContext, changes, diagnostics = result
        status, certainty, catalog, session, nextContext, changes, diagnostics,
        controlOperation operation
    /// SQL Server's DML may promote an explicit transaction; MySQL's storage
    /// engine might be nontransactional. A modeled virtual-Catalog ROLLBACK
    /// cannot prove the server data effects of either.
    let invalidateIfUnproven sourceDialect context (kind: SqlScriptStatementKind)
                (status: SqlStatementAnalysisStatus) outsideBoundaryUnproven =
        match context with
        // Outside means no tracked BEGIN in this static script, not a live
        // connection-state attestation. Opaque SQL can change the effective
        // transaction boundary even when no explicit BEGIN was analyzed.
        | Outside when outsideBoundaryUnproven -> Unknown
        | Active(_, _, marks, _) when status = SqlStatementAnalysisStatus.Invalid
                                  || status = SqlStatementAnalysisStatus.Unsupported
                                  || kind = SqlScriptStatementKind.Utility
                                  || kind = SqlScriptStatementKind.Procedural
                                  || kind = SqlScriptStatementKind.Unknown
                                  || kind = SqlScriptStatementKind.Security
                                  || (kind = SqlScriptStatementKind.Session
                                      && sourceDialect <> SqlAgentToolType.Postgres)
                                  || (kind = SqlScriptStatementKind.Mutation
                                      && (sourceDialect = SqlAgentToolType.MySQL
                                          || sourceDialect = SqlAgentToolType.MsSqlServer)) ->
            Unknown
        | other -> other
