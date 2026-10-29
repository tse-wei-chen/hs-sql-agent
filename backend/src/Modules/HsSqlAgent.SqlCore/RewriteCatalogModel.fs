namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog

/// Persistent, deterministic virtual catalog. It is never a database snapshot,
/// and unmentioned names in an open-world catalog remain unknown.
module internal RewriteCatalogModel =
    let private sameName left right =
        String.Equals(left, right, StringComparison.Ordinal)

    let private sameIdentity (a: SqlCatalogQualifiedName) (b: SqlCatalogQualifiedName) =
        sameName a.Key b.Key

    let lookup (state: SqlCatalogState) (name: SqlCatalogQualifiedName) =
        state.Tables |> Seq.tryFind (fun t -> sameIdentity t.QualifiedName name)

    let lookupView (state: SqlCatalogState) (name: SqlCatalogQualifiedName) =
        state.Views |> Seq.tryFind (fun view ->
            sameIdentity view.QualifiedName name)

    let dependentViews (state: SqlCatalogState) (targetKeys: Set<string>) =
        // A least fixed-point closure over direct structured identities.
        // Dependency cycles cannot be created through the supported
        // CREATE VIEW grammar, but the closure remains cycle-safe.
        let mutable known = targetKeys
        let mutable discovered = Set.empty<string>
        let discoveredInOrder = ResizeArray<SqlCatalogView>()
        let mutable changed = true
        while changed do
            RewriteResourceBudget.checkpoint ()
            changed <- false
            for view in state.Views do
                RewriteResourceBudget.checkpoint ()
                if not (Set.contains view.QualifiedName.Key discovered)
                   && (view.Dependencies |> Seq.exists (fun dependency ->
                       Set.contains dependency.Key known)) then
                    discovered <- Set.add view.QualifiedName.Key discovered
                    discoveredInOrder.Add(view)
                    known <- Set.add view.QualifiedName.Key known
                    changed <- true
        discoveredInOrder |> Seq.toList

    /// An unqualified name may resolve into an explicitly qualified table
    /// through a session search_path/default schema not modeled by Catalog.
    /// Absence is not provable just by comparing the visible last component.
    let hasUnqualifiedScopeConflict (state: SqlCatalogState) (name: SqlCatalogQualifiedName) =
        name.Parts.Count = 1
        && (state.Tables |> Seq.exists (fun table ->
            table.QualifiedName.IsQualified
            && String.Equals(
                table.QualifiedName.Parts[table.QualifiedName.Parts.Count - 1],
                name.Parts[0], StringComparison.Ordinal)))

    let isKnownAbsent (state: SqlCatalogState) name =
        state.IsClosedWorld && (lookup state name).IsNone

    let private replace (state: SqlCatalogState) (name: SqlCatalogQualifiedName) (replacement: SqlCatalogTable option) =
        let retained =
            state.Tables |> Seq.filter (fun t -> not (sameIdentity t.QualifiedName name)) |> Seq.toArray
        let tables =
            match replacement with
            | Some value -> Array.append retained [| value |]
            | None -> retained
        Array.sortInPlaceWith (fun (left: SqlCatalogTable) (right: SqlCatalogTable) ->
            StringComparer.Ordinal.Compare(left.QualifiedName.Key, right.QualifiedName.Key)) tables
        SqlCatalogState(state.IsClosedWorld, Array.AsReadOnly(tables), state.Views)

    let withViewSources (state: SqlCatalogState)
                        (identity: SqlCatalogQualifiedName)
                        (columns: seq<SqlCatalogColumn>)
                        (bindings: seq<SqlCatalogViewSourceBinding>) =
        let exact = bindings |> Seq.toArray
        let dependencies =
            exact |> Seq.map (fun b -> b.Relation) |> Seq.toArray
        let flattened =
            exact |> Seq.collect (fun b -> b.Columns) |> Seq.toArray
        let view =
            SqlCatalogView(identity.DisplayName, identity,
                           columns |> Seq.toArray |> Array.AsReadOnly,
                           dependencies |> Array.AsReadOnly,
                           flattened |> Array.AsReadOnly,
                           exact |> Array.AsReadOnly)
        let next =
            state.Views |> Seq.filter (fun existing ->
                not (sameIdentity existing.QualifiedName identity))
            |> Seq.append [view]
            |> Seq.sortBy (fun item -> item.QualifiedName.Key)
            |> Seq.toArray |> Array.AsReadOnly
        SqlCatalogState(state.IsClosedWorld, state.Tables, next)

    /// Preserve existing direct-source usage without distributing flat
    /// column names over unrelated relations.
    let withView (state: SqlCatalogState) (identity: SqlCatalogQualifiedName)
                 (columns: seq<SqlCatalogColumn>)
                 (dependencies: seq<SqlCatalogQualifiedName>)
                 (sourceColumns: seq<string>) =
        let physicalNames = sourceColumns |> Seq.toArray |> Array.AsReadOnly
        let inputs =
            dependencies |> Seq.map (fun relation ->
                SqlCatalogViewSourceBinding(relation, physicalNames))
        withViewSources state identity columns inputs

    let dropView (state: SqlCatalogState) (identity: SqlCatalogQualifiedName) =
        let next =
            state.Views |> Seq.filter (fun existing ->
                not (sameIdentity existing.QualifiedName identity))
            |> Seq.toArray |> Array.AsReadOnly
        SqlCatalogState(state.IsClosedWorld, state.Tables, next)

    let replaceTable state name columns constraints indexes =
        let cols = columns |> Seq.toArray |> Array.AsReadOnly
        let keys = constraints |> Seq.toArray |> Array.AsReadOnly
        let idxs = indexes |> Seq.toArray |> Array.AsReadOnly
        replace state name (Some(SqlCatalogTable(name.DisplayName, SqlCatalogKnowledge.Known, cols, keys, idxs, name)))

    /// Atomically publish a PostgreSQL column rename across modeled physical
    /// tables, inbound/self-referencing FKs and directly bound View sources.
    /// Existing View output headers never change on physical column rename.
    /// All replacements derive from ONE pre-rename immutable snapshot.
    /// Caller must prove a complete known Catalog and the target column.
    let renameColumnWithProvenDependents
            (state: SqlCatalogState) (name: SqlCatalogQualifiedName)
            (oldName: string) (newName: string) =
        let renamed value = if sameName value oldName then newName else value
        let renameColumns (values: System.Collections.Generic.IReadOnlyList<string>)
                : System.Collections.Generic.IReadOnlyList<string> =
            values |> Seq.map renamed |> Seq.toArray |> Array.AsReadOnly
            :> System.Collections.Generic.IReadOnlyList<string>
        let result =
            state.Tables
            |> Seq.map (fun table ->
                RewriteResourceBudget.checkpoint ()
                let target = sameIdentity table.QualifiedName name
                let columns : System.Collections.Generic.IReadOnlyList<SqlCatalogColumn> =
                    if target then
                        table.Columns
                        |> Seq.map (fun col ->
                            SqlCatalogColumn(renamed col.Name, col.TypeName, col.Nullable))
                        |> Seq.toArray |> Array.AsReadOnly
                        :> System.Collections.Generic.IReadOnlyList<SqlCatalogColumn>
                    else table.Columns
                let indexes : System.Collections.Generic.IReadOnlyList<SqlCatalogIndex> =
                    if target then
                        table.Indexes
                        |> Seq.map (fun index ->
                            SqlCatalogIndex(index.Name, renameColumns index.Columns))
                        |> Seq.toArray |> Array.AsReadOnly
                        :> System.Collections.Generic.IReadOnlyList<SqlCatalogIndex>
                    else table.Indexes
                let mutable changedConstraints = false
                let constraints =
                    table.Constraints
                    |> Seq.map (fun c ->
                        RewriteResourceBudget.checkpoint ()
                        // A self-FK is both local and inbound. Preserve its
                        // physical target identity and constraint name.
                        let localChange =
                            target && (c.Columns |> Seq.exists (sameName oldName))
                        let inboundChange =
                            c.Kind = SqlCatalogConstraintKind.ForeignKey
                            && not (isNull c.ReferencedIdentity)
                            && sameIdentity c.ReferencedIdentity name
                            && (c.ReferencedColumns |> Seq.exists (sameName oldName))
                        if localChange || inboundChange then
                            changedConstraints <- true
                            SqlCatalogConstraint(
                                c.Name, c.Kind,
                                (if localChange then renameColumns c.Columns else c.Columns),
                                c.ReferencedIdentity,
                                (if inboundChange then
                                     renameColumns c.ReferencedColumns
                                 else c.ReferencedColumns))
                        else c)
                    |> Seq.toArray
                if target || changedConstraints then
                    SqlCatalogTable(
                        table.Name, table.Knowledge, columns,
                        Array.AsReadOnly(constraints), indexes, table.QualifiedName)
                else table)
            |> Seq.toArray |> Array.AsReadOnly
        let rewrittenViews =
            state.Views
            |> Seq.map (fun (view: SqlCatalogView) ->
                RewriteResourceBudget.checkpoint ()
                let mutable changed = false
                let bindings =
                    view.SourceBindings
                    |> Seq.map (fun (binding: SqlCatalogViewSourceBinding) ->
                        RewriteResourceBudget.checkpoint ()
                        // Only direct physical inputs of the renamed relation
                        // track the new attribute name. A downstream View uses
                        // the upstream View's stable SQL-facing output header.
                        let owned =
                            sameIdentity binding.Relation name
                            && (binding.Columns |> Seq.exists (sameName oldName))
                        if owned then
                            changed <- true
                            SqlCatalogViewSourceBinding(
                                binding.Relation, renameColumns binding.Columns)
                        else binding)
                    |> Seq.toArray
                if changed then
                    let flattened =
                        bindings
                        |> Seq.collect (fun binding -> binding.Columns)
                        |> Seq.toArray |> Array.AsReadOnly
                    SqlCatalogView(
                        view.Name, view.QualifiedName, view.Columns,
                        view.Dependencies, flattened,
                        Array.AsReadOnly(bindings))
                else view)
            |> Seq.toArray |> Array.AsReadOnly
        SqlCatalogState(state.IsClosedWorld, result, rewrittenViews)

    let knownTableWithConstraints state name columns constraints =
        replaceTable state name columns constraints Seq.empty

    /// Normal column ALTER keeps every established key and non-unique index.
    let knownTable state name columns =
        match lookup state name with
        | Some existing ->
            replaceTable state name columns existing.Constraints existing.Indexes
        | None ->
            replaceTable state name columns Seq.empty Seq.empty

    let withConstraints state name constraints =
        match lookup state name with
        | Some table -> replaceTable state name table.Columns constraints table.Indexes
        | None -> invalidArg "name" "Table not found."

    /// Replace one proven named constraint in an immutable table snapshot.
    /// Remote FK columns/relation identities are physical key bindings, not
    /// links to the old constraint's displayed name: retain them unchanged.
    let renameKnownConstraint
            (state: SqlCatalogState) (name: SqlCatalogQualifiedName)
            (oldName: string) (newName: string) =
        match lookup state name with
        | Some owner ->
            let next =
                owner.Constraints
                |> Seq.map (fun c ->
                    RewriteResourceBudget.checkpoint ()
                    if sameName c.Name oldName then
                        SqlCatalogConstraint(
                            newName, c.Kind, c.Columns,
                            c.ReferencedIdentity, c.ReferencedColumns)
                    else c)
                |> Seq.toArray |> Array.AsReadOnly
            replaceTable state name owner.Columns next owner.Indexes
        | None -> invalidArg "name" "Constraint owner was not proven."

    /// PostgreSQL DROP CONSTRAINT CASCADE: remove the target key and only
    /// provably dependent inbound/self foreign keys in one immutable state.
    /// The proof planner resolves every (owner, constraint name) against the
    /// original Catalog before calling this operation.
    let dropProvenConstraintsAtomically
            (state: SqlCatalogState)
            (targets: (SqlCatalogQualifiedName * string) list) =
        let nextTables =
            state.Tables
            |> Seq.map (fun (table: SqlCatalogTable) ->
                RewriteResourceBudget.checkpoint ()
                let dropped =
                    targets
                    |> List.choose (fun (owner, constraintName) ->
                        if sameIdentity owner table.QualifiedName then
                            Some constraintName
                        else None)
                if dropped.IsEmpty then table
                else
                    let remaining =
                        table.Constraints
                        |> Seq.filter (fun c ->
                            not (dropped |> List.exists (fun name ->
                                sameName name c.Name)))
                        |> Seq.toArray |> Array.AsReadOnly
                    SqlCatalogTable(
                        table.Name, table.Knowledge, table.Columns,
                        remaining, table.Indexes, table.QualifiedName))
            |> Seq.toArray |> Array.AsReadOnly
        SqlCatalogState(state.IsClosedWorld, nextTables, state.Views)

    let withIndexes state name indexes =
        match lookup state name with
        | Some table -> replaceTable state name table.Columns table.Constraints indexes
        | None -> invalidArg "name" "Table not found."

    let inboundForeignKeys (state: SqlCatalogState) targetTable =
        state.Tables
        |> Seq.collect (fun table ->
            table.Constraints
            |> Seq.filter (fun constraintInfo ->
                constraintInfo.Kind = SqlCatalogConstraintKind.ForeignKey
                && not (isNull constraintInfo.ReferencedIdentity)
                && sameIdentity constraintInfo.ReferencedIdentity targetTable)
            |> Seq.map (fun constraintInfo -> table, constraintInfo))

    let columnDependencies (state: SqlCatalogState) tableName columnName =
        let local =
            match lookup state tableName with
            | Some table ->
                table.Constraints
                |> Seq.filter (fun item ->
                    item.Columns |> Seq.exists (sameName columnName))
                |> Seq.map (fun item -> table.QualifiedName, item)
            | None -> Seq.empty
        let incoming =
            inboundForeignKeys state tableName
            |> Seq.filter (fun (_, item) ->
                item.ReferencedColumns |> Seq.exists (sameName columnName))
            |> Seq.map (fun (table, item) -> table.QualifiedName, item)
        let indexed =
            match lookup state tableName with
            | Some table ->
                table.Indexes
                |> Seq.filter (fun index ->
                    index.Columns |> Seq.exists (sameName columnName))
                |> Seq.map (fun index -> table.QualifiedName, index.Name)
            | None -> Seq.empty
        let viewDependencies =
            state.Views
            |> Seq.filter (fun view ->
                view.SourceBindings |> Seq.exists (fun binding ->
                    sameIdentity binding.Relation tableName
                    && (binding.Columns |> Seq.exists (fun sourceColumn ->
                        sameName sourceColumn columnName))))
            |> Seq.map (fun view -> view.QualifiedName, "View")
        Seq.concat [
            indexed
            (Seq.append local incoming |> Seq.map (fun (owner, item) -> owner, string item.Kind))
            viewDependencies
        ] |> Seq.toList

    let unknownTable state name =
        replace state name
            (Some(SqlCatalogTable(name.DisplayName, SqlCatalogKnowledge.Unknown,
                                  Array.AsReadOnly(Array.empty<SqlCatalogColumn>),
                                  Array.AsReadOnly(Array.empty<SqlCatalogConstraint>),
                                  Array.AsReadOnly(Array.empty<SqlCatalogIndex>),
                                  name)))

    let dropKnown state name = replace state name None

    /// An unmodeled DDL operation may change any object/dependency. All prior
    /// exact table facts must be invalidated, not merely flagged as a warning.
    let taintAll (state: SqlCatalogState) =
        let tables =
            state.Tables
            |> Seq.map (fun t ->
                SqlCatalogTable(t.Name, SqlCatalogKnowledge.Unknown,
                                Array.AsReadOnly(Array.empty<SqlCatalogColumn>),
                                Array.AsReadOnly(Array.empty<SqlCatalogConstraint>),
                                Array.AsReadOnly(Array.empty<SqlCatalogIndex>),
                                t.QualifiedName))
            |> Seq.toArray
            |> Array.AsReadOnly
        // Opaque schema changes also invalidate all previous View proofs:
        // never leave known View dependencies alongside an unknown Catalog.
        SqlCatalogState(false, tables,
                        Array.AsReadOnly(Array.empty<SqlCatalogView>))
