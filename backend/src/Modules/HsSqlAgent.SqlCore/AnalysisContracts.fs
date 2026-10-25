namespace HsSqlAgent.SqlCore.Script

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation

[<Flags>]
type SqlEffectKind =
    | None = 0
    | ReadData = 1
    | WriteData = 2
    | ChangeSchema = 4
    | ChangePermissions = 8
    | ChangeTransaction = 16
    | ChangeSession = 32
    | Administrative = 64

type SqlStatementAnalysisStatus =
    | Analyzed = 0
    | PartiallyAnalyzed = 1
    | Unsupported = 2
    | Invalid = 3

/// A separate proof of static name/type binding; never compiler authorization.
type SqlCatalogBindingStatus =
    | NotApplicable = 0
    | Verified = 1
    | Unverified = 2
    | Rejected = 3

/// Static transaction state, not evidence of server execution.
/// Unknown means no schema proof can survive unmodeled transaction effects.
type SqlTransactionState =
    | Outside = 0
    | Active = 1
    | Unknown = 2

/// Effects are *potential* unless certainty is Known. This is not an
/// execution permission, SQL compiler proof, or security authorization.
[<Sealed>]
type SqlStatementAnalysis internal (
    syntax: SqlScriptStatementSyntax,
    status: SqlStatementAnalysisStatus,
    effects: SqlEffectKind,
    certainty: SqlCatalogKnowledge,
    catalogBinding: SqlCatalogBindingStatus,
    catalogBefore: SqlCatalogState,
    catalogAfter: SqlCatalogState,
    transactionBefore: SqlTransactionState,
    transactionAfter: SqlTransactionState,
    changes: IReadOnlyList<SqlCatalogChange>,
    diagnostics: IReadOnlyList<SqlDiagnostic>) =
    member _.Syntax = syntax
    member _.Kind = syntax.Kind
    member _.Status = status
    member _.Effects = effects
    member _.Certainty = certainty
    member _.CatalogBinding = catalogBinding
    member _.CatalogBefore = catalogBefore
    member _.CatalogAfter = catalogAfter
    member _.TransactionBefore = transactionBefore
    member _.TransactionAfter = transactionAfter
    member _.Changes = changes
    member _.Diagnostics = diagnostics

[<Sealed>]
type SqlScriptAnalysisResult internal (
    statements: IReadOnlyList<SqlStatementAnalysis>,
    diagnostics: IReadOnlyList<SqlDiagnostic>,
    finalCatalog: SqlCatalogState,
    finalTransaction: SqlTransactionState) =
    member _.Statements = statements
    member _.Diagnostics = diagnostics
    member _.FinalCatalog = finalCatalog
    member _.FinalTransaction = finalTransaction
