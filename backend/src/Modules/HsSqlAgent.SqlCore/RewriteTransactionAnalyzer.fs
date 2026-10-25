namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer

/// PostgreSQL's simple transactional-DDL subset. All other transaction
/// syntaxes/dialects stay unknown until their server semantics are proven.
/// No database connection or executable proof is produced.
module internal RewriteTransactionAnalyzer =

    type TransactionContext =
        | Outside
        | Active of original: SqlCatalogState
        | Unknown

    let status = function
        | Outside -> SqlTransactionState.Outside
        | Active _ -> SqlTransactionState.Active
        | Unknown -> SqlTransactionState.Unknown

    type private Command =
        | Begin
        | Commit
        | Rollback
        | Unsupported

    let private command (tokens: Token list) =
        let parts =
            tokens
            |> List.choose (fun token ->
                match token.Kind with
                | Keyword value
                | Identifier(value, false) -> Some(value.ToUpperInvariant())
                | End -> None
                | _ -> Some("<other>"))
        match parts with
        | ["BEGIN"]
        | ["BEGIN"; "WORK"]
        | ["BEGIN"; "TRANSACTION"]
        | ["START"; "TRANSACTION"] -> Begin
        | ["COMMIT"]
        | ["COMMIT"; "WORK"]
        | ["COMMIT"; "TRANSACTION"] -> Commit
        | ["ROLLBACK"]
        | ["ROLLBACK"; "WORK"]
        | ["ROLLBACK"; "TRANSACTION"] -> Rollback
        | _ -> Unsupported

    let private identical (left: SqlCatalogState) (right: SqlCatalogState) =
        left.IsClosedWorld = right.IsClosedWorld
        && left.Tables.Count = right.Tables.Count
        && (left.Tables |> Seq.forall (fun a ->
            right.Tables |> Seq.exists (fun b ->
                String.Equals(a.Name, b.Name, StringComparison.Ordinal)
                && a.Knowledge = b.Knowledge
                && a.Columns.Count = b.Columns.Count
                && (a.Columns |> Seq.forall (fun col ->
                    b.Columns |> Seq.exists (fun other ->
                        String.Equals(col.Name, other.Name, StringComparison.Ordinal)
                        && String.Equals(col.TypeName, other.TypeName, StringComparison.Ordinal)
                        && col.Nullable = other.Nullable)))))

    let private error syntax code message =
        SqlDiagnostic(
            code,
            SqlDiagnosticStage.SemanticValidation,
            SqlDiagnosticCategory.Capability,
            message,
            syntax.Span)

    let private delta syntax certainty =
        SqlCatalogChange("*", SqlCatalogOperation.Indeterminate, certainty, syntax.Span)

    let analyze sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                (current: SqlCatalogState) (context: TransactionContext)
                (syntax: SqlScriptStatementSyntax) =
        let parsed =
            RewriteResourceBudget.withStage "TransactionLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
        let operation =
            if sourceDialect = SqlAgentToolType.Postgres then command parsed else Unsupported
        let uncertain code message =
            let state = RewriteCatalogModel.taintAll current
            SqlStatementAnalysisStatus.Unsupported,
            SqlCatalogKnowledge.Unknown,
            state,
            Unknown,
            [delta syntax SqlCatalogKnowledge.Unknown],
            [error syntax code message]
        match operation, context with
        | Begin, Outside ->
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            current, Active current, [], []
        | Commit, Active _ ->
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            current, Outside, [], []
        | Rollback, Active original ->
            // PostgreSQL's modeled TABLE DDL is transactional; this is valid
            // only when every intervening effect has remained in the proven
            // subset and the transaction context is still Active.
            let changes =
                if identical current original then []
                else [delta syntax SqlCatalogKnowledge.Known]
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            original, Outside, changes, []
        | Unsupported, _ ->
            uncertain "SQL_TRANSACTION_UNSUPPORTED"
                "Transaction grammar or source dialect is not modeled; Catalog proofs are invalidated."
        | _, Unknown ->
            uncertain "SQL_TRANSACTION_STATE_UNKNOWN"
                "Transaction state is unknown; this command cannot re-establish schema certainty."
        | Begin, Active _ ->
            uncertain "SQL_TRANSACTION_NESTED_UNPROVEN"
                "Nested transaction BEGIN is not statically proven."
        | Commit, Outside
        | Rollback, Outside ->
            uncertain "SQL_TRANSACTION_WITHOUT_BEGIN"
                "COMMIT or ROLLBACK has no proven preceding BEGIN."
    
    /// Errors, unsupported DDL and unmodeled statement effects inside a
    /// transaction may invalidate the entire transaction's control flow.
    /// Never allow a future ROLLBACK to restore precise facts after that.
    let invalidateIfUnproven context (kind: SqlScriptStatementKind)
                (status: SqlStatementAnalysisStatus) =
        match context with
        | Active _ when status = SqlStatementAnalysisStatus.Invalid
                        || status = SqlStatementAnalysisStatus.Unsupported
                        || kind = SqlScriptStatementKind.Session
                        || kind = SqlScriptStatementKind.Utility
                        || kind = SqlScriptStatementKind.Procedural
                        || kind = SqlScriptStatementKind.Unknown
                        || kind = SqlScriptStatementKind.Security -> Unknown
        | other -> other
