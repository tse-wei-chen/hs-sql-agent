namespace HsSqlAgent.SqlCore.Ddl

open System
open System.Collections.Generic
open System.Threading
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// This is compilation policy, NOT a database privilege, human approval, or
/// a proven physical schema state. Cross-provider DDL is deliberately closed.
[<Sealed>]
type DdlCompilationOptions(
    sourceDialect: SqlAgentToolType,
    targetDialect: SqlAgentToolType,
    sourceProfile: SqlProviderCapabilityProfile | null,
    targetProfile: SqlProviderCapabilityProfile | null,
    cancellationToken: CancellationToken) =

    new(sourceDialect, targetDialect) =
        DdlCompilationOptions(sourceDialect, targetDialect, null, null, CancellationToken.None)
    new(sourceDialect, targetDialect, sourceProfile, targetProfile) =
        DdlCompilationOptions(sourceDialect, targetDialect, sourceProfile, targetProfile, CancellationToken.None)

    member _.SourceDialect = sourceDialect
    member _.TargetDialect = targetDialect
    member _.SourceProfile = sourceProfile
    member _.TargetProfile = targetProfile
    member _.CancellationToken = cancellationToken

type DdlSchemaOperation =
    | CreateTable = 0
    | AddNullableColumn = 1
    | CreateIndex = 2
    | DropTable = 3

/// These are REQUIRED live preconditions, never claims that reality satisfies them.
type DdlPrecondition =
    | RequireObjectAbsent = 0
    | RequireTablePresentAndColumnAbsent = 1
    | RequireTableAndColumnsPresentAndIndexAbsent = 2
    | RequireTablePresentAndEmpty = 3

type DdlExecutionRisk =
    | Additive = 0
    | PotentialLock = 1
    | Destructive = 2

/// Transaction behavior describes a general provider class, not runtime proof.
type DdlTransactionBehavior =
    | Unknown = 0
    | ImplicitCommit = 1

[<Sealed>]
type DdlSchemaChange internal (
    objectIdentity: SqlCatalogQualifiedName,
    operation: DdlSchemaOperation,
    precondition: DdlPrecondition,
    columns: IReadOnlyList<string>,
    indexName: string | null,
    columnDefinitions: IReadOnlyList<SqlCatalogColumn>) =
    member _.ObjectIdentity = objectIdentity
    member _.Operation = operation
    member _.Precondition = precondition
    member _.Columns = columns
    member _.IndexName = indexName
    /// Typed from G1's closed DdlOperation, never parsed from rendered SQL.
    member _.ColumnDefinitions = columnDefinitions
    member _.Certainty = SqlCatalogKnowledge.Unknown

[<Sealed>]
type DdlSchemaImpact internal (mayLockSchema: bool, mayLoseData: bool) =
    member _.MayLockSchema = mayLockSchema
    member _.MayLoseData = mayLoseData
    /// No live row counts/lock estimate is available in a driver-free compiler.
    member _.EstimatedAffectedRows : Nullable<int64> = Nullable()

[<Sealed>]
type DdlCompileEvidence internal (
    sourceDialect: SqlAgentToolType,
    targetDialect: SqlAgentToolType,
    sourceVersion: Version | null,
    targetVersion: Version | null) =
    member _.SchemaVersion = 1
    member _.SourceDialect = sourceDialect
    member _.TargetDialect = targetDialect
    member _.SourceVersion = sourceVersion
    member _.TargetVersion = targetVersion
    member _.RequiresPhysicalPreconditions = true

/// DDL never masquerades as the DQL/DML CompiledSqlCommand kind.
[<Sealed>]
type DdlCompiledCommand internal (sql: string, targetDialect: SqlAgentToolType) =
    member _.Sql = sql
    member _.TargetDialect = targetDialect
    member _.Parameters : IReadOnlyList<SqlParameterValue> =
        Array.Empty<SqlParameterValue>() :> IReadOnlyList<SqlParameterValue>

[<Sealed>]
type CompiledDdlPlan internal (
    command: DdlCompiledCommand,
    targetDialect: SqlAgentToolType,
    changes: IReadOnlyList<DdlSchemaChange>,
    impact: DdlSchemaImpact,
    risk: DdlExecutionRisk,
    transactionBehavior: DdlTransactionBehavior,
    evidence: DdlCompileEvidence,
    planFingerprint: string) =
    member _.Command = command
    member _.TargetDialect = targetDialect
    member _.Changes = changes
    member _.Impact = impact
    member _.Risk = risk
    member _.TransactionBehavior = transactionBehavior
    member _.Evidence = evidence
    member _.PlanFingerprint = planFingerprint
    member _.RequiresLiveValidation = true
    member _.AuthorizesExecution = false

/// G6/S16: immutable, non-authoritative ordered script proof. Atomicity is
/// UNKNOWN until a particular Host commits a real provider transaction.
[<Sealed>]
type CompiledDdlBatchPlan internal (
    plans: IReadOnlyList<CompiledDdlPlan>,
    sourceSpans: IReadOnlyList<SqlDiagnosticSpan>,
    fingerprint: string) =
    member _.Plans = plans
    member _.SourceSpans = sourceSpans
    member _.PlanFingerprint = fingerprint
    member _.TransactionBehavior = DdlTransactionBehavior.Unknown
    member _.RequiresLiveValidation = true
    member _.AuthorizesExecution = false
