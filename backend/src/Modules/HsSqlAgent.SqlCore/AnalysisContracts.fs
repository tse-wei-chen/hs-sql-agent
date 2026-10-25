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
    | GrantPrivilege = 16
    | RevokePrivilege = 17
    | DenyPrivilege = 18
    | GrantRoleMembership = 19
    | RevokeRoleMembership = 20

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


/// DCL describes syntactic *intent*, not effective database permissions.
/// No principal, grantor, inherited role, ownership or server ACL is attested.
type SqlSecurityAction =
    | Grant = 0
    | Revoke = 1
    | Deny = 2

type SqlTablePrivilege =
    | Select = 0
    | Insert = 1
    | Update = 2
    | Delete = 3

type SqlSecurityPrincipalKind =
    | Named = 0
    | User = 1
    | Role = 2
    | Public = 3
    | MySqlAccount = 4


/// How a syntactic DCL statement treats possible downstream grants.
/// This is not a proof that any dependent ACL or role actually exists.
type SqlSecurityCascadeMode =
    | None = 0
    | Cascade = 1
    | Restrict = 2

type SqlSecurityDependencyKind =
    | RoleMembership = 0
    | TableDelegation = 1

/// Directed *potential* edge only. RoleMembership means Principal may become
/// a member of Role; TableDelegation means Principal may delegate Privilege
/// on Table. The graph is not a representation of effective server ACLs.
[<Sealed>]
type SqlSecurityDependency internal (
    kind: SqlSecurityDependencyKind,
    action: SqlSecurityAction,
    principal: string,
    principalKind: SqlSecurityPrincipalKind,
    role: string | null,
    table: SqlCatalogQualifiedName | null,
    privilege: Nullable<SqlTablePrivilege>,
    withDelegationOption: bool,
    revokeOptionOnly: bool,
    cascadeMode: SqlSecurityCascadeMode,
    span: SqlDiagnosticSpan) =
    member _.Kind = kind
    member _.Action = action
    member _.Principal = principal
    member _.PrincipalKind = principalKind
    member _.Role = role
    member _.Table = table
    member _.Privilege = privilege
    member _.WithDelegationOption = withDelegationOption
    member _.RevokeOptionOnly = revokeOptionOnly
    member _.CascadeMode = cascadeMode
    member _.Span = span
    member _.Certainty = SqlCatalogKnowledge.Unknown

/// A parsed potential privilege change. Target is the syntax-qualified name,
/// NEVER an identity proven to exist. Even a syntactically valid GRANT might
/// fail or be ignored, so Certainty is unconditionally Unknown.
[<Sealed>]
type SqlSecurityIntent internal (
    action: SqlSecurityAction,
    privilege: SqlTablePrivilege,
    target: SqlCatalogQualifiedName,
    principal: string,
    principalKind: SqlSecurityPrincipalKind,
    span: SqlDiagnosticSpan,
    principalHost: string | null,
    withDelegationOption: bool,
    revokeOptionOnly: bool,
    cascadeMode: SqlSecurityCascadeMode) =
    member _.Action = action
    member _.Privilege = privilege
    member _.Target = target
    member _.Principal = principal
    /// Explicit MySQL account Host as written in the script, NEVER a server
    /// identity/ACL proof. Null for non-MySQL principals.
    member _.PrincipalHost = principalHost
    member _.PrincipalKind = principalKind
    member _.WithDelegationOption = withDelegationOption
    member _.RevokeOptionOnly = revokeOptionOnly
    member _.CascadeMode = cascadeMode
    member _.Certainty = SqlCatalogKnowledge.Unknown
    member _.Span = span

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
    securityIntents: IReadOnlyList<SqlSecurityIntent>,
    securityDependencies: IReadOnlyList<SqlSecurityDependency>,
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
    member _.SecurityIntents = securityIntents
    member _.SecurityDependencies = securityDependencies
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
    let securityIntents : IReadOnlyList<SqlSecurityIntent> =
        (statements
         |> Seq.collect (fun item -> item.SecurityIntents)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlSecurityIntent>
    let securityDependencies : IReadOnlyList<SqlSecurityDependency> =
        (statements
         |> Seq.collect (fun item -> item.SecurityDependencies)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlSecurityDependency>
    member _.DetailedEffects = detailedEffects
    member _.SecurityIntents = securityIntents
    member _.SecurityDependencies = securityDependencies
    member _.Statements = statements
    member _.Diagnostics = diagnostics
    member _.FinalCatalog = finalCatalog
    member _.FinalTransaction = finalTransaction
    member _.FinalSession = finalSession
