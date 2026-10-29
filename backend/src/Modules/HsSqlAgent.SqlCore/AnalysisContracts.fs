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
    | ExplainPlan = 21
    | ExplainExecute = 22
    | AnalyzeStatistics = 23
    | VacuumMaintenance = 24
    | UseDatabase = 25
    | SetNoCount = 26
    | InvokeProcedure = 27
    | DefineProcedure = 28
    | DefineFunction = 29
    | PossibleImplicitCommit = 30
    | CreateView = 31
    | DropView = 32
    | ReplaceView = 33
    | RenameColumn = 34

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
    dependencies: IReadOnlyList<string>,
    constraintName: string) =
    new(kind, operation, objectName, objectIdentity, certainty, span, dependencies) =
        SqlStatementEffect(kind, operation, objectName, objectIdentity,
                           certainty, span, dependencies, null)
    member _.Kind = kind
    member _.Operation = operation
    member _.ObjectName = objectName
    member _.ObjectIdentity = objectIdentity
    member _.Certainty = certainty
    member _.Span = span
    member _.Dependencies = dependencies
    /// Proven constraint name from a Catalog delta; null on other effects.
    member _.ConstraintName = constraintName

/// Potential object reference derived only from a previously classified static
/// statement effect; never a proof of physical existence or server privileges.
type SqlScriptDependencyKind =
    | ReadTable = 0
    | MutationTarget = 1
    | PermissionTarget = 2
    | RoutineInvocation = 3

[<Sealed>]
type SqlScriptDependencyReference internal (
    statementIndex: int,
    kind: SqlScriptDependencyKind,
    objectName: string,
    span: SqlDiagnosticSpan) =
    member _.StatementIndex = statementIndex
    member _.Kind = kind
    /// An untrusted diagnostic display name, not a proven Catalog identity.
    member _.ObjectName = objectName
    /// At best a whole-statement span, not necessarily an exact identifier span.
    member _.Span = span
    /// Syntactic dependencies never prove the server object exists.
    member _.Certainty = SqlCatalogKnowledge.Unknown

/// Only final, statically proven declarations in the Virtual Catalog are
/// represented as Symbols; SQL aliases and lexical references are not inferred.
type SqlScriptSymbolKind =
    | Table = 0
    | Column = 1

[<Sealed>]
type SqlScriptSymbol internal (
    kind: SqlScriptSymbolKind,
    name: string,
    tableIdentity: SqlCatalogQualifiedName,
    typeName: string | null,
    nullable: Nullable<bool>) =
    member _.Kind = kind
    /// Normalized declaration name in the final Virtual Catalog.
    member _.Name = name
    /// Structured table identity. Never derive it by splitting Name on dots.
    member _.TableIdentity = tableIdentity
    /// Present for Column declarations, null for Table.
    member _.TypeName = typeName
    /// Populated only for columns. Null is not equivalent to nullable=true.
    member _.Nullable = nullable
    /// Knowledge of the modeled virtual declaration, NOT live database existence.
    member _.Certainty = SqlCatalogKnowledge.Known

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

/// Source-row cardinality evidence for a joined Mutation, independent from
/// Catalog name/type proof. No runtime data, permissions or effect certainty.
type SqlMutationSourceMatchCardinality =
    | NotApplicable = 0
    | Unverified = 1
    | ProvenAtMostOne = 2


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

/// Source-provider invocation syntax recognized without inspecting a body.
/// A procedure can invoke DML, DDL, dynamic SQL, external code and routines.
type SqlProceduralInvocationKind =
    | Call = 0
    | Exec = 1
    | ExecuteProcedure = 2

/// Syntactic *potential* invocation only. Target is untrusted script text,
/// not a Catalog identity, executable plan, authorized routine or body proof.
[<Sealed>]
type SqlProceduralIntent internal (
    invocationKind: SqlProceduralInvocationKind,
    routine: SqlCatalogQualifiedName,
    potentialEffects: SqlEffectKind,
    span: SqlDiagnosticSpan) =
    member _.InvocationKind = invocationKind
    member _.Routine = routine
    member _.PotentialEffects = potentialEffects
    member _.MayExecuteSql = true
    member _.BodyAnalyzed = false
    member _.RuntimeCertainty = SqlCatalogKnowledge.Unknown
    member _.Span = span

/// Strictly syntax-level PostgreSQL SQL-language routine definition.
/// Functions need separate return-value/volatility proofs and remain opaque.
type SqlProceduralDefinitionKind =
    | Procedure = 0
    | Function = 1

/// Closed, deliberately limited PostgreSQL SQL-language function returns.
/// The analyzer does not infer arbitrary column, user-defined or implicit
/// assignment-cast types from server metadata.
type SqlFunctionReturnType =
    | Integer = 0
    | BigInteger = 1
    | Boolean = 2
    | Text = 3
    | Void = 4

/// The static AST evidence for the declared result shape, not proof that a
/// CREATE FUNCTION, SELECT or actual server conversion ever succeeded.
type SqlFunctionReturnEvidence =
    | LiteralMatch = 0
    | ExplicitCastMatch = 1
    | VoidNoValueRequired = 2
    | ParameterSymbolMatch = 3
    | PrimitiveExpressionMatch = 4
    | PositionalParameterMatch = 5

[<Sealed; AllowNullLiteral>]
type SqlFunctionReturnContract internal (
    declaredType: SqlFunctionReturnType,
    evidence: SqlFunctionReturnEvidence,
    resultSpan: SqlDiagnosticSpan) =
    member _.DeclaredType = declaredType
    member _.Evidence = evidence
    member _.ResultSpan = resultSpan
    member _.IsRuntimeReturnProven = false
    member _.RuntimeCertainty = SqlCatalogKnowledge.Unknown

/// A declared PostgreSQL IN-only, primitive SQL-language function
/// argument. Position is 1-based; normalized spelling is not a Catalog
/// identity or a proof of execution, and quoted/OUT/default forms are excluded.
[<Sealed>]
type SqlFunctionParameterSymbol internal (
    ordinal: int,
    name: string,
    declaredType: SqlFunctionReturnType,
    span: SqlDiagnosticSpan) =
    member _.Ordinal = ordinal
    member _.Name = name
    member _.DeclaredType = declaredType
    member _.Span = span
    member _.RuntimeCertainty = SqlCatalogKnowledge.Unknown

/// One closed F# AST statement in a bounded SQL-language procedure or function body.
/// No semantic Catalog binding, function effects, triggers or permission
/// checks are implied. Span is absolute in the submitted SQL script.
[<Sealed>]
type SqlProceduralBodyStatement internal (
    kind: SqlScriptStatementKind,
    operation: SqlStatementEffectOperation,
    effects: SqlEffectKind,
    referencedTables: IReadOnlyList<string>,
    span: SqlDiagnosticSpan) =
    member _.Kind = kind
    member _.Operation = operation
    member _.PotentialEffects = effects
    member _.ReferencedTables = referencedTables
    member _.Span = span
    member _.RuntimeCertainty = SqlCatalogKnowledge.Unknown

/// Static inspection of a SQL-language PROCEDURE definition, not its
/// execution, existence on the server or resolved effect closure.
[<Sealed>]
type SqlProceduralDefinition internal (
    kind: SqlProceduralDefinitionKind,
    routine: SqlCatalogQualifiedName,
    bodyStatements: IReadOnlyList<SqlProceduralBodyStatement>,
    potentialBodyEffects: SqlEffectKind,
    span: SqlDiagnosticSpan,
    functionReturn: SqlFunctionReturnContract | null,
    parameters: IReadOnlyList<SqlFunctionParameterSymbol>) =
    member _.Kind = kind
    member _.Routine = routine
    member _.BodyStatements = bodyStatements
    member _.PotentialBodyEffects = potentialBodyEffects
    /// Null for PROCEDURE; a proven static shape only for FUNCTION.
    member _.FunctionReturn = functionReturn
    member _.Parameters = parameters
    member _.Span = span
    member _.BodyAnalyzed = true
    member _.IsBodyExecutionProven = false
    member _.RuntimeCertainty = SqlCatalogKnowledge.Unknown

/// Provider-specific static utility operation. Not an executable SQL plan.
type SqlUtilityOperation =
    | ExplainPlan = 0
    | ExplainExecute = 1
    | AnalyzeStatistics = 2
    | VacuumMaintenance = 3

type SqlUtilityExecutionMode =
    | PlanningOnly = 0
    | ExecutesStatement = 1
    | Maintenance = 2

/// What the *SQL text could do* on its named provider. No statement has
/// actually run, and unmodeled functions, triggers or metadata remain unknown.
[<Sealed>]
type SqlUtilityIntent internal (
    operation: SqlUtilityOperation,
    executionMode: SqlUtilityExecutionMode,
    innerStatementKind: SqlScriptStatementKind,
    targetName: string | null,
    potentialEffects: SqlEffectKind,
    span: SqlDiagnosticSpan) =
    member _.Operation = operation
    member _.ExecutionMode = executionMode
    member _.InnerStatementKind = innerStatementKind
    member _.TargetName = targetName
    member _.PotentialEffects = potentialEffects
    member _.MayExecuteStatement = executionMode = SqlUtilityExecutionMode.ExecutesStatement
    member _.Certainty = SqlCatalogKnowledge.Unknown
    member _.Span = span

/// Static transaction state, not evidence of server execution.
/// Unknown means no schema proof can survive unmodeled transaction effects.
type SqlTransactionState =
    | Outside = 0
    | Active = 1
    | Unknown = 2

/// Closed, provider-aware grammar classification. The command is recognized
/// statically, not executed on a database server.
type SqlTransactionControlOperation =
    | None = 0
    | Begin = 1
    | Commit = 2
    | Rollback = 3
    | Savepoint = 4
    | RollbackToSavepoint = 5
    | ReleaseSavepoint = 6
    | Unknown = 7

/// What supports a *virtual* rollback claim. Even StaticCatalogAndSession
/// NEVER implies that any database change has run or been rolled back.
type SqlTransactionRollbackScope =
    | NotApplicable = 0
    | StaticCatalog = 1
    | StaticCatalogAndSession = 2
    | Unproven = 3

/// Conservative, non-executable source of the per-statement transaction fact.
type SqlTransactionEffectOrigin =
    | OutsideTransaction = 0
    | TransactionControl = 1
    | CatalogMutation = 2
    | SessionMutation = 3
    | PossibleImplicitCommit = 4
    | UnprovenRuntimeEffects = 5
    | UnknownTransactionState = 6

/// The statement's potential transaction effect and evidence boundary.
/// The source dialect's static grammar can prove Catalog snapshot transitions;
/// it can NEVER certify storage engine execution, successful COMMIT/ROLLBACK,
/// effective permissions, or a physical server's transactional guarantees.
[<Sealed>]
type SqlTransactionEffectProvenance internal (
    origin: SqlTransactionEffectOrigin,
    rollbackScope: SqlTransactionRollbackScope,
    controlOperation: SqlTransactionControlOperation,
    effects: SqlEffectKind,
    transactionBefore: SqlTransactionState,
    transactionAfter: SqlTransactionState,
    span: SqlDiagnosticSpan) =
    member _.Origin = origin
    member _.RollbackScope = rollbackScope
    member _.ControlOperation = controlOperation
    member _.PotentialEffects = effects
    member _.TransactionBefore = transactionBefore
    member _.TransactionAfter = transactionAfter
    member _.Span = span
    member _.RuntimeCertainty = SqlCatalogKnowledge.Unknown
    member _.IsServerRollbackProven = false

/// Immutable static session scope. A known SearchPath is a caller/script
/// premise, never evidence from a live server or a compilation permission.
[<Sealed>]
type SqlSessionAnalysisState internal (
    searchPath: string | null,
    currentDatabase: string | null,
    isDatabaseScopeProven: bool,
    noCount: Nullable<bool>) =
    member _.SearchPath = searchPath
    member _.IsSearchPathProven = not (isNull searchPath)
    /// Script/caller-declared current database, not proof it exists or that
    /// the executing principal has access.
    member _.CurrentDatabase = currentDatabase
    /// A single-schema lookup can be projected only when the provider's
    /// identifier rules AND the existing virtual Catalog are safe.
    member _.IsDatabaseScopeProven = isDatabaseScopeProven
    /// SQL Server NOCOUNT as a statically parsed option; null = unknown.
    member _.NoCount = noCount

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
    utilityIntents: IReadOnlyList<SqlUtilityIntent>,
    proceduralIntents: IReadOnlyList<SqlProceduralIntent>,
    proceduralDefinitions: IReadOnlyList<SqlProceduralDefinition>,
    transactionProvenance: SqlTransactionEffectProvenance,
    diagnostics: IReadOnlyList<SqlDiagnostic>) =
    member _.Syntax = syntax
    member _.Kind = syntax.Kind
    member _.Status = status
    member _.Effects = effects
    member _.Certainty = certainty
    member _.CatalogBinding = catalogBinding
    /// Initially unproven: joined Mutation cardinality requires a separate
    /// structural proof. Ordinary DQL/DDL does not have a Mutation source.
    member _.SourceMatchCardinality =
        if syntax.Kind = SqlScriptStatementKind.Mutation then
            SqlMutationSourceMatchCardinality.Unverified
        else SqlMutationSourceMatchCardinality.NotApplicable
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
    member _.UtilityIntents = utilityIntents
    member _.ProceduralIntents = proceduralIntents
    member _.ProceduralDefinitions = proceduralDefinitions
    member _.TransactionProvenance = transactionProvenance
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

    // A script cannot provide stronger static effect evidence than its least
    // certain statement. This is an analysis summary, never execution proof.
    let effectCoverage =
        if statements |> Seq.exists (fun item ->
            item.EffectCoverage = SqlEffectCoverage.Unknown
            || item.Status = SqlStatementAnalysisStatus.Invalid
            || item.Status = SqlStatementAnalysisStatus.Unsupported) then
            SqlEffectCoverage.Unknown
        elif statements |> Seq.exists (fun item ->
            item.EffectCoverage = SqlEffectCoverage.Partial
            || item.Status = SqlStatementAnalysisStatus.PartiallyAnalyzed) then
            SqlEffectCoverage.Partial
        else
            SqlEffectCoverage.Complete

    // Preserve statement-order deltas (including uncertainty); do not compute
    // this from FinalCatalog, which can hide drops, rollbacks, and intermediate
    // changes in a multi-statement script.
    let catalogChanges : IReadOnlyList<SqlCatalogChange> =
        (statements
         |> Seq.collect (fun item -> item.Changes)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlCatalogChange>

    // Project only typed object references already present in the analyzed
    // effect model. In particular, a CREATE/DROP declaration is a Catalog
    // change, not a read/write dependency, and Unknown operations invent none.
    // Do not promote these display names to resolved physical Catalog identities.
    let dependencies : IReadOnlyList<SqlScriptDependencyReference> =
        (statements
         |> Seq.mapi (fun index statement ->
             if statement.Status = SqlStatementAnalysisStatus.Invalid
                || statement.Status = SqlStatementAnalysisStatus.Unsupported then
                 Seq.empty
             else
                 statement.DetailedEffects
                 |> Seq.choose (fun effect ->
                     if String.IsNullOrWhiteSpace(effect.ObjectName) then None
                     else
                         let kind =
                             match effect.Operation with
                             | SqlStatementEffectOperation.ReadTable ->
                                 Some SqlScriptDependencyKind.ReadTable
                             | SqlStatementEffectOperation.Insert
                             | SqlStatementEffectOperation.Update
                             | SqlStatementEffectOperation.Delete
                             | SqlStatementEffectOperation.Merge ->
                                 Some SqlScriptDependencyKind.MutationTarget
                             | SqlStatementEffectOperation.GrantPrivilege
                             | SqlStatementEffectOperation.RevokePrivilege
                             | SqlStatementEffectOperation.DenyPrivilege ->
                                 Some SqlScriptDependencyKind.PermissionTarget
                             | SqlStatementEffectOperation.InvokeProcedure ->
                                 Some SqlScriptDependencyKind.RoutineInvocation
                             | _ -> None
                         kind |> Option.map (fun value ->
                             SqlScriptDependencyReference(
                                 index, value, effect.ObjectName, effect.Span))))
         |> Seq.collect id
         // Deduplicate only within the same statement, kind and exact source
         // name; case folding would conflate quoted identifiers on some DBs.
         |> Seq.distinctBy (fun item ->
             item.StatementIndex, item.Kind, item.ObjectName)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlScriptDependencyReference>

    // The Symbol projection describes FINAL virtual Catalog declarations only.
    // Never reconstruct identities from lossy display names or claim unknown
    // table shapes were verified. DDL rolled back/dropped mid-script disappears.
    let hasCompleteSymbolCatalog =
        finalCatalog.IsClosedWorld
        && (finalCatalog.Tables
            |> Seq.forall (fun table -> table.Knowledge = SqlCatalogKnowledge.Known))

    let symbols : IReadOnlyList<SqlScriptSymbol> =
        (finalCatalog.Tables
         |> Seq.filter (fun table -> table.Knowledge = SqlCatalogKnowledge.Known)
         |> Seq.collect (fun table ->
             seq {
                 yield SqlScriptSymbol(
                     SqlScriptSymbolKind.Table, table.Name,
                     table.QualifiedName, null, Nullable<bool>())
                 for column in table.Columns do
                     yield SqlScriptSymbol(
                         SqlScriptSymbolKind.Column, column.Name,
                         table.QualifiedName, column.TypeName,
                         Nullable<bool>(column.Nullable))
             })
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlScriptSymbol>

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
    let utilityIntents : IReadOnlyList<SqlUtilityIntent> =
        (statements
         |> Seq.collect (fun item -> item.UtilityIntents)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlUtilityIntent>
    let transactionProvenance : IReadOnlyList<SqlTransactionEffectProvenance> =
        (statements
         |> Seq.map (fun item -> item.TransactionProvenance)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlTransactionEffectProvenance>
    let proceduralIntents : IReadOnlyList<SqlProceduralIntent> =
        (statements
         |> Seq.collect (fun item -> item.ProceduralIntents)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlProceduralIntent>
    let proceduralDefinitions : IReadOnlyList<SqlProceduralDefinition> =
        (statements
         |> Seq.collect (fun item -> item.ProceduralDefinitions)
         |> Seq.toArray
         |> Array.AsReadOnly) :> IReadOnlyList<SqlProceduralDefinition>
    member _.DetailedEffects = detailedEffects
    /// Least-proven static effect coverage across the script. Not authorization.
    member _.EffectCoverage = effectCoverage
    /// All per-statement static Catalog deltas in source order, as an immutable snapshot.
    member _.CatalogChanges = catalogChanges
    /// Unverified object references from typed effects, never effective permissions
    /// or physical-object identity proofs. Does not include DDL declarations.
    member _.Dependencies = dependencies
    /// Proven declarations of the final Virtual Catalog only: Table and Column.
    /// Never a full SQL lexical symbol table or proof of live server existence.
    member _.Symbols = symbols
    /// False when the caller's initial Catalog or any later statement leaves
    /// parts of the virtual symbol universe unknown.
    member _.HasCompleteSymbolCatalog = hasCompleteSymbolCatalog
    member _.ProceduralDefinitions = proceduralDefinitions
    member _.ProceduralIntents = proceduralIntents
    member _.TransactionProvenance = transactionProvenance
    member _.UtilityIntents = utilityIntents
    member _.SecurityIntents = securityIntents
    member _.SecurityDependencies = securityDependencies
    member _.Statements = statements
    member _.Diagnostics = diagnostics
    member _.FinalCatalog = finalCatalog
    member _.FinalTransaction = finalTransaction
    member _.FinalSession = finalSession
