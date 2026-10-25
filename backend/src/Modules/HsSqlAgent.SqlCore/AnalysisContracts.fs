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
    | UnknownEffects = 128

/// The declared scope of the static effect description, NOT a proof of runtime behavior.
type SqlEffectCoverage =
    | Complete = 0
    | Partial = 1
    | Unknown = 2

/// Typed operation tags. Unknown preserves unmodeled or conditional behavior.
type SqlStatementEffectOperation =
    | Unknown = 0
    | ReadTable = 1
    | Insert = 2
    | Update = 3
    | Delete = 4
    | Merge = 5
    | CreateTable = 6
    | DropTable = 7
    | AddColumn = 8
    | DropColumn = 9
    | CreateIndex = 10
    | DropIndex = 11
    | AddConstraint = 12
    | DropConstraint = 13
    | TransactionControl = 14
    | SetSearchPath = 15

/// Static potential effect. ObjectName and Dependencies are diagnostic names,
/// not trusted resolved identities. Only Catalog operations can have ObjectIdentity.
/// Known certainty means a proven static catalog delta, never server execution.
[<Sealed>]
type SqlStatementEffect internal (
    kind: SqlEffectKind,
    operation: SqlStatementEffectOperation,
    objectName: string | null,
    objectIdentity: SqlCatalogQualifiedName | null,
    certainty: SqlCatalogKnowledge,
    span: SqlDiagnosticSpan,
    dependencies: IReadOnlyList<string>) =
    member _.Kind = kind
    member _.Operation = operation
    member _.ObjectName = objectName
    member _.ObjectIdentity = objectIdentity
    member _.Certainty = certainty
    member _.Span = span
    member _.Dependencies = dependencies

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

/// Immutable static session scope. A known SearchPath is a caller/script
/// premise, never evidence from a live server or a compilation permission.
[<Sealed>]
type SqlSessionAnalysisState internal (searchPath: string | null) =
    member _.SearchPath = searchPath
    member _.IsSearchPathProven = not (isNull searchPath)

/// Effects are *potential* unless certainty is Known. This is not an
/// execution permission, SQL compiler proof, or security authorization.
[<Sealed>]
type SqlStatementAnalysis internal (
    syntax: SqlScriptStatementSyntax,
    status: SqlStatementAnalysisStatus,
    effects: SqlEffectKind,
    certainty: SqlCatalogKnowledge,
    catalogBinding: SqlCatalogBindingStatus,
    nameBinding: SqlCatalogBindingStatus,
    typeBinding: SqlCatalogBindingStatus,
    catalogBefore: SqlCatalogState,
    catalogAfter: SqlCatalogState,
    transactionBefore: SqlTransactionState,
    transactionAfter: SqlTransactionState,
    sessionBefore: SqlSessionAnalysisState,
    sessionAfter: SqlSessionAnalysisState,
    changes: IReadOnlyList<SqlCatalogChange>,
    detailedEffects: IReadOnlyList<SqlStatementEffect>,
    effectCoverage: SqlEffectCoverage,
    diagnostics: IReadOnlyList<SqlDiagnostic>) =
    member _.Syntax = syntax
    member _.Kind = syntax.Kind
    member _.Status = status
    member _.Effects = effects
    member _.Certainty = certainty
    member _.CatalogBinding = catalogBinding
    /// Proof of physical and scoped SQL identifiers against the Virtual Catalog.
    member _.NameBinding = nameBinding
    /// Proof of the modeled source-dialect value categories; independent of name binding.
    member _.TypeBinding = typeBinding
    member _.CatalogBefore = catalogBefore
    member _.CatalogAfter = catalogAfter
    member _.TransactionBefore = transactionBefore
    member _.TransactionAfter = transactionAfter
    member _.SessionBefore = sessionBefore
    member _.SessionAfter = sessionAfter
    member _.Changes = changes
    member _.DetailedEffects = detailedEffects
    member _.EffectCoverage = effectCoverage
    member _.Diagnostics = diagnostics

[<Sealed>]
type SqlScriptAnalysisResult internal (
    statements: IReadOnlyList<SqlStatementAnalysis>,
    diagnostics: IReadOnlyList<SqlDiagnostic>,
    finalCatalog: SqlCatalogState,
    finalTransaction: SqlTransactionState,
    finalSession: SqlSessionAnalysisState) =
    let detailedEffects : IReadOnlyList<SqlStatementEffect> =
        (statements
         |> Seq.collect (fun item -> item.DetailedEffects)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlStatementEffect>
    member _.DetailedEffects = detailedEffects
    member _.Statements = statements
    member _.Diagnostics = diagnostics
    member _.FinalCatalog = finalCatalog
    member _.FinalTransaction = finalTransaction
    member _.FinalSession = finalSession
