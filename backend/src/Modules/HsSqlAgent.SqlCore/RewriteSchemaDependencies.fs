namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums

/// Proven relational dependencies of a known, closed-world Virtual Catalog.
/// This is a static safety model, NOT an authorization or execution plan.
/// Indexes/CHECK/triggers not represented here must taint the Catalog at parse.
module internal RewriteSchemaDependencies =

    type DependencyProof =
        | Proven
        | Invalid of string
        | Unproven of string

    let private equals left right =
        String.Equals(left, right, StringComparison.Ordinal)

    let private complete (state: SqlCatalogState) =
        state.IsClosedWorld &&
        (state.Tables |> Seq.forall (fun t -> t.Knowledge = SqlCatalogKnowledge.Known))

    let validateCreate dialect (state: SqlCatalogState) name
                       (columns: SqlCatalogColumn list)
                       (constraints: SqlCatalogConstraint list) =
        if not (complete state) then
            Unproven "Existing Catalog is incomplete; referenced key dependencies cannot be proven."
        elif constraints |> List.exists (fun d ->
            d.Kind = SqlCatalogConstraintKind.ForeignKey)
             && (dialect = SqlAgentToolType.MySQL || dialect = SqlAgentToolType.MsSqlServer) then
            Unproven "Foreign-key table resolution needs a proven provider identifier case/collation contract."
        else
            let mutable error: DependencyProof option = None
            let reject message =
                if error.IsNone then error <- Some(Invalid message)
            let named = constraints |> List.choose (fun d ->
                if String.IsNullOrEmpty(d.Name) then None else Some d.Name)
            if (named |> List.distinct).Length <> named.Length then
                reject "CREATE TABLE contains duplicate constraint names."
            if constraints |> List.filter (fun d ->
                d.Kind = SqlCatalogConstraintKind.PrimaryKey) |> List.length > 1 then
                reject "CREATE TABLE can define only one PRIMARY KEY."
            let columnLookup memberName =
                columns |> List.tryFind (fun c -> equals c.Name memberName)
            for item in constraints do
                RewriteResourceBudget.checkpoint ()
                let local = item.Columns |> Seq.toList
                if local.IsEmpty || List.distinct local <> local then
                    reject "Constraint has an empty or duplicate column list."
                for memberName in local do
                    RewriteResourceBudget.checkpoint ()
                    if columnLookup memberName |> Option.isNone then
                        reject ("Constraint references missing local column '" + memberName + "'.")
                if item.Kind = SqlCatalogConstraintKind.PrimaryKey
                   && dialect = SqlAgentToolType.Firebird then
                    for col in local do
                        match columnLookup col with
                        | Some value when value.Nullable ->
                            reject ("Firebird PRIMARY KEY column '" + col +
                                    "' must declare NOT NULL.")
                        | _ -> ()
                if item.Kind = SqlCatalogConstraintKind.ForeignKey then
                    let remote = item.ReferencedColumns |> Seq.toList
                    if String.IsNullOrWhiteSpace item.ReferencedTable
                       || remote.Length <> local.Length || remote.IsEmpty
                       || List.distinct remote <> remote then
                        reject "Foreign-key column counts, target and uniqueness must be explicitly provable."
                    else
                        let parent =
                            if equals item.ReferencedTable name then
                                Some(columns, constraints)
                            else
                                match RewriteCatalogModel.lookup state item.ReferencedTable with
                                | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                                    Some(table.Columns |> Seq.toList,
                                         table.Constraints |> Seq.toList)
                                | _ -> None
                        match parent with
                        | None ->
                            reject ("Foreign key references missing known table '" +
                                    item.ReferencedTable + "'.")
                        | Some(parentColumns, parentConstraints) ->
                            let uniqueTarget =
                                parentConstraints |> List.exists (fun key ->
                                    (key.Kind = SqlCatalogConstraintKind.PrimaryKey
                                     || key.Kind = SqlCatalogConstraintKind.Unique)
                                    && (key.Columns |> Seq.toList) = remote)
                            if not uniqueTarget then
                                reject "Foreign key target is not a proven PRIMARY KEY or UNIQUE key."
                            for (localName, remoteName) in List.zip local remote do
                                RewriteResourceBudget.checkpoint ()
                                let sourceType = columnLookup localName
                                let targetType =
                                    parentColumns |> List.tryFind (fun c -> equals c.Name remoteName)
                                match sourceType, targetType with
                                | Some source, Some target when equals source.TypeName target.TypeName -> ()
                                | Some _, Some _ ->
                                    reject "Foreign-key referenced and referencing column types are not identical."
                                | _ ->
                                    reject "Foreign key references a missing local/remote column."
            defaultArg error Proven

    let canDropTable (state: SqlCatalogState) name =
        if not (complete state) then
            Unproven "Cannot prove absence of dependent tables in an incomplete Catalog."
        else
            let inbound =
                RewriteCatalogModel.inboundForeignKeys state name
                |> Seq.tryFind (fun (table, _) -> not (equals table.Name name))
            match inbound with
            | Some(table, _) ->
                Invalid ("DROP TABLE " + name +
                         " is blocked by a FOREIGN KEY in table '" + table.Name + "'.")
            | None -> Proven

    let canDropColumn (state: SqlCatalogState) tableName columnName =
        if not (complete state) then
            Unproven "Cannot prove absence of column constraints in an incomplete Catalog."
        else
            match RewriteCatalogModel.columnDependencies state tableName columnName with
            | (owner, dependency)::_ ->
                Invalid ("DROP COLUMN " + columnName +
                         " is blocked by " + string dependency.Kind +
                         " dependency in table '" + owner + "'.")
            | [] -> Proven
