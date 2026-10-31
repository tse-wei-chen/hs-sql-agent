namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog

/// Persistent, deterministic virtual catalog. It is never a database snapshot,
/// and unmentioned names in an open-world catalog remain unknown.
module internal RewriteCatalogModel =
    let private sameName left right =
        String.Equals(left, right, StringComparison.Ordinal)

    let private sameIdentity (a: SqlCatalogQualifiedName) (b: SqlCatalogQualifiedName) =
        sameName a.Key b.Key

    /// All table snapshots are maintained in exact ordinal structured-Key
    /// order. Reuse the same lower-bound lookup for reads and immutable
    /// insertion/removal; do not scan every table per Catalog proof.
    let private tablePosition (tables: IReadOnlyList<SqlCatalogTable>) (key: string) =
        let mutable first = 0
        let mutable last = tables.Count
        while first < last do
            RewriteResourceBudget.checkpoint ()
            let middle = first + (last - first) / 2
            if StringComparer.Ordinal.Compare(tables.[middle].QualifiedName.Key, key) < 0 then
                first <- middle + 1
            else
                last <- middle
        first

    let lookup (state: SqlCatalogState) (name: SqlCatalogQualifiedName) =
        let tables = state.Tables
        let key = name.Key
        let index = tablePosition tables key
        if index < tables.Count
           && StringComparer.Ordinal.Equals(tables.[index].QualifiedName.Key, key) then
            Some tables.[index]
        else None

    /// Same exact ordinal structured-key contract as physical Table snapshots.
    /// View replacement is a sorted insertion; no caller can insert an
    /// unsorted View through the public Catalog API.
    let private viewPosition (views: IReadOnlyList<SqlCatalogView>) (key: string) =
        let mutable first = 0
        let mutable last = views.Count
        while first < last do
            RewriteResourceBudget.checkpoint ()
            let middle = first + (last - first) / 2
            if StringComparer.Ordinal.Compare(views.[middle].QualifiedName.Key, key) < 0 then
                first <- middle + 1
            else
                last <- middle
        first

    let lookupView (state: SqlCatalogState) (name: SqlCatalogQualifiedName) =
        let views = state.Views
        let key = name.Key
        let index = viewPosition views key
        if index < views.Count
           && StringComparer.Ordinal.Equals(views.[index].QualifiedName.Key, key) then
            Some views.[index]
        else None

    let dependentViews (state: SqlCatalogState) (targetKeys: Set<string>) =
        // A direct reverse-edge index eliminates repeated full-Catalog scans.
        // The two sorted worklists retain EXACT original fixed-point scan
        // order: when a newly found dependency is at a later View index it
        // becomes reachable in the current pass; earlier indexes wait until
        // the next pass. This preserves publicly observed CASCADE change order.
        let views = state.Views
        let reverse = Dictionary<string, ResizeArray<int>>(StringComparer.Ordinal)
        for i = 0 to views.Count - 1 do
            RewriteResourceBudget.checkpoint ()
            for dependency in views.[i].Dependencies do
                RewriteResourceBudget.checkpoint ()
                match reverse.TryGetValue dependency.Key with
                | true, indices -> indices.Add i
                | false, _ ->
                    let indices = ResizeArray<int>()
                    indices.Add i
                    reverse.Add(dependency.Key, indices)
        let mutable current = SortedSet<int>()
        let mutable following = SortedSet<int>()
        let enqueue (frontier: SortedSet<int>) key =
            match reverse.TryGetValue key with
            | true, dependents ->
                for index in dependents do
                    RewriteResourceBudget.checkpoint ()
                    frontier.Add index |> ignore
            | false, _ -> ()
        for key in targetKeys do enqueue current key
        let discovered = HashSet<int>()
        let ordered = ResizeArray<SqlCatalogView>()
        while current.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let index = current.Min
            current.Remove index |> ignore
            if discovered.Add index then
                let view = views.[index]
                ordered.Add view
                match reverse.TryGetValue view.QualifiedName.Key with
                | true, dependents ->
                    for dependent in dependents do
                        RewriteResourceBudget.checkpoint ()
                        if not (discovered.Contains dependent) then
                            (if dependent > index then current else following).Add dependent
                            |> ignore
                | false, _ -> ()
            if current.Count = 0 && following.Count > 0 then
                current <- following
                following <- SortedSet<int>()
        ordered |> Seq.toList

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

    /// Table snapshots are always sorted by the exact structured Key. Insert,
    /// replace and remove at the ordered position instead of sorting the full
    /// Catalog again after every DDL statement. The original snapshot is never
    /// modified (transaction rollback and per-statement proof rely on this).
    let private replace (state: SqlCatalogState) (name: SqlCatalogQualifiedName) (replacement: SqlCatalogTable option) =
        let tables = state.Tables
        let key = name.Key
        let first = tablePosition tables key
        let found =
            first < tables.Count
            && StringComparer.Ordinal.Equals(tables.[first].QualifiedName.Key, key)
        let size =
            tables.Count +
            (match replacement, found with
             | Some _, false -> 1
             | None, true -> -1
             | _ -> 0)
        let ordered = Array.zeroCreate<SqlCatalogTable> size
        let mutable output = 0
        for i = 0 to tables.Count do
            if i = first then
                match replacement with
                | Some table ->
                    ordered.[output] <- table
                    output <- output + 1
                | None -> ()
            if i < tables.Count && (not found || i <> first) then
                ordered.[output] <- tables.[i]
                output <- output + 1
        SqlCatalogState(state.IsClosedWorld, Array.AsReadOnly(ordered), state.Views)

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
        let current = state.Views
        let position = viewPosition current identity.Key
        let exists = position < current.Count
                     && StringComparer.Ordinal.Equals(
                         current.[position].QualifiedName.Key, identity.Key)
        let next =
            if exists then
                Array.init current.Count (fun i ->
                    RewriteResourceBudget.checkpoint ()
                    if i = position then view else current.[i])
            else
                Array.init (current.Count + 1) (fun i ->
                    RewriteResourceBudget.checkpoint ()
                    if i < position then current.[i]
                    elif i = position then view
                    else current.[i - 1])
        SqlCatalogState(state.IsClosedWorld, state.Tables, Array.AsReadOnly(next))

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

    /// One immutable snapshot for an already authorized batch of View
    /// removals. Proof/RESTRICT/cascade checks run against the ORIGINAL state;
    /// this function only applies that proven set and never authorizes it.
    let dropViews (state: SqlCatalogState) (keys: Set<string>) =
        if Set.isEmpty keys then state
        else
            let next =
                state.Views
                |> Seq.filter (fun existing ->
                    RewriteResourceBudget.checkpoint ()
                    not (Set.contains existing.QualifiedName.Key keys))
                |> Seq.toArray |> Array.AsReadOnly
            SqlCatalogState(state.IsClosedWorld, state.Tables, next)

    let dropView (state: SqlCatalogState) (identity: SqlCatalogQualifiedName) =
        dropViews state (Set.singleton identity.Key)

    let replaceTable state name columns (constraints: seq<SqlCatalogConstraint>) indexes =
        let cols = columns |> Seq.toArray |> Array.AsReadOnly
        let keys = constraints |> Seq.toArray |> Array.AsReadOnly
        let idxs = indexes |> Seq.toArray |> Array.AsReadOnly
        // Immutable uncertainty *names*: neither DROP FK nor adding a new
        // supporting index proves that InnoDB removed its auto index.
        // Empty name is a sentinel for unprovable FK physical identity.
        let prior = lookup state name
        let removedFkNames =
            match prior with
            | None -> Seq.empty
            | Some old ->
                old.Constraints
                |> Seq.filter (fun c ->
                    c.Kind = SqlCatalogConstraintKind.ForeignKey
                    && not (keys |> Seq.exists (fun next ->
                        next.Kind = SqlCatalogConstraintKind.ForeignKey
                        && sameName next.Name c.Name)))
                |> Seq.map (fun c -> if isNull c.Name then "" else c.Name)
        let possibleNames =
            seq {
                match prior with
                | Some old -> yield! old.PossibleFkIndexNames
                | None -> ()
                yield! removedFkNames
            }
            |> Seq.distinct
            |> Seq.toArray |> Array.AsReadOnly
        let residual =
            (prior |> Option.exists (fun old -> old.MayRetainUnmodeledFkIndexes))
            || possibleNames.Count > 0
        replace state name (Some(SqlCatalogTable(name.DisplayName,
            SqlCatalogKnowledge.Known, cols, keys, idxs, name, residual,
            possibleNames)))

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
                        Array.AsReadOnly(constraints), indexes,
                        table.QualifiedName, table.MayRetainUnmodeledFkIndexes,
                        table.PossibleFkIndexNames)
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
                    let droppedNames =
                        table.Constraints
                        |> Seq.filter (fun old ->
                            old.Kind = SqlCatalogConstraintKind.ForeignKey
                            && not (remaining |> Seq.exists (fun current ->
                                current.Kind = SqlCatalogConstraintKind.ForeignKey
                                && sameName old.Name current.Name)))
                        |> Seq.map (fun c ->
                            if isNull c.Name then "" else c.Name)
                    let possible =
                        Seq.append table.PossibleFkIndexNames droppedNames
                        |> Seq.distinct
                        |> Seq.toArray |> Array.AsReadOnly
                    SqlCatalogTable(
                        table.Name, table.Knowledge, table.Columns,
                        remaining, table.Indexes, table.QualifiedName,
                        table.MayRetainUnmodeledFkIndexes || possible.Count > 0,
                        possible))
            |> Seq.toArray |> Array.AsReadOnly
        SqlCatalogState(state.IsClosedWorld, nextTables, state.Views)

    let withIndexes state name indexes =
        match lookup state name with
        | Some table -> replaceTable state name table.Columns table.Constraints indexes
        | None -> invalidArg "name" "Table not found."

    /// A newly declared left-prefix covering index can silently replace an
    /// InnoDB implicit support index. Publish the proven standalone index
    /// while retaining the old physical name as UNKNOWN, not as a DROP.
    /// This immutable marker survives later constraints/index updates.
    let withIndexesAndUnprovenFkReplacement state name indexes =
        let updated = withIndexes state name indexes
        match lookup updated name with
        | Some table ->
            let newIndex = table.Indexes |> Seq.last
            let newMembers = newIndex.Columns |> Seq.toList
            let coveredNames =
                table.Constraints
                |> Seq.filter (fun fk ->
                    let key = fk.Columns |> Seq.toList
                    fk.Kind = SqlCatalogConstraintKind.ForeignKey
                    && key.Length <= newMembers.Length
                    && List.forall2
                        (fun a b -> String.Equals(a, b, StringComparison.Ordinal))
                        key (newMembers |> List.take key.Length))
                |> Seq.map (fun fk -> if isNull fk.Name then "" else fk.Name)
            let possible =
                Seq.append table.PossibleFkIndexNames coveredNames
                |> Seq.distinct
                |> Seq.toArray |> Array.AsReadOnly
            replace updated name
                (Some(SqlCatalogTable(
                    table.Name, table.Knowledge, table.Columns, table.Constraints,
                    table.Indexes, table.QualifiedName, true, possible)))
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
