namespace HsSqlAgent.SqlCore.Rewrite

open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script

/// Static evidence of a statement's possible transactional effect.
/// This is orthogonal to SQL authorization and must NEVER be read as
/// proof of database execution, COMMIT, ROLLBACK, or server storage guarantees.
module internal RewriteTransactionProvenance =

    let describe sourceDialect (syntax: SqlScriptStatementSyntax)
                 (status: SqlStatementAnalysisStatus)
                 (certainty: SqlCatalogKnowledge)
                 (effects: SqlEffectKind)
                 (before: SqlTransactionState)
                 (after: SqlTransactionState)
                 (control: SqlTransactionControlOperation)
                 (diagnostics: IReadOnlyList<SqlDiagnostic>) =
        let active = before = SqlTransactionState.Active
        let supported = status = SqlStatementAnalysisStatus.Analyzed
        let implicitCommit =
            diagnostics |> Seq.exists (fun d ->
                d.Code = "SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN")
        let staticScope =
            if sourceDialect = SqlAgentToolType.Postgres then
                SqlTransactionRollbackScope.StaticCatalogAndSession
            else SqlTransactionRollbackScope.StaticCatalog

        let origin, rollbackScope =
            if implicitCommit then
                SqlTransactionEffectOrigin.PossibleImplicitCommit,
                SqlTransactionRollbackScope.Unproven
            elif before = SqlTransactionState.Unknown then
                SqlTransactionEffectOrigin.UnknownTransactionState,
                SqlTransactionRollbackScope.Unproven
            elif syntax.Kind = SqlScriptStatementKind.Transaction then
                if not supported || after = SqlTransactionState.Unknown then
                    SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
                    SqlTransactionRollbackScope.Unproven
                else
                    let rollbackScope =
                        match control with
                        | SqlTransactionControlOperation.Rollback
                        | SqlTransactionControlOperation.RollbackToSavepoint
                        | SqlTransactionControlOperation.Savepoint ->
                            staticScope
                        | _ -> SqlTransactionRollbackScope.NotApplicable
                    SqlTransactionEffectOrigin.TransactionControl, rollbackScope
            elif not active then
                SqlTransactionEffectOrigin.OutsideTransaction,
                SqlTransactionRollbackScope.NotApplicable
            elif not supported
                 || after = SqlTransactionState.Unknown
                 || status = SqlStatementAnalysisStatus.PartiallyAnalyzed then
                SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
                SqlTransactionRollbackScope.Unproven
            elif syntax.Kind = SqlScriptStatementKind.Schema
                 && certainty = SqlCatalogKnowledge.Known then
                // Only an in-memory Catalog delta/snapshot is proven here.
                SqlTransactionEffectOrigin.CatalogMutation,
                SqlTransactionRollbackScope.StaticCatalog
            elif syntax.Kind = SqlScriptStatementKind.Session
                 && sourceDialect = SqlAgentToolType.Postgres
                 && certainty = SqlCatalogKnowledge.Known then
                // The bounded SET search_path model has atomic snapshot
                // restoration, but only in the static Catalog/Session model.
                SqlTransactionEffectOrigin.SessionMutation,
                SqlTransactionRollbackScope.StaticCatalogAndSession
            else
                // Queries may invoke volatile functions; DML may be affected
                // by triggers, engines, isolation levels or permissions.
                // Never promote parser acceptance to runtime rollback proof.
                SqlTransactionEffectOrigin.UnprovenRuntimeEffects,
                SqlTransactionRollbackScope.Unproven

        SqlTransactionEffectProvenance(
            origin, rollbackScope, control, effects, before, after, syntax.Span)
