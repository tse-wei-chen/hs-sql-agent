namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Enums
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
          UniqueKeys: string list list }

    // A literal NULL is a fixed value but does not provide non-NULL evidence.
    // Ordinary equality is NULL-rejecting; NULL-safe equality is not.
    type private Operand =
        | FixedLiteral of isNonNull: bool
        | Field of relationIndex: int * columnName: string

    let private keyPart (part: IdentifierPart) =
        if part.WasQuoted then part.Value else part.Value.ToLowerInvariant()

    let private escaped (part: string) =
        part.Replace("\\", "\\\\").Replace(".", "\\.")

    let private identifierKey identifier =
        Identifier.parts identifier
        |> List.map (keyPart >> escaped)
        |> String.concat "."

    let private sourceQualifiers name alias =
        match alias with
        | Some value -> Set.singleton (keyPart value |> escaped)
        | None ->
            let full = identifierKey name
            let tail = Identifier.parts name |> List.last |> keyPart |> escaped
            Set.ofList [full; tail]

    let private uniqueKeys (table: SqlCatalogTable) =
        table.Constraints
        |> Seq.choose (fun item ->
            RewriteResourceBudget.checkpoint ()
            match item.Kind with
            | SqlCatalogConstraintKind.PrimaryKey
            | SqlCatalogConstraintKind.Unique when item.Columns.Count > 0 ->
                item.Columns
                |> Seq.map (fun name -> name)
                |> Seq.toList
                |> Some
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

    let private resolveField (relations: Relation list) expression =
        match Expr.unspan expression with
        | Column identifier
        | BoundColumn(identifier, ColumnBinding.LocalRowSource) ->
            let parts = Identifier.parts identifier
            let columnName = parts |> List.last |> keyPart
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
                        |> List.map (keyPart >> escaped)
                        |> String.concat "."
                    relations |> List.filter (fun relation ->
                        RewriteResourceBudget.checkpoint ()
                        relation.Qualifiers.Contains qualifier)
            match matches with
            | [relation] when relation.Columns.Contains columnName ->
                Some(Field(relation.Position, columnName))
            | _ -> None
        | Literal ScalarValue.Null -> Some(FixedLiteral false)
        | Literal _ -> Some(FixedLiteral true)
        | _ -> None

    let private mandatoryEqualities (relations: Relation list) predicate =
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
                     || comparison = BinaryOperator.NotDistinctFrom ->
                match resolveField relations left, resolveField relations right with
                | Some a, Some b -> matched.Add(comparison, a, b)
                | _ -> ()
            | _ -> ()  // OR/NOT/IN/functions cannot make equality mandatory.
        matched |> Seq.toList

    let private proof (state: SqlCatalogState) target targetAlias sources predicate =
        let tableByIdentity =
            state.Tables
            |> Seq.map (fun table -> table.QualifiedName.BinderKey, table)
            |> Map.ofSeq
        let create position name alias =
            match Map.tryFind (identifierKey name) tableByIdentity with
            | None -> None
            | Some table ->
                Some { Position = position
                       Qualifiers = sourceQualifiers name alias
                       Columns = table.Columns |> Seq.map (fun col -> col.Name) |> Set.ofSeq
                       NonNullColumns = nonNullColumns table
                       UniqueKeys = uniqueKeys table }
        let targetRelation = create 0 target targetAlias
        let readRelations =
            sources
            |> List.mapi (fun index source ->
                RewriteResourceBudget.checkpoint ()
                match source with
                | NamedTable(name, alias) -> create (index + 1) name alias
                | CteTable _ | DerivedTable _ | LateralDerivedTable _ -> None)
        if targetRelation.IsNone || (readRelations |> List.exists Option.isNone) then
            SqlMutationSourceMatchCardinality.Unverified
        else
            let all = Option.get targetRelation :: (readRelations |> List.choose id)
            let relationByPosition = all |> List.toArray
            let equalities =
                predicate
                |> Option.map (mandatoryEqualities all)
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

    /// Only validated PostgreSQL AST + a complete known Virtual Catalog can
    /// supply this proof. The caller must establish name/type validation FIRST.
    let classify dialect (state: SqlCatalogState) (bound: BoundSql) =
        match (Bound.value bound).Statement with
        | UpdateStatement update when not update.From.IsEmpty ->
            if dialect = SqlAgentToolType.Postgres && state.IsClosedWorld then
                proof state update.Target update.TargetAlias update.From update.Where
            else SqlMutationSourceMatchCardinality.Unverified
        | DeleteStatement delete when not delete.Using.IsEmpty ->
            if dialect = SqlAgentToolType.Postgres && state.IsClosedWorld then
                proof state delete.Target delete.TargetAlias delete.Using delete.Where
            else SqlMutationSourceMatchCardinality.Unverified
        | _ -> SqlMutationSourceMatchCardinality.NotApplicable
