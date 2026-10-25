namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog

/// Persistent, deterministic virtual catalog. It is never a database snapshot,
/// and unmentioned names in an open-world catalog remain unknown.
module internal RewriteCatalogModel =
    let private sameName left right =
        String.Equals(left, right, StringComparison.Ordinal)

    let lookup (state: SqlCatalogState) name =
        state.Tables |> Seq.tryFind (fun t -> sameName t.Name name)

    let isKnownAbsent (state: SqlCatalogState) name =
        state.IsClosedWorld && (lookup state name).IsNone

    let private replace (state: SqlCatalogState) name (replacement: SqlCatalogTable option) =
        let retained =
            state.Tables |> Seq.filter (fun t -> not (sameName t.Name name)) |> Seq.toArray
        let tables =
            match replacement with
            | Some value -> Array.append retained [| value |]
            | None -> retained
        Array.sortInPlaceWith (fun (left: SqlCatalogTable) (right: SqlCatalogTable) -> StringComparer.Ordinal.Compare(left.Name, right.Name)) tables
        SqlCatalogState(state.IsClosedWorld, Array.AsReadOnly(tables))

    let replaceTable state name columns constraints indexes =
        let cols = columns |> Seq.toArray |> Array.AsReadOnly
        let keys = constraints |> Seq.toArray |> Array.AsReadOnly
        let idxs = indexes |> Seq.toArray |> Array.AsReadOnly
        replace state name (Some(SqlCatalogTable(name, SqlCatalogKnowledge.Known, cols, keys, idxs)))

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
                && sameName constraintInfo.ReferencedTable targetTable)
            |> Seq.map (fun constraintInfo -> table, constraintInfo))

    let columnDependencies (state: SqlCatalogState) tableName columnName =
        let local =
            match lookup state tableName with
            | Some table ->
                table.Constraints
                |> Seq.filter (fun item ->
                    item.Columns |> Seq.exists (sameName columnName))
                |> Seq.map (fun item -> table.Name, item)
            | None -> Seq.empty
        let incoming =
            inboundForeignKeys state tableName
            |> Seq.filter (fun (_, item) ->
                item.ReferencedColumns |> Seq.exists (sameName columnName))
            |> Seq.map (fun (table, item) -> table.Name, item)
        let indexed =
            match lookup state tableName with
            | Some table ->
                table.Indexes
                |> Seq.filter (fun index ->
                    index.Columns |> Seq.exists (sameName columnName))
                |> Seq.map (fun index -> table.Name, index.Name)
            | None -> Seq.empty
            |> Seq.toList
        (Seq.append local incoming |> Seq.map (fun (owner, item) -> owner, string item.Kind))
        |> Seq.append indexed
        |> Seq.toList

    let unknownTable state name =
        replace state name
            (Some(SqlCatalogTable(name, SqlCatalogKnowledge.Unknown,
                                  Array.AsReadOnly(Array.empty<SqlCatalogColumn>))))

    let dropKnown state name = replace state name None

    /// An unmodeled DDL operation may change any object/dependency. All prior
    /// exact table facts must be invalidated, not merely flagged as a warning.
    let taintAll (state: SqlCatalogState) =
        let tables =
            state.Tables
            |> Seq.map (fun t ->
                SqlCatalogTable(t.Name, SqlCatalogKnowledge.Unknown,
                                Array.AsReadOnly(Array.empty<SqlCatalogColumn>)))
            |> Seq.toArray
            |> Array.AsReadOnly
        SqlCatalogState(false, tables)
