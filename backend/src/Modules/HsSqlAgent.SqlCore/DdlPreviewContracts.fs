namespace HsSqlAgent.SqlCore.Ddl

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog

/// Presence within a caller-declared virtual catalog, NOT in a live database.
type DdlObjectPresence =
    | Unknown = 0
    | Absent = 1
    | Present = 2

/// A typed static requirement check, never permission or live identity proof.
type DdlPreviewPreconditionStatus =
    | Unverified = 0
    | ConsistentWithCallerCatalog = 1
    | ContradictedByCallerCatalog = 2

type DdlDependencyKind =
    | View = 0
    | ForeignKey = 1

[<Sealed>]
type DdlPreviewObject internal (
    presence: DdlObjectPresence,
    columns: IReadOnlyList<SqlCatalogColumn>,
    indexes: IReadOnlyList<SqlCatalogIndex>,
    isConditional: bool) =
    member _.Presence = presence
    member _.Columns = columns
    member _.Indexes = indexes
    /// Expected after values are hypothetical and depend on ALL Host checks,
    /// regardless of whether caller-supplied virtual metadata appears complete.
    member _.IsConditional = isConditional

[<Sealed>]
type DdlDependencyImpact internal (
    kind: DdlDependencyKind,
    objectIdentity: SqlCatalogQualifiedName) =
    member _.Kind = kind
    member _.ObjectIdentity = objectIdentity
    /// The caller-provided Catalog is NOT proof of the complete physical graph.
    member _.IsLiveVerified = false

/// A reviewable, *purely static* projection over G1's closed compiled plan.
/// The virtual catalog cannot assert real server/database/schema/permissions.
[<Sealed>]
type DdlSchemaPreview internal (
    plan: CompiledDdlPlan,
    before: DdlPreviewObject,
    expectedAfter: DdlPreviewObject,
    preconditionStatus: DdlPreviewPreconditionStatus,
    dependencies: IReadOnlyList<DdlDependencyImpact>,
    warnings: IReadOnlyList<string>,
    catalogKnowledge: SqlCatalogKnowledge) =
    member _.Plan = plan
    member _.PlanFingerprint = plan.PlanFingerprint
    member _.Before = before
    member _.ExpectedAfter = expectedAfter
    member _.PreconditionStatus = preconditionStatus
    member _.Dependencies = dependencies
    member _.Warnings = warnings
    member _.CatalogKnowledge = catalogKnowledge
    member _.DependenciesAreLiveVerified = false
    member _.IsLiveVerified = false
    member _.RequiresLiveValidation = true
    member _.RequiresDdlApproval = true
    member _.AuthorizesExecution = false
