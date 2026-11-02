namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Read-only source evidence for statically analyzed DML. Never provides a
/// physical target table, executable mutation permission, or runtime effect
/// proof. Only proven PostgreSQL View output columns are admitted.
module internal RewriteMutationAnalyzer =
    let readableViews (dialect: SqlAgentToolType)
                      (state: SqlCatalogState)
                      (bound: BoundSql) : seq<SqlCatalogView> =
        match (Bound.value bound).Statement with
        | InsertStatement _ | UpdateStatement _ | DeleteStatement _
            when dialect = SqlAgentToolType.Postgres ->
            state.Views :> seq<SqlCatalogView>
        | _ -> Seq.empty

/// A strict AST INSERT can identify positional write targets only when
/// explicit names were given, or the ordered physical DDL projection itself
/// was proved by the in-memory Virtual Catalog. Generic snapshots expose
/// column sets, NOT sufficient physical ordinal provenance.
type internal InsertTargetLayout<'column> =
    | ProvenTargets of 'column list
    | UnprovenImplicitOrder

module internal RewriteInsertTargetLayout =
    let select (explicitColumns: 'column list) (physicalColumns: 'column list)
               implicitOrderProven =
        match explicitColumns with
        | _ :: _ -> ProvenTargets explicitColumns
        | [] when implicitOrderProven -> ProvenTargets physicalColumns
        | [] -> UnprovenImplicitOrder


/// Proof of the number of source-row combinations that can match one
/// already-fixed physical target row. This is never a proof of runtime
/// uniqueness or deterministic expression evaluation, and cannot authorize
/// executing the Mutation.
module internal RewriteMutationSourceMatchProof =
    type private Relation =
        { Position: int
          Qualifiers: Set<string>
          Columns: Set<string>
          NonNullColumns: Set<string>
          ExactIntColumns: Set<string>
          OracleNumberColumns: Set<string>
          SqliteIntegerColumns: Set<string>
          UniqueKeys: string list list }

    // A literal NULL is a fixed value but does not provide non-NULL evidence.
    // Ordinary equality is NULL-rejecting; NULL-safe equality is not.
    type private Operand =
        | FixedLiteral of isNonNull: bool
        | Field of relationIndex: int * columnName: string

    let private keyPart dialect (part: IdentifierPart) =
        match dialect with
        | SqlAgentToolType.MsSqlServer ->
            // This proof is invoked *only after* identifier validation has
            // verified an explicitly declared ASCII-safe catalog collation.
            part.Value.ToUpperInvariant()
        | SqlAgentToolType.Oracle ->
            // Oracle folds unquoted identifiers to uppercase, while quoted
            // names retain their exact spelling. Never uppercase quoted names.
            if part.WasQuoted then part.Value else part.Value.ToUpperInvariant()
        | SqlAgentToolType.Sqlite ->
            // Follow RewriteCatalogIdentifierProof.canonicalName: SQLite
            // virtual Catalog physical names are upper-canonical even when
            // the identifier was quoted. PostgreSQL's lowercase rule differs.
            part.Value.ToUpperInvariant()
        | _ ->
            if part.WasQuoted then part.Value else part.Value.ToLowerInvariant()

    let private escaped (part: string) =
        part.Replace("\\", "\\\\").Replace(".", "\\.")

    let private identifierKey dialect identifier =
        Identifier.parts identifier
        |> List.map (keyPart dialect >> escaped)
        |> String.concat "."

    let private sourceQualifiers dialect name alias =
        match alias with
        | Some value -> Set.singleton (keyPart dialect value |> escaped)
        | None ->
            let full = identifierKey dialect name
            let tail = Identifier.parts name |> List.last |> keyPart dialect |> escaped
            Set.ofList [full; tail]

    let private exactIntColumns (table: SqlCatalogTable) =
        table.Columns
        |> Seq.choose (fun column ->
            RewriteResourceBudget.checkpoint ()
            if String.Equals(column.TypeName, "INT", StringComparison.OrdinalIgnoreCase) then
                Some column.Name
            else None)
        |> Set.ofSeq

    /// Exact SQLite INTEGER is significant: INT and BIGINT share affinity,
    /// but are not INTEGER PRIMARY KEY rowid aliases. The closed catalog
    /// parser excludes WITHOUT ROWID and PRIMARY KEY DESC variants.
    let private sqliteIntegerColumns (table: SqlCatalogTable) =
        table.Columns
        |> Seq.choose (fun column ->
            RewriteResourceBudget.checkpoint ()
            if String.Equals(column.TypeName, "INTEGER", StringComparison.OrdinalIgnoreCase) then
                Some column.Name
            else None)
        |> Set.ofSeq

    let private oracleNumberColumns (table: SqlCatalogTable) =
        table.Columns
        |> Seq.choose (fun column ->
            RewriteResourceBudget.checkpoint ()
            // No NUMBER(p,s) / FLOAT / BINARY_FLOAT / VARCHAR2 implicit
            // conversion or precision/scale inference is modeled here.
            if String.Equals(column.TypeName, "NUMBER", StringComparison.OrdinalIgnoreCase) then
                Some column.Name
            else None)
        |> Set.ofSeq

    let private uniqueKeys dialect (table: SqlCatalogTable) =
        // SQL Server compares unlike types using its precedence/conversion
        // rules. An equality to REAL/FLOAT/VARCHAR/BIGINT can collapse values
        // that are distinct in a physical unique index. Admit only exact INT
        // keys in that provider; PostgreSQL keeps its existing proof model.
        let safeInt = exactIntColumns table
        let safeOracleNumber = oracleNumberColumns table
        let safeSqliteInteger = sqliteIntegerColumns table
        table.Constraints
        |> Seq.choose (fun item ->
            RewriteResourceBudget.checkpoint ()
            match item.Kind with
            | SqlCatalogConstraintKind.PrimaryKey
            | SqlCatalogConstraintKind.Unique when item.Columns.Count > 0 ->
                let columns = item.Columns |> Seq.toList
                let supported =
                    match dialect with
                    | SqlAgentToolType.MsSqlServer -> columns |> List.forall safeInt.Contains
                    | SqlAgentToolType.Oracle -> columns |> List.forall safeOracleNumber.Contains
                    | SqlAgentToolType.Sqlite ->
                        // Only a single physical INTEGER PRIMARY KEY aliases a
                        // rowid in the currently modeled SQLite DDL subset.
                        // Ordinary UNIQUE, INT/BIGINT and composite keys do not
                        // get a dynamic-affinity/collation proof.
                        item.Kind = SqlCatalogConstraintKind.PrimaryKey
                        && columns.Length = 1
                        && (columns |> List.forall safeSqliteInteger.Contains)
                    | _ -> true
                if supported then Some columns else None
            | _ -> None)
        |> Seq.toList

    let private nonNullColumns (table: SqlCatalogTable) =
        let declared =
            table.Columns
            |> Seq.choose (fun col ->
                RewriteResourceBudget.checkpoint ()
                if col.Nullable then None else Some col.Name)
            |> Set.ofSeq
        // PostgreSQL PRIMARY KEY columns are non-NULL regardless of whether
        // the DDL column definition explicitly said NOT NULL.
        let primary =
            table.Constraints
            |> Seq.filter (fun constraintInfo ->
                RewriteResourceBudget.checkpoint ()
                constraintInfo.Kind = SqlCatalogConstraintKind.PrimaryKey)
            |> Seq.collect (fun constraintInfo -> constraintInfo.Columns)
            |> Set.ofSeq
        Set.union declared primary

    let private resolveField dialect (relations: Relation list) expression =
        match Expr.unspan expression with
        | Column identifier
        | BoundColumn(identifier, ColumnBinding.LocalRowSource) ->
            let parts = Identifier.parts identifier
            let columnName = parts |> List.last |> keyPart dialect
            let matches =
                if parts.Length = 1 then
                    // Name validation has already proved lexical resolution.
                    // Do not choose a source based on UNIQUE metadata: the
                    // same unqualified field must exist on exactly one local
                    // physical relation, regardless of its key status.
                    relations |> List.filter (fun relation ->
                        RewriteResourceBudget.checkpoint ()
                        relation.Columns.Contains columnName)
                else
                    let qualifier =
                        parts
                        |> List.take (parts.Length - 1)
                        |> List.map (keyPart dialect >> escaped)
                        |> String.concat "."
                    relations |> List.filter (fun relation ->
                        RewriteResourceBudget.checkpoint ()
                        relation.Qualifiers.Contains qualifier)
            match matches with
            | [relation] when relation.Columns.Contains columnName
                              && (match dialect with
                                  | SqlAgentToolType.MsSqlServer ->
                                      relation.ExactIntColumns.Contains columnName
                                  | SqlAgentToolType.Oracle ->
                                      relation.OracleNumberColumns.Contains columnName
                                  | SqlAgentToolType.Sqlite ->
                                      relation.SqliteIntegerColumns.Contains columnName
                                  | _ -> true) ->
                Some(Field(relation.Position, columnName))
            | _ -> None
        | Literal ScalarValue.Null when dialect = SqlAgentToolType.Postgres ->
            Some(FixedLiteral false)
        | Literal(ScalarValue.Integer value)
            when dialect = SqlAgentToolType.MsSqlServer
                 || dialect = SqlAgentToolType.Oracle
                 || dialect = SqlAgentToolType.Sqlite ->
            if value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue then
                Some(FixedLiteral true)
            else None
        | Literal _ when dialect = SqlAgentToolType.Postgres ->
            Some(FixedLiteral true)
        | _ -> None
        | _ -> None

    let private mandatoryEqualities dialect (relations: Relation list) predicate =
        let pending = ResizeArray<Expr>()
        let matched = ResizeArray<BinaryOperator * Operand * Operand>()
        pending.Add predicate
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let at = pending.Count - 1
            let current = pending.[at]
            pending.RemoveAt at
            match Expr.unspan current with
            | Binary(BinaryOperator.And, left, right) ->
                pending.Add right
                pending.Add left
            | Binary(comparison, left, right)
                when comparison = BinaryOperator.Equal
                     || (dialect = SqlAgentToolType.Postgres
                         && comparison = BinaryOperator.NotDistinctFrom) ->
                match resolveField dialect relations left,
                      resolveField dialect relations right with
                | Some a, Some b -> matched.Add(comparison, a, b)
                | _ -> ()
            | _ -> ()  // OR/NOT/IN/functions cannot make equality mandatory.
        matched |> Seq.toList

    let private proof dialect (state: SqlCatalogState)
                      (ctes: Cte list) target targetAlias sources predicate =
        let tableByIdentity =
            state.Tables
            |> Seq.map (fun table -> table.QualifiedName.BinderKey, table)
            |> Map.ofSeq
        let create position name alias =
            match Map.tryFind (identifierKey dialect name) tableByIdentity with
            | None -> None
            | Some table ->
                Some { Position = position
                       Qualifiers = sourceQualifiers dialect name alias
                       Columns = table.Columns |> Seq.map (fun col -> col.Name) |> Set.ofSeq
                       NonNullColumns = nonNullColumns table
                       ExactIntColumns = exactIntColumns table
                       OracleNumberColumns = oracleNumberColumns table
                       SqliteIntegerColumns = sqliteIntegerColumns table
                       UniqueKeys = uniqueKeys dialect table }
        // Bound CTE references are virtual; do not use a CTE's name as
        // evidence of a physical table or a physical write target. A unique
        // key may survive a read-only CTE ONLY as a direct field projection
        // of a catalog-proven exact-INT unique key (including renaming), and
        // only if its entire relational path cannot duplicate a source row.
        // The proof is observational: caller DDL is not live DB metadata.
        let cteRelations =
            if dialect <> SqlAgentToolType.MsSqlServer then Map.empty
            else
                let seen = HashSet<string>(StringComparer.Ordinal)
                let mutable proven: Map<string, Relation> = Map.empty
                // A read-only derived projection preserves a proven key only
                // when every output is a direct column reference, every output
                // name is unambiguous, and all components of the original
                // unique key survive under an injective name mapping.
                // This helper is the single owner for BOTH CTE and derived
                // projection lineage; neither name nor display SQL is proof.
                let projectRelation (items: SelectItem list)
                                    (aliases: IdentifierPart list)
                                    (input: Relation) =
                    if (not aliases.IsEmpty && aliases.Length <> items.Length)
                       || input.UniqueKeys.IsEmpty then None
                    else
                        let projected =
                            items
                            |> List.mapi (fun index item ->
                                RewriteResourceBudget.checkpoint ()
                                let column =
                                    match Expr.unspan item.Expression with
                                    | Column identifier
                                    | BoundColumn(identifier, ColumnBinding.LocalRowSource) ->
                                        let parts = Identifier.parts identifier
                                        let physical = parts |> List.last |> keyPart dialect
                                        let validQualifier =
                                            if parts.Length = 1 then true
                                            else
                                                let qualifier =
                                                    parts
                                                    |> List.take (parts.Length - 1)
                                                    |> List.map (keyPart dialect >> escaped)
                                                    |> String.concat "."
                                                input.Qualifiers.Contains qualifier
                                        if validQualifier && input.Columns.Contains physical then
                                            Some physical
                                        else None
                                    | _ -> None
                                let output =
                                    if not aliases.IsEmpty then Some(keyPart dialect aliases.[index])
                                    else
                                        match item.Alias, column with
                                        | Some alias, _ -> Some(keyPart dialect alias)
                                        | None, Some field -> Some field
                                        | _ -> None
                                match column, output with
                                | Some sourceName, Some outputName -> Some(sourceName, outputName)
                                | _ -> None)
                        if projected |> List.exists Option.isNone then None
                        else
                            let mapping = projected |> List.choose id
                            let names = HashSet<string>(StringComparer.Ordinal)
                            if not (mapping |> List.forall (fun (_, output) -> names.Add output)) then None
                            else
                                let rename values =
                                    values
                                    |> Seq.choose (fun original ->
                                        mapping
                                        |> List.tryFind (fun (inputName, _) -> inputName = original)
                                        |> Option.map snd)
                                    |> Set.ofSeq
                                let keys =
                                    input.UniqueKeys
                                    |> List.choose (fun key ->
                                        let translated =
                                            key
                                            |> List.map (fun component ->
                                                mapping
                                                |> List.tryFind (fun (sourceName, _) -> sourceName = component)
                                                |> Option.map snd)
                                        if translated |> List.forall Option.isSome then
                                            let translated = translated |> List.choose id
                                            if (translated |> List.distinct |> List.length) = key.Length then
                                                Some translated
                                            else None
                                        else None)
                                if keys.IsEmpty then None
                                else
                                    Some
                                        { Position = 0
                                          Qualifiers = Set.empty
                                          Columns = mapping |> List.map snd |> Set.ofList
                                          NonNullColumns = rename input.NonNullColumns
                                          ExactIntColumns = rename input.ExactIntColumns
                                          OracleNumberColumns = Set.empty
                                          SqliteIntegerColumns = Set.empty
                                          UniqueKeys = keys }

                // Each derived layer must preserve a single physical row
                // identity with a direct, injective column projection. Two
                // layers are the bounded M06 extension; deeper nesting,
                // correlated/lateral or multiplicative sources remain closed.
                let rec derivedSource depth (query: Query) (alias: IdentifierPart) =
                    let head = query.Head
                    if depth > 2 || not query.SetOperations.IsEmpty
                       || not query.OrderBy.IsEmpty
                       || query.Limit.IsSome || query.Offset.IsSome
                       || query.FetchPercent.IsSome || query.FetchWithTies
                       || not head.Ctes.IsEmpty || not head.Joins.IsEmpty
                       || head.Where.IsSome || not head.GroupBy.IsEmpty
                       || head.Having.IsSome
                       || head.DistinctMode <> SelectDistinct.AllRows
                       || head.Projection.Length > 32 then None
                    else
                        let input =
                            match head.From with
                            | Some(NamedTable(name, physicalAlias)) ->
                                create 0 name physicalAlias
                            | Some(DerivedTable(nested, nestedAlias)) when depth < 2 ->
                                derivedSource (depth + 1) nested nestedAlias
                            | _ -> None
                        input
                        |> Option.bind (projectRelation head.Projection [])
                        |> Option.map (fun relation ->
                            { relation with
                                Qualifiers = Set.singleton (keyPart dialect alias |> escaped) })
                let cteSource = function
                    | NamedTable(name, alias) -> create 0 name alias
                    | CteTable(name, alias) ->
                        match Identifier.parts name with
                        | [part] ->
                            Map.tryFind (keyPart dialect part) proven
                            |> Option.map (fun relation ->
                                { relation with Qualifiers = sourceQualifiers dialect name alias })
                        | _ -> None
                    | DerivedTable(query, alias) -> derivedSource 1 query alias
                    | LateralDerivedTable _ -> None
                for cte in ctes do
                    RewriteResourceBudget.checkpoint ()
                    let name = keyPart dialect cte.Name
                    if not (seen.Add name) then
                        proven <- Map.remove name proven
                    else
                        let query = cte.Query
                        let head = query.Head
                        let source =
                            if cte.RecursiveScope
                               || not query.SetOperations.IsEmpty
                               || not head.Ctes.IsEmpty
                               || not head.Joins.IsEmpty
                               || not head.GroupBy.IsEmpty
                               || head.Having.IsSome
                               || head.DistinctMode <> SelectDistinct.AllRows
                               || query.FetchWithTies then None
                            else head.From |> Option.bind cteSource
                        match source with
                        | Some input ->
                            match projectRelation head.Projection cte.ColumnAliases input with
                            | Some relation -> proven <- Map.add name relation proven
                            | None -> ()
                        | None -> ()
                proven
        let targetRelation = create 0 target targetAlias
        let readRelations =
            sources
            |> List.mapi (fun index source ->
                RewriteResourceBudget.checkpoint ()
                match source with
                | NamedTable(name, alias) -> create (index + 1) name alias
                | CteTable(name, alias) when dialect = SqlAgentToolType.MsSqlServer ->
                    match Identifier.parts name with
                    | [part] ->
                        Map.tryFind (keyPart dialect part) cteRelations
                        |> Option.map (fun relation ->
                            { relation with
                                Position = index + 1
                                Qualifiers = sourceQualifiers dialect name alias })
                    | _ -> None
                | CteTable _ | DerivedTable _ | LateralDerivedTable _ -> None)
        if targetRelation.IsNone || (readRelations |> List.exists Option.isNone) then
            SqlMutationSourceMatchCardinality.Unverified
        else
            let all = Option.get targetRelation :: (readRelations |> List.choose id)
            let relationByPosition = all |> List.toArray
            let equalities =
                predicate
                |> Option.map (mandatoryEqualities dialect all)
                |> Option.defaultValue []
            let anchored = HashSet<int>()
            anchored.Add 0 |> ignore // A single target row is already fixed.
            // Build an undirected graph only from *mandatory* WHERE
            // equalities. Edges retain their SQL NULL semantics; a NULL-safe
            // path without any non-NULL evidence cannot prove nullable UNIQUE.
            // This graph never contains predicates under OR, NOT, subqueries
            // or unmodeled expression operators.
            let graph = Dictionary<Operand, ResizeArray<Operand * bool>>()
            let addEdge fromNode next isOrdinary =
                match graph.TryGetValue fromNode with
                | true, edges -> edges.Add((next, isOrdinary))
                | false, _ ->
                    let edges = ResizeArray<Operand * bool>()
                    edges.Add((next, isOrdinary))
                    graph.Add(fromNode, edges)
            for (comparison, left, right) in equalities do
                RewriteResourceBudget.checkpoint ()
                let ordinary = comparison = BinaryOperator.Equal
                addEdge left right ordinary
                addEdge right left ordinary

            let isNonNull value =
                match value with
                | FixedLiteral nonNull -> nonNull
                | Field(index, name) ->
                    relationByPosition.[index].NonNullColumns.Contains name

            let isFixedByOtherSource source value =
                match value with
                | FixedLiteral _ -> true
                | Field(index, _) ->
                    index <> source && anchored.Contains index

            let constrains source column =
                let first = Field(source, column)
                // Each state tracks whether *this path* proves non-NULL:
                // either a physical NOT NULL / PRIMARY KEY, a non-NULL
                // literal, or an ordinary '=' anywhere along the path.
                // The visited set bounds cycles without depending on the
                // caller stack; no source is implicitly fixed by a cycle.
                let pending = Queue<Operand * bool * bool>()
                let visited = HashSet<Operand * bool * bool>()
                pending.Enqueue((first, false, isNonNull first))
                let mutable proven = false
                while pending.Count > 0 && not proven do
                    RewriteResourceBudget.checkpoint ()
                    let value, hasOrdinary, hasNonNull = pending.Dequeue()
                    if visited.Add((value, hasOrdinary, hasNonNull)) then
                        if isFixedByOtherSource source value
                           && (hasOrdinary || hasNonNull) then
                            proven <- true
                        else
                            match graph.TryGetValue value with
                            | false, _ -> ()
                            | true, edges ->
                                for (next, ordinary) in edges do
                                    RewriteResourceBudget.checkpoint ()
                                    pending.Enqueue((
                                        next,
                                        hasOrdinary || ordinary,
                                        hasNonNull || ordinary || isNonNull next))
                proven
            // Each newly fixed unique-key source may anchor another source.
            // A cycle of as-yet-unfixed sources cannot establish cardinality.
            let mutable changed = true
            while changed do
                changed <- false
                for relation in List.tail all do
                    RewriteResourceBudget.checkpoint ()
                    if not (anchored.Contains relation.Position)
                       && (relation.UniqueKeys
                           |> List.exists (fun key ->
                               key |> List.forall (constrains relation.Position))) then
                        anchored.Add relation.Position |> ignore
                        changed <- true
            if anchored.Count = all.Length then
                SqlMutationSourceMatchCardinality.ProvenAtMostOne
            else SqlMutationSourceMatchCardinality.Unverified

    /// Name/type proof and a closed Virtual Catalog must be established FIRST.
    /// Oracle 26 direct-join syntax needs an explicit server version proof;
    /// Oracle's NUMBER equality and identifier folding are provider-local.
    /// SQL Server has its separate declared collation and INT-only fence.
    let classify dialect (profile: SqlProviderCapabilityProfile | null)
                 (state: SqlCatalogState) (bound: BoundSql) =
        let canClassify =
            match dialect with
            | SqlAgentToolType.Postgres
            | SqlAgentToolType.MsSqlServer -> true
            | SqlAgentToolType.Oracle ->
                not (isNull profile)
                && profile.Provider = SqlAgentToolType.Oracle
                && not (isNull profile.ServerVersion)
                && profile.ServerVersion.Major >= 26
            | SqlAgentToolType.Sqlite ->
                // A source-profile syntax gate is necessary but does not
                // authorize runtime execution or declare SQLite type rigidity.
                not (isNull profile)
                && profile.Provider = SqlAgentToolType.Sqlite
                && not (isNull profile.ServerVersion)
                && profile.ServerVersion.CompareTo(Version(3, 33, 0)) >= 0
                // The shared .NET Unicode uppercase transform is not a
                // general SQLite identifier-collation proof. Restrict the
                // physical Catalog to exact ASCII canonical identities.
                && RewriteCatalogIdentifierProof.safeCatalog state
            | _ -> false
        match (Bound.value bound).Statement with
        | UpdateStatement update when not update.From.IsEmpty ->
            if canClassify && state.IsClosedWorld then
                proof dialect state update.Ctes update.Target update.TargetAlias update.From update.Where
            else SqlMutationSourceMatchCardinality.Unverified
        | DeleteStatement delete when not delete.Using.IsEmpty ->
            if canClassify && state.IsClosedWorld then
                proof dialect state delete.Ctes delete.Target delete.TargetAlias delete.Using delete.Where
            else SqlMutationSourceMatchCardinality.Unverified
        | _ -> SqlMutationSourceMatchCardinality.NotApplicable
