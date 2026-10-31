namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Ddl
open HsSqlAgent.SqlCore.Enums

/// Read-only G2 projection: consume ONLY the typed G1 plan plus a caller-supplied
/// Virtual Catalog. Never query DB, replay DDL, infer physical identity, or
/// turn Catalog.Known into Host permission/approval evidence.
module internal RewriteDdlPreview =

    let private readonly (items: seq<'T>) : IReadOnlyList<'T> =
        Array.AsReadOnly(Seq.toArray items) :> IReadOnlyList<'T>

    let private emptyColumns : IReadOnlyList<SqlCatalogColumn> =
        readonly Seq.empty
    let private emptyIndexes : IReadOnlyList<SqlCatalogIndex> =
        readonly Seq.empty

    let private projected presence columns indexes =
        DdlPreviewObject(presence, columns, indexes, true)

    let private beforeObject (table: SqlCatalogTable option) presence =
        match table with
        | Some item ->
            DdlPreviewObject(presence, readonly item.Columns, readonly item.Indexes, false)
        | None -> DdlPreviewObject(presence, emptyColumns, emptyIndexes, false)

    let private columnExists (table: SqlCatalogTable) name =
        table.Columns |> Seq.exists (fun c -> String.Equals(c.Name, name, StringComparison.Ordinal))

    let private indexExists (table: SqlCatalogTable) name =
        table.Indexes |> Seq.exists (fun i -> String.Equals(i.Name, name, StringComparison.Ordinal))

    let private dependencyImpact (catalog: SqlCatalogState) (identity: SqlCatalogQualifiedName) =
        // Real physical FK/View metadata is unknown even if the virtual Catalog
        // contains explicit known dependencies. Never invent omitted objects.
        seq {
            let key = Set.singleton identity.Key
            for view in RewriteCatalogModel.dependentViews catalog key do
                RewriteResourceBudget.checkpoint()
                yield DdlDependencyImpact(DdlDependencyKind.View, view.QualifiedName)
            for table in catalog.Tables do
                RewriteResourceBudget.checkpoint()
                for fk in table.Constraints do
                    RewriteResourceBudget.checkpoint()
                    if fk.Kind = SqlCatalogConstraintKind.ForeignKey
                       && not (isNull fk.ReferencedIdentity)
                       && fk.ReferencedIdentity.Key = identity.Key then
                        yield DdlDependencyImpact(DdlDependencyKind.ForeignKey, table.QualifiedName)
        }
        |> Seq.distinctBy (fun impact -> impact.Kind, impact.ObjectIdentity.Key)
        |> readonly

    let project (plan: CompiledDdlPlan) (catalog: SqlCatalogState) =
        ArgumentNullException.ThrowIfNull(plan)
        ArgumentNullException.ThrowIfNull(catalog)
        if plan.Changes.Count <> 1 then
            invalidOp "Only one closed DDL schema change can be previewed."
        let change = plan.Changes.[0]
        let identity = change.ObjectIdentity
        let table = RewriteCatalogModel.lookup catalog identity
        let view = RewriteCatalogModel.lookupView catalog identity
        // An unqualified identity is NOT equivalent to a same-final-component
        // schema-qualified name when search_path/default schema is unverified.
        let ambiguousScope =
            RewriteCatalogModel.hasUnqualifiedScopeConflict catalog identity
        let knownAbsent =
            catalog.Knowledge = SqlCatalogKnowledge.Known
            && not ambiguousScope
            && table.IsNone && view.IsNone
        let beforePresence =
            match table with
            | Some _ -> DdlObjectPresence.Present
            | None when view.IsSome -> DdlObjectPresence.Present
            | None when knownAbsent -> DdlObjectPresence.Absent
            | _ -> DdlObjectPresence.Unknown
        let before = beforeObject table beforePresence
        let trustedTable =
            match table with
            | Some item when item.Knowledge = SqlCatalogKnowledge.Known -> Some item
            | _ -> None

        let status, after =
            match change.Operation with
            | DdlSchemaOperation.CreateTable ->
                match beforePresence with
                | DdlObjectPresence.Present ->
                    // The precondition is already false *within the supplied
                    // static Catalog*. Do not claim CREATE would replace it.
                    DdlPreviewPreconditionStatus.ContradictedByCallerCatalog,
                    projected beforePresence before.Columns before.Indexes
                | _ ->
                    let status =
                        if knownAbsent then DdlPreviewPreconditionStatus.ConsistentWithCallerCatalog
                        else DdlPreviewPreconditionStatus.Unverified
                    status, projected DdlObjectPresence.Present
                        (readonly change.ColumnDefinitions) emptyIndexes

            | DdlSchemaOperation.AddNullableColumn ->
                match trustedTable with
                | Some item ->
                    let definition = change.ColumnDefinitions.[0]
                    if columnExists item definition.Name then
                        DdlPreviewPreconditionStatus.ContradictedByCallerCatalog,
                        projected DdlObjectPresence.Present before.Columns before.Indexes
                    else
                        DdlPreviewPreconditionStatus.ConsistentWithCallerCatalog,
                        projected DdlObjectPresence.Present
                            (readonly (Seq.append item.Columns [definition]))
                            (readonly item.Indexes)
                | None ->
                    let status =
                        if beforePresence = DdlObjectPresence.Absent || view.IsSome then
                            DdlPreviewPreconditionStatus.ContradictedByCallerCatalog
                        else DdlPreviewPreconditionStatus.Unverified
                    status, projected DdlObjectPresence.Unknown emptyColumns emptyIndexes

            | DdlSchemaOperation.CreateIndex ->
                match trustedTable with
                | Some item ->
                    let missingColumn =
                        change.Columns |> Seq.exists (fun name -> not (columnExists item name))
                    let localCollision = indexExists item change.IndexName
                    if missingColumn || localCollision then
                        DdlPreviewPreconditionStatus.ContradictedByCallerCatalog,
                        projected DdlObjectPresence.Present before.Columns before.Indexes
                    else
                        // A local Catalog index list does not prove namespace-wide
                        // absence of index names (Postgres/Oracle/SQL Server etc).
                        // May model the hypothetical delta, but never "satisfied".
                        let index =
                            SqlCatalogIndex(change.IndexName, readonly change.Columns)
                        DdlPreviewPreconditionStatus.Unverified,
                        projected DdlObjectPresence.Present
                            (readonly item.Columns)
                            (readonly (Seq.append item.Indexes [index]))
                | None ->
                    let status =
                        if beforePresence = DdlObjectPresence.Absent || view.IsSome then
                            DdlPreviewPreconditionStatus.ContradictedByCallerCatalog
                        else DdlPreviewPreconditionStatus.Unverified
                    status, projected DdlObjectPresence.Unknown emptyColumns emptyIndexes
            | _ -> invalidOp "Unrecognized closed DDL schema operation."

        let deps = dependencyImpact catalog identity
        let warnings = ResizeArray<string>()
        warnings.Add("This is a caller-declared virtual Catalog, not a live database/schema identity or freshness proof.")
        warnings.Add("ExpectedAfter is hypothetical; DDL execution requires live metadata, independent DDL permission and approval.")
        warnings.Add("Physical dependency graph completeness, affected rows and lock duration are unknown.")
        if plan.Impact.MayLockSchema then
            warnings.Add("The DDL may acquire schema/metadata locks; the lock duration is unknown.")
        if plan.TransactionBehavior = DdlTransactionBehavior.ImplicitCommit then
            warnings.Add("Provider DDL can implicitly commit; Preview never executes or rolls back the statement.")
        if ambiguousScope then
            warnings.Add("Unqualified object identity may resolve differently under the live session/schema search path.")
        if change.Operation = DdlSchemaOperation.CreateIndex then
            warnings.Add("Index names may occupy a wider physical namespace than the modeled table indexes.")
        match status with
        | DdlPreviewPreconditionStatus.ContradictedByCallerCatalog ->
            warnings.Add("A required DDL precondition is contradicted by the caller-provided Catalog.")
        | DdlPreviewPreconditionStatus.Unverified ->
            warnings.Add("One or more DDL preconditions are unresolved in the caller-provided Catalog.")
        | _ -> ()
        DdlSchemaPreview(plan, before, after, status, deps, readonly warnings, catalog.Knowledge)
