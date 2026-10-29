namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

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

    /// Closed algebraic identities of MySQL physical index names. A PRIMARY
    /// constraint owns the physical index PRIMARY regardless of its optional
    /// SQL constraint symbol. Named UNIQUE constraints own a physical index.
    /// An explicitly named FK may prove one *implicit* supporting index
    /// only while no other key could cover the same FK-left-prefix. This is
    /// not a general physical-index history or executable schema plan:
    /// overlapping/replacement and post-drop states remain unproven.
    type private MySqlPhysicalIndexName =
        | PrimaryIndex
        | NamedUniqueIndex of string
        | NamedForeignKeySupportingIndex of string
        | StandaloneIndex of string

    let private physicalMySqlIndexName = function
        | PrimaryIndex -> "PRIMARY"
        | NamedUniqueIndex name
        | NamedForeignKeySupportingIndex name
        | StandaloneIndex name -> name

    /// The logical explicit-index transition is proven while the physical
    /// auto-FK index may survive OR be dropped by InnoDB. These are not
    /// interchangeable states for later name-absence / DROP INDEX checks.
    type MySqlPhysicalIndexTransition =
        | StableImplicitFkSupport
        | MayReplaceImplicitFkSupport

    let canCreateMySqlSecondaryIndex
            (profile: SqlProviderCapabilityProfile | null)
            (state: SqlCatalogState)
            (owner: SqlCatalogTable)
            (proposedName: string)
            (members: string list) =
        let same (a: string) (b: string) =
            String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        let asciiName (value: string) =
            RewriteCatalogIdentifierProof.isSimpleAsciiIdentifier value
            && value.Length <= 64
        if not (complete state) then
            Error(Unproven "MySQL index namespace requires a complete known Catalog.")
        elif not (asciiName proposedName)
             || (owner.Indexes |> Seq.exists (fun idx -> not (asciiName idx.Name))) then
            Error(Unproven "MySQL index names need bounded simple ASCII identity proof.")
        elif same proposedName "PRIMARY" then
            Error(Invalid "The physical MySQL PRIMARY index name is reserved.")
        elif owner.MayRetainUnmodeledFkIndexes then
            Error(Unproven "A previously dropped/replaced MySQL FK index may survive: physical namespace absence is not proven.")
        elif owner.Constraints.Count = 0 then
            // Existing standalone-index collisions are checked by the
            // caller using MySQL's case-insensitive index-name equality.
            Ok StableImplicitFkSupport
        else
            let namesKnown =
                RewriteCatalogIdentifierProof.canProvePhysicalNames
                    SqlAgentToolType.MySQL profile state owner.QualifiedName.DisplayName
                || RewriteCatalogIdentifierProof.canProveMySqlLowercaseZeroCatalog
                    profile state owner.QualifiedName
                    (owner.Columns |> Seq.toList)
                    (owner.Constraints |> Seq.toList)
            if not namesKnown then
                Error(Unproven "MySQL constraint-backed indexes require declared proven identifier semantics.")
            elif owner.Constraints |> Seq.exists (fun c ->
                     c.Kind = SqlCatalogConstraintKind.Unique
                     && String.IsNullOrWhiteSpace c.Name) then
                Error(Unproven "MySQL generated UNIQUE index names are not represented by the Catalog.")
            else
                // A covering index can replace the single tracked implicit
                // FK-support index. We can prove the explicit index and FK
                // semantics, but MUST retain uncertainty about the physical
                // implicit index, whose removal is engine-dependent.
                let foreignKeys =
                    owner.Constraints
                    |> Seq.filter (fun c ->
                        c.Kind = SqlCatalogConstraintKind.ForeignKey)
                    |> Seq.toList
                let startsWith (prefix: string list) (candidate: string list) =
                    prefix.Length <= candidate.Length
                    && List.forall2 (fun left right ->
                        String.Equals(left, right, StringComparison.Ordinal))
                            prefix (candidate |> List.take prefix.Length)
                let supportProof =
                    match foreignKeys with
                    | [] -> Ok StableImplicitFkSupport
                    | [fk] ->
                        let cols = fk.Columns |> Seq.toList
                        let implicitName = fk.Name
                        if String.IsNullOrWhiteSpace implicitName
                           || not (asciiName implicitName) then
                            Error(Unproven "Unnamed or non-ASCII FK support indexes have no physical name proof.")
                        elif startsWith cols members then
                            // Keep checking possible competing parent/local
                            // support keys before accepting this transition.
                            if owner.Constraints |> Seq.exists (fun key ->
                                (key.Kind = SqlCatalogConstraintKind.PrimaryKey
                                 || key.Kind = SqlCatalogConstraintKind.Unique)
                                && startsWith cols (key.Columns |> Seq.toList)) then
                                Error(Unproven "An existing PRIMARY/UNIQUE key may already support the FK.")
                            elif owner.Indexes |> Seq.exists (fun idx ->
                                startsWith cols (idx.Columns |> Seq.toList)) then
                                Error(Unproven "An existing explicit index may already support the FK.")
                            else Ok MayReplaceImplicitFkSupport
                        elif owner.Constraints |> Seq.exists (fun key ->
                            (key.Kind = SqlCatalogConstraintKind.PrimaryKey
                             || key.Kind = SqlCatalogConstraintKind.Unique)
                            && startsWith cols (key.Columns |> Seq.toList)) then
                            Error(Unproven "An existing key may already support this FK, so implicit index origin is unproven.")
                        elif owner.Indexes |> Seq.exists (fun idx ->
                            startsWith cols (idx.Columns |> Seq.toList)) then
                            Error(Unproven "An existing explicit index may already support this FK, so implicit index origin is unproven.")
                        else Ok StableImplicitFkSupport
                    | _ ->
                        Error(Unproven "Multiple MySQL FKs may share an implicit support index; physical lineage is not proven.")
                match supportProof with
                | Error proof -> Error proof
                | Ok transition ->
                    let keyNames =
                        owner.Constraints
                        |> Seq.choose (fun c ->
                            match c.Kind with
                            | SqlCatalogConstraintKind.PrimaryKey -> Some PrimaryIndex
                            | SqlCatalogConstraintKind.Unique ->
                                Some(NamedUniqueIndex c.Name)
                            | SqlCatalogConstraintKind.ForeignKey ->
                                Some(NamedForeignKeySupportingIndex c.Name))
                        |> Seq.toList
                    let all =
                        keyNames @
                        (owner.Indexes |> Seq.map (fun i -> StandaloneIndex i.Name)
                                       |> Seq.toList)
                    let rendered = all |> List.map physicalMySqlIndexName
                    let canonical = rendered |> List.map (fun n -> n.ToUpperInvariant())
                    if rendered |> List.exists (fun name -> not (asciiName name)) then
                        Error(Unproven "Physical PRIMARY/UNIQUE/FK index identity is not a bounded simple ASCII name.")
                    elif canonical.Length <> (canonical |> List.distinct |> List.length) then
                        Error(Invalid "The known MySQL key/index namespace contains colliding physical names.")
                    elif rendered |> List.exists (same proposedName) then
                        Error(Invalid ("CREATE INDEX '" + proposedName +
                                 "' collides with a MySQL PRIMARY/UNIQUE/FK-support/standalone physical index."))
                    else Ok transition
    let validateCreate dialect (profile: SqlProviderCapabilityProfile | null) (state: SqlCatalogState) (name: SqlCatalogQualifiedName)
                       (columns: SqlCatalogColumn list)
                       (constraints: SqlCatalogConstraint list) =
        if not (complete state) then
            Unproven "Existing Catalog is incomplete; referenced key dependencies cannot be proven."
        elif constraints |> List.exists (fun d ->
            d.Kind = SqlCatalogConstraintKind.ForeignKey)
             && not (RewriteCatalogIdentifierProof.canProvePhysicalNames dialect profile state name.DisplayName
                     || (dialect = SqlAgentToolType.MySQL
                         && RewriteCatalogIdentifierProof.canProveMySqlLowercaseZeroCatalog
                             profile state name columns constraints)) then
            Unproven "Foreign-key table resolution needs a proven provider identifier case/collation contract."
        else
            let mutable error: DependencyProof option = None
            let reject message =
                if error.IsNone then error <- Some(Invalid message)
            let named = constraints |> List.choose (fun d ->
                if String.IsNullOrEmpty(d.Name) then None else Some d.Name)
            if (named |> List.distinct).Length <> named.Length then
                reject "CREATE TABLE contains duplicate constraint names."
            // MySQL InnoDB FK symbols are unique per schema, not per child
            // table. The physical engine rejects duplicates across children.
            if dialect = SqlAgentToolType.MySQL then
                let knownForeignNames =
                    state.Tables
                    |> Seq.collect (fun table -> table.Constraints)
                    |> Seq.filter (fun c ->
                        c.Kind = SqlCatalogConstraintKind.ForeignKey
                        && not (String.IsNullOrWhiteSpace c.Name))
                    |> Seq.map (fun c -> c.Name)
                    |> Set.ofSeq
                if constraints |> List.exists (fun c ->
                    c.Kind = SqlCatalogConstraintKind.ForeignKey
                    && not (String.IsNullOrWhiteSpace c.Name)
                    && Set.contains c.Name knownForeignNames) then
                    reject "MySQL FOREIGN KEY constraint symbol already exists in the known schema."
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
                            if not (isNull item.ReferencedIdentity) && equals item.ReferencedIdentity.Key name.Key then
                                Some(columns, constraints)
                            else
                                match RewriteCatalogModel.lookup state item.ReferencedIdentity with
                                | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                                    Some(table.Columns |> Seq.toList,
                                         table.Constraints |> Seq.toList)
                                | _ -> None
                        match parent with
                        | None ->
                            let uncertain =
                                not (isNull item.ReferencedIdentity)
                                && (RewriteCatalogModel.hasUnqualifiedScopeConflict
                                        state item.ReferencedIdentity
                                    || (name.IsQualified
                                        && item.ReferencedIdentity.Parts.Count = 1
                                        && equals
                                            name.Parts[name.Parts.Count - 1]
                                            item.ReferencedIdentity.Parts[0]))
                            if uncertain then
                                if error.IsNone then
                                    error <- Some(Unproven(
                                        "Foreign-key parent may resolve through an undeclared default schema/search_path."))
                            else
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

    let validateNewConstraint dialect profile state name
                              (constraintInfo: SqlCatalogConstraint) =
        match RewriteCatalogModel.lookup state name with
        | None -> Invalid ("Cannot add constraint to missing table '" + name.DisplayName + "'.")
        | Some table ->
            if String.IsNullOrWhiteSpace constraintInfo.Name then
                Unproven "ALTER ADD CONSTRAINT requires an explicit constraint name."
            elif table.Constraints |> Seq.exists (fun item ->
                equals item.Name constraintInfo.Name) then
                Invalid ("Constraint '" + constraintInfo.Name + "' already exists.")
            else
                let proposed = Seq.append table.Constraints [constraintInfo] |> Seq.toList
                match validateCreate dialect profile state name
                          (table.Columns |> Seq.toList) proposed with
                | Proven ->
                    // Structural validity is not a proof of existing row data.
                    // PRIMARY KEY / UNIQUE may find duplicates; FOREIGN KEY
                    // may find unmatched child rows. No cardinality evidence
                    // is carried by a Virtual Catalog.
                    Unproven "ALTER ADD CONSTRAINT depends on existing row values; uniqueness or referential integrity is not proven."
                | other -> other

    let canDropConstraint (state: SqlCatalogState) tableName
                          (constraintInfo: SqlCatalogConstraint) =
        if not (complete state) then
            Unproven "Constraint dependencies cannot be proven against an incomplete Catalog."
        elif constraintInfo.Kind = SqlCatalogConstraintKind.ForeignKey then Proven
        else
            let inbound =
                RewriteCatalogModel.inboundForeignKeys state tableName
                |> Seq.exists (fun (_, fk) ->
                    fk.ReferencedColumns.Count = constraintInfo.Columns.Count
                    && Seq.forall2 equals fk.ReferencedColumns constraintInfo.Columns)
            if inbound then
                Invalid ("DROP CONSTRAINT '" + constraintInfo.Name +
                         "' is blocked by a referencing FOREIGN KEY.")
            else Proven

    let canDropTable (state: SqlCatalogState) name =
        if not (complete state) then
            Unproven "Cannot prove absence of dependent tables in an incomplete Catalog."
        else
            let inbound =
                RewriteCatalogModel.inboundForeignKeys state name
                |> Seq.tryFind (fun (table, _) -> not (equals table.QualifiedName.Key name.Key))
            match inbound with
            | Some(table, _) ->
                Invalid ("DROP TABLE " + name.DisplayName +
                         " is blocked by a FOREIGN KEY in table '" + table.Name + "'.")
            | None -> Proven

    let canDropColumn (state: SqlCatalogState) tableName columnName =
        if not (complete state) then
            Unproven "Cannot prove absence of column constraints in an incomplete Catalog."
        else
            match RewriteCatalogModel.columnDependencies state tableName columnName with
            | (owner, dependencyKind)::_ ->
                Invalid ("DROP COLUMN " + columnName +
                         " is blocked by " + dependencyKind +
                         " dependency in table '" + owner.DisplayName + "'.")
            | [] -> Proven
