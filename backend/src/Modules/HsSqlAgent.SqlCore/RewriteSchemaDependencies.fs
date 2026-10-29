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
        elif owner.MayRetainUnmodeledFkIndexes
             && (owner.PossibleFkIndexNames.Count = 0
                 || (owner.PossibleFkIndexNames |> Seq.exists (fun name ->
                     not (asciiName name)))) then
            // An unknown FK symbol (or a legacy unknown state) is NOT an
            // enumerated physical namespace. Never treat it as absence.
            Error(Unproven "Possible MySQL FK-support index names are not fully proven.")
        elif owner.MayRetainUnmodeledFkIndexes
             && (owner.PossibleFkIndexNames |> Seq.exists (same proposedName)) then
            Error(Unproven "An old implicit MySQL FK-support index might still occupy this physical name.")
        elif owner.MayRetainUnmodeledFkIndexes
             && not (
                 RewriteCatalogIdentifierProof.canProvePhysicalNames
                     SqlAgentToolType.MySQL profile state owner.QualifiedName.DisplayName
                 || RewriteCatalogIdentifierProof.canProveMySqlLowercaseZeroCatalog
                     profile state owner.QualifiedName
                     (owner.Columns |> Seq.toList)
                     (owner.Constraints |> Seq.toList)) then
            Error(Unproven "Residual FK physical index names require declared server identifier semantics.")
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
                // A named FK created on a complete MySQL Catalog may own
                // an implicit physical supporting index. Two FKs have
                // independent lineage ONLY if their ordered local prefixes
                // are incomparable: shared/prefix-related keys may reuse
                // the same physical index and cannot be proven statically.
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
                let localColumns (fk: SqlCatalogConstraint) =
                    fk.Columns |> Seq.toList
                let overlaps =
                    foreignKeys
                    |> List.mapi (fun i fk ->
                        foreignKeys
                        |> List.skip (i + 1)
                        |> List.exists (fun other ->
                            startsWith (localColumns fk) (localColumns other)
                            || startsWith (localColumns other) (localColumns fk)))
                    |> List.exists id
                let invalidNames =
                    foreignKeys |> List.exists (fun fk ->
                        String.IsNullOrWhiteSpace fk.Name
                        || not (asciiName fk.Name)
                        || fk.Columns.Count = 0)
                let competingSupport =
                    foreignKeys |> List.exists (fun fk ->
                        let prefix = localColumns fk
                        (owner.Constraints |> Seq.exists (fun c ->
                            (c.Kind = SqlCatalogConstraintKind.PrimaryKey
                             || c.Kind = SqlCatalogConstraintKind.Unique)
                            && startsWith prefix (c.Columns |> Seq.toList)))
                        || ((owner.Indexes |> Seq.exists (fun idx ->
                            startsWith prefix (idx.Columns |> Seq.toList)))
                            // An explicit index that was previously proven
                            // to replace this very FK's implicit index is
                            // sufficient independent support. Otherwise we
                            // cannot establish the original auto-index origin.
                            && not (owner.PossibleFkIndexNames |> Seq.exists
                                (fun name -> same name fk.Name))))
                let covered =
                    foreignKeys |> List.filter (fun fk ->
                        startsWith (localColumns fk) members)
                let supportProof =
                    if invalidNames then
                        Error(Unproven "Unnamed or non-ASCII FK supporting indexes have no physical name proof.")
                    elif overlaps then
                        Error(Unproven "Foreign keys sharing an ordered left prefix may share one InnoDB support index.")
                    elif competingSupport then
                        Error(Unproven "An existing PRIMARY/UNIQUE/standalone index may already support a foreign key.")
                    elif covered.Length > 1 then
                        Error(Unproven "One new index may replace multiple implicit FK indexes; physical lineage is not proven.")
                    elif covered.IsEmpty then Ok StableImplicitFkSupport
                    elif covered |> List.forall (fun fk ->
                         owner.PossibleFkIndexNames |> Seq.exists (fun name ->
                             same name fk.Name)) then
                        // An older proven explicit index already covers this
                        // FK. A second independently named explicit index
                        // cannot remove that user-created physical index.
                        Ok StableImplicitFkSupport
                    else Ok MayReplaceImplicitFkSupport
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
                                // Same-name promotion is one physical index,
                                // not two colliding indexes. The prior
                                // replacement proof and matching ordered
                                // prefix are mandatory for deduplication.
                                let promoted =
                                    owner.PossibleFkIndexNames
                                    |> Seq.exists (fun name -> same name c.Name)
                                    && (owner.Indexes |> Seq.exists (fun idx ->
                                        same idx.Name c.Name
                                        && startsWith (c.Columns |> Seq.toList)
                                            (idx.Columns |> Seq.toList)))
                                if promoted then None
                                else Some(NamedForeignKeySupportingIndex c.Name))
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
                    else
                        let collisions =
                            all |> List.filter (fun identity ->
                                same (physicalMySqlIndexName identity) proposedName)
                        // MySQL can promote its OWN implicitly created
                        // FK-supporting index to an explicit covering index
                        // with the same name. No cross-FK or ordinary index
                        // collision is safe to reinterpret this way.
                        let ownFkPromotion =
                            match collisions, transition with
                            | [NamedForeignKeySupportingIndex name],
                              MayReplaceImplicitFkSupport ->
                                covered |> List.exists (fun fk ->
                                    same fk.Name name
                                    && startsWith (localColumns fk) members)
                            | _ -> false
                        if not collisions.IsEmpty && not ownFkPromotion then
                            Error(Invalid ("CREATE INDEX '" + proposedName +
                                  "' collides with a MySQL PRIMARY/UNIQUE/FK-support/standalone physical index."))
                        else Ok transition
    /// A MySQL DROP INDEX is safe for every local FK only when removing
    /// this *tracked physical index* leaves another independent, proven
    /// PK/UNIQUE/explicit index with the same ordered FK left prefix.
    /// A possibly retained InnoDB auto-index is NEVER counted as support.
    let canDropMySqlTrackedIndex
            (state: SqlCatalogState)
            (owner: SqlCatalogTable)
            (indexName: string) =
        let same (a: string) (b: string) =
            String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        let startsWith (prefix: string list) (candidate: string list) =
            prefix.Length <= candidate.Length
            && List.forall2 (fun a b ->
                String.Equals(a, b, StringComparison.Ordinal))
                prefix (candidate |> List.take prefix.Length)
        if not (complete state) then
            Unproven "A MySQL DROP INDEX requires a complete known Catalog."
        else
            match owner.Indexes |> Seq.tryFind (fun idx ->
                same idx.Name indexName) with
            | None ->
                Unproven "The requested MySQL index is not a tracked explicit index."
            | Some target ->
                let unsupportedFk =
                    owner.Constraints
                    |> Seq.filter (fun fk ->
                        fk.Kind = SqlCatalogConstraintKind.ForeignKey
                        && startsWith (fk.Columns |> Seq.toList)
                            (target.Columns |> Seq.toList))
                    |> Seq.tryFind (fun fk ->
                        let members = fk.Columns |> Seq.toList
                        let otherExplicit =
                            owner.Indexes |> Seq.exists (fun idx ->
                                not (same idx.Name target.Name)
                                && startsWith members (idx.Columns |> Seq.toList))
                        let otherKey =
                            owner.Constraints |> Seq.exists (fun c ->
                                (c.Kind = SqlCatalogConstraintKind.PrimaryKey
                                 || c.Kind = SqlCatalogConstraintKind.Unique)
                                && startsWith members (c.Columns |> Seq.toList))
                        not (otherExplicit || otherKey))
                match unsupportedFk with
                | Some fk ->
                    Unproven ("Dropping index '" + indexName +
                        "' could remove the last proven support for foreign key '" +
                        fk.Name + "'.")
                | None -> Proven

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
