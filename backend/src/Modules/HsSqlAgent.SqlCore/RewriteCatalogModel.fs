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

    let knownTableWithConstraints state name columns constraints =
        let copied = columns |> Seq.toArray |> Array.AsReadOnly
        let verifiedConstraints = constraints |> Seq.toArray |> Array.AsReadOnly
        replace state name
            (Some(SqlCatalogTable(name, SqlCatalogKnowledge.Known, copied, verifiedConstraints)))

    /// ALTER never silently discards existing PK, UNIQUE or FK dependencies.
    let knownTable state name columns =
        let existing =
            match lookup state name with
            | Some table -> table.Constraints :> seq<SqlCatalogConstraint>
            | None -> Seq.empty
        knownTableWithConstraints state name columns existing

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
        Seq.append local incoming |> Seq.toList

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
