namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Script
open HsSqlAgent.SqlCore.Rewrite.RewriteSchemaAnalyzer

/// Statement-order coordinator. A Catalog is simulated, not queried or
/// executed; neither successful analysis nor a known Catalog authorizes SQL.
module internal RewriteScriptAnalyzer =

    /// One visible stage-column can represent several physical USING
    /// inputs. The chosen projected owner and the complete physical
    /// dependency lineage are deliberately distinct.
    type private ViewGraphColumn =
        { Output: SqlCatalogColumn
          Owner: SqlCatalogQualifiedName
          PhysicalName: string
          Lineage: (SqlCatalogQualifiedName * string) list }

    /// New relational comparisons require a proven PostgreSQL scalar
    /// operator, rather than accepting arbitrary same-name SQL type tags.
    /// Existing equality / NULL-safe equality retain their prior contract.
    let private provenOnComparison op (typeName: string) =
        let name = typeName.ToUpperInvariant()
        let ordered =
            List.contains name
                ["SMALLINT"; "INT"; "INTEGER"; "BIGINT"; "REAL";
                 "DOUBLE PRECISION"; "TEXT"; "DATE"]
            || name.StartsWith("VARCHAR(", StringComparison.Ordinal)
            || name.StartsWith("CHAR(", StringComparison.Ordinal)
            || name.StartsWith("DECIMAL(", StringComparison.Ordinal)
            || name.StartsWith("NUMERIC(", StringComparison.Ordinal)
        let scalar = ordered || List.contains name ["BOOLEAN"; "UUID"]
        match op with
        | OrdinaryEquality | NullSafeEquality -> true
        | OrdinaryInequality | NullSafeInequality -> scalar
        | LessThan | LessThanOrEqual | GreaterThan | GreaterThanOrEqual -> ordered

    /// PostgreSQL cannot generally plan FULL JOIN without a joinable
    /// cross-input equality. OR alternatives do not establish a mandatory
    /// equality, even when some OR branches individually contain one.
    /// This is stricter than SQL parsing: it proves a usable View contract.
    let private hasFullJoinEquiAnchor
            (condition: ViewJoinCriteria)
            (crossEqui: ViewPredicateColumn -> ViewPredicateColumn -> bool) =
        let rec mandatory node =
            RewriteResourceBudget.checkpoint ()
            match node with
            | OnCompare(left, OrdinaryEquality, right) -> crossEqui left right
            | OnCompare _ -> false
            | OnAnd(left, right) -> mandatory left || mandatory right
            | OnOr _ -> false
        match condition with
        | UsingKeys keys -> not keys.IsEmpty
        | OnEquality comparisons ->
            comparisons |> List.exists (fun (left, op, right) ->
                op = OrdinaryEquality && crossEqui left right)
        | OnBoolean expression -> mandatory expression

    /// Collect every typed comparison leaf while keeping the original
    /// OnBoolean tree intact for SQL three-valued Boolean semantics.
    /// Every leaf, including alternate OR branches, must prove its
    /// source binding and physical dependency independently.
    let rec private viewOnLeaves predicate =
        RewriteResourceBudget.checkpoint ()
        match predicate with
        | OnCompare(left, op, right) -> [left, op, right]
        | OnAnd(left, right)
        | OnOr(left, right) ->
            viewOnLeaves left @ viewOnLeaves right

    let private viewOnCriteriaLeaves condition =
        match condition with
        | OnEquality pairs -> pairs
        | OnBoolean predicate -> viewOnLeaves predicate
        | UsingKeys _ -> []

    /// One equality operand binds to one side of the *current* join edge.
    /// A prior merged USING column can own several physical dependencies
    /// without losing its single stage-visible SQL name or type.
    type private ViewStageOnSide =
        | PriorStage
        | IntroducedSource

    type private ViewStageOnOperand =
        { Side: ViewStageOnSide
          TypeName: string
          Lineage: (SqlCatalogQualifiedName * string) list }

    /// Closed left-deep multi-source JOIN proof. An ON operand may be
    /// physically qualified or uniquely visible at this precise stage.
    /// Neither form may cross to a future source or resolve ambiguity
    /// by guessing one of several same-named physical columns.
    let private proveMultiUsingGraph
            (proven: (SqlCatalogQualifiedName * string * SqlCatalogColumn list) list)
            (joins: ViewJoinEdge list)
            : (ViewGraphColumn list * (SqlCatalogQualifiedName * string) list) option =
        match proven with
        | [] -> None
        | (firstId, _, firstColumns)::_ ->
            let bindPhysical identity (column: SqlCatalogColumn) =
                { Output = column
                  Owner = identity
                  PhysicalName = column.Name
                  Lineage = [identity, column.Name] }
            let nullExtend (field: ViewGraphColumn) =
                { field with
                    Output = SqlCatalogColumn(
                        field.Output.Name, field.Output.TypeName, true) }
            let mutable visible =
                firstColumns |> List.map (bindPhysical firstId)
            let required = ResizeArray<SqlCatalogQualifiedName * string>()
            let mutable provenGraph = true
            for edgeIndex in 0 .. joins.Length - 1 do
                RewriteResourceBudget.checkpoint ()
                if provenGraph then
                    let edge = joins.[edgeIndex]
                    let newId, _, newColumns = proven.[edgeIndex + 1]
                    let newlyBound = newColumns |> List.map (bindPhysical newId)
                    let extendLeft =
                        edge.Kind = RightOuterJoin || edge.Kind = FullOuterJoin
                    let extendRight =
                        edge.Kind = LeftOuterJoin || edge.Kind = FullOuterJoin
                    let extend apply fields =
                        if apply then fields |> List.map nullExtend else fields
                    let next =
                        match edge.Condition with
                        | UsingKeys keys ->
                            if (keys |> List.distinct |> List.length) <> keys.Length then
                                None
                            else
                                let keyProof key =
                                    RewriteResourceBudget.checkpoint ()
                                    let byName fields =
                                        fields |> List.filter (fun field ->
                                            String.Equals(field.Output.Name, key,
                                                          StringComparison.Ordinal))
                                    match byName visible, byName newlyBound with
                                    | [left], [right] when
                                        String.Equals(left.Output.TypeName,
                                                      right.Output.TypeName,
                                                      StringComparison.Ordinal) ->
                                        let chosen, nullable =
                                            match edge.Kind with
                                            | InnerJoin | LeftOuterJoin ->
                                                left, left.Output.Nullable
                                            | RightOuterJoin ->
                                                right, right.Output.Nullable
                                            | FullOuterJoin ->
                                                left,
                                                (left.Output.Nullable
                                                 || right.Output.Nullable)
                                        Some {
                                            Output = SqlCatalogColumn(
                                                key, chosen.Output.TypeName, nullable)
                                            Owner = chosen.Owner
                                            PhysicalName = chosen.PhysicalName
                                            Lineage = left.Lineage @ right.Lineage }
                                    | _ -> None
                                let proofs = keys |> List.map keyProof
                                if proofs |> List.exists Option.isNone then None
                                else
                                    let merged = proofs |> List.choose id
                                    let keySet = Set.ofList keys
                                    let notMerged (field: ViewGraphColumn) =
                                        not (Set.contains field.Output.Name keySet)
                                    let leftRest =
                                        visible |> List.filter notMerged
                                        |> extend extendLeft
                                    let rightRest =
                                        newlyBound |> List.filter notMerged
                                        |> extend extendRight
                                    Some(merged @ leftRest @ rightRest,
                                         merged |> List.collect (fun field -> field.Lineage))
                        | OnEquality _ | OnBoolean _ ->
                            let pairs = viewOnCriteriaLeaves edge.Condition
                            let bindOperand (operand: ViewPredicateColumn)
                                    : ViewStageOnOperand option =
                                match operand.Qualifier with
                                | None ->
                                    // PostgreSQL name resolution uses the
                                    // prior visible LEFT join result plus the
                                    // newly introduced RIGHT input, *not*
                                    // all underlying physical source columns.
                                    let matches =
                                        (visible |> List.map (fun field ->
                                            PriorStage, field))
                                        @ (newlyBound |> List.map (fun field ->
                                            IntroducedSource, field))
                                        |> List.filter (fun (_, field) ->
                                            String.Equals(
                                                field.Output.Name,
                                                operand.SourceColumn,
                                                StringComparison.Ordinal))
                                    match matches with
                                    | [side, field] ->
                                        Some {
                                            Side = side
                                            TypeName = field.Output.TypeName
                                            Lineage = field.Lineage }
                                    | _ -> None
                                | Some qualifier ->
                                    let matches =
                                        proven |> List.take (edgeIndex + 2)
                                        |> List.collect (fun (identity, alias, cols) ->
                                            if not (String.Equals(alias, qualifier,
                                                                  StringComparison.Ordinal))
                                            then []
                                            else
                                                cols |> List.filter (fun column ->
                                                    String.Equals(
                                                        column.Name, operand.SourceColumn,
                                                        StringComparison.Ordinal))
                                                |> List.map (fun column -> identity, column))
                                    match matches with
                                    | [identity, column] ->
                                        let side =
                                            if String.Equals(
                                                identity.Key, newId.Key,
                                                StringComparison.Ordinal)
                                            then IntroducedSource
                                            else PriorStage
                                        Some {
                                            Side = side
                                            TypeName = column.TypeName
                                            Lineage = [identity, column.Name] }
                                    | _ -> None
                            let proofs =
                                pairs |> List.map (fun (left, operator, right) ->
                                    RewriteResourceBudget.checkpoint ()
                                    // Preserve original SQL comparison and
                                    // separately prove the operator/type pair.
                                    match bindOperand left, bindOperand right with
                                    | Some(leftBound), Some(rightBound)
                                        when String.Equals(
                                                leftBound.TypeName,
                                                rightBound.TypeName,
                                                StringComparison.Ordinal)
                                             && provenOnComparison operator
                                                    leftBound.TypeName ->
                                        // Preserve each local/cross operand's
                                        // physical dependencies; do not move
                                        // local ON conditions to WHERE.
                                        Some(leftBound.Lineage @ rightBound.Lineage)
                                    | _ -> None)
                            let fullJoinAnchor =
                                edge.Kind <> FullOuterJoin
                                || hasFullJoinEquiAnchor edge.Condition (fun left right ->
                                    match bindOperand left, bindOperand right with
                                    | Some a, Some b ->
                                        a.Side <> b.Side
                                        && String.Equals(a.TypeName, b.TypeName,
                                                         StringComparison.Ordinal)
                                        && provenOnComparison OrdinaryInequality a.TypeName
                                    | _ -> false)
                            if proofs.IsEmpty
                               || (proofs |> List.exists Option.isNone)
                               || not fullJoinAnchor then None
                            else
                                Some(extend extendLeft visible @
                                     extend extendRight newlyBound,
                                     proofs |> List.choose id |> List.concat)
                    match next with
                    | Some(fields, refs) ->
                        visible <- fields
                        for reference in refs do
                            required.Add(reference)
                    | None -> provenGraph <- false
            if provenGraph then Some(visible, required |> Seq.toList)
            else None


    /// FROM instance and physical relation have different identities.
    /// Only instance indices can determine JOIN sides / null extension.
    /// Persistent Catalog dependencies still refer to the physical owner.
    type private ViewInstanceField =
        { Output: SqlCatalogColumn
          SourceIndex: int
          PhysicalName: string
          Lineage: (SqlCatalogQualifiedName * string) list }

    /// A bounded, alias-scoped self-JOIN proof. No synthetic relation ID
    /// escapes into SqlCatalogViewSourceBinding or DROP dependency metadata.
    let private proveInstanceJoinGraph
            (proven: (SqlCatalogQualifiedName * string * SqlCatalogColumn list) list)
            (joins: ViewJoinEdge list)
            : (ViewInstanceField list *
               (SqlCatalogQualifiedName * string) list * Set<int>) option =
        let nodes = proven |> List.toArray
        if nodes.Length < 2 || joins.Length <> nodes.Length - 1 then None
        else
            let idAt index =
                let id, _, _ = nodes.[index]
                id
            let newField index (col: SqlCatalogColumn) =
                { Output = col
                  SourceIndex = index
                  PhysicalName = col.Name
                  Lineage = [idAt index, col.Name] }
            let extend (field: ViewInstanceField) =
                { field with
                    Output = SqlCatalogColumn(
                        field.Output.Name, field.Output.TypeName, true) }
            let _, _, firstCols = nodes.[0]
            let mutable visible = firstCols |> List.map (newField 0)
            let mutable nullableIndices: Set<int> = Set.empty
            let dependencies = ResizeArray<SqlCatalogQualifiedName * string>()
            let mutable complete = true
            for edgeIndex in 0 .. joins.Length - 1 do
                RewriteResourceBudget.checkpoint ()
                if complete then
                    let rightIndex = edgeIndex + 1
                    let _, _, rightCols = nodes.[rightIndex]
                    let incoming = rightCols |> List.map (newField rightIndex)
                    let edge = joins.[edgeIndex]
                    let extendLeft =
                        edge.Kind = RightOuterJoin || edge.Kind = FullOuterJoin
                    let extendRight =
                        edge.Kind = LeftOuterJoin || edge.Kind = FullOuterJoin
                    let withNullable apply fields =
                        if apply then fields |> List.map extend else fields
                    let next:
                        (ViewInstanceField list *
                         (SqlCatalogQualifiedName * string) list) option =
                        match edge.Condition with
                        | UsingKeys keys ->
                            if (keys |> List.distinct |> List.length) <> keys.Length
                            then None
                            else
                                let keyProof key =
                                    RewriteResourceBudget.checkpoint ()
                                    let find fields =
                                        fields |> List.filter (fun field ->
                                            String.Equals(field.Output.Name, key,
                                                          StringComparison.Ordinal))
                                    match find visible, find incoming with
                                    | [left], [right] when
                                        String.Equals(left.Output.TypeName,
                                                      right.Output.TypeName,
                                                      StringComparison.Ordinal) ->
                                        let selected, nullable =
                                            match edge.Kind with
                                            | InnerJoin | LeftOuterJoin ->
                                                left, left.Output.Nullable
                                            | RightOuterJoin ->
                                                right, right.Output.Nullable
                                            | FullOuterJoin ->
                                                left,
                                                (left.Output.Nullable
                                                 || right.Output.Nullable)
                                        Some {
                                            Output = SqlCatalogColumn(
                                                key, selected.Output.TypeName, nullable)
                                            SourceIndex = selected.SourceIndex
                                            PhysicalName = selected.PhysicalName
                                            Lineage = left.Lineage @ right.Lineage }
                                    | _ -> None
                                let keysProven = keys |> List.map keyProof
                                if keysProven |> List.exists Option.isNone then None
                                else
                                    let merged = keysProven |> List.choose id
                                    let keyNames = keys |> Set.ofList
                                    let nonKey (field: ViewInstanceField) =
                                        not (Set.contains field.Output.Name keyNames)
                                    Some(merged
                                         @ (visible |> List.filter nonKey
                                            |> withNullable extendLeft)
                                         @ (incoming |> List.filter nonKey
                                            |> withNullable extendRight),
                                         merged |> List.collect (fun f -> f.Lineage))
                        | OnEquality _ | OnBoolean _ ->
                            let comparisons = viewOnCriteriaLeaves edge.Condition
                            let bind (operand: ViewPredicateColumn) =
                                match operand.Qualifier with
                                | None ->
                                    let hits =
                                        (visible |> List.map (fun f -> PriorStage, f))
                                        @ (incoming |> List.map (fun f ->
                                            IntroducedSource, f))
                                        |> List.filter (fun (_, f) ->
                                            String.Equals(f.Output.Name,
                                                          operand.SourceColumn,
                                                          StringComparison.Ordinal))
                                    match hits with
                                    | [side, field] ->
                                        Some {
                                            Side = side
                                            TypeName = field.Output.TypeName
                                            Lineage = field.Lineage }
                                    | _ -> None
                                | Some alias ->
                                    let matches =
                                        nodes |> Array.mapi (fun index (_, nodeAlias, cols) ->
                                            if index > rightIndex
                                               || not (String.Equals(
                                                   nodeAlias, alias,
                                                   StringComparison.Ordinal))
                                            then []
                                            else
                                                cols |> List.filter (fun col ->
                                                    String.Equals(col.Name,
                                                                  operand.SourceColumn,
                                                                  StringComparison.Ordinal))
                                                |> List.map (fun col -> index, col))
                                        |> Array.toList |> List.concat
                                    match matches with
                                    | [index, col] ->
                                        Some {
                                            Side =
                                                if index = rightIndex
                                                then IntroducedSource
                                                else PriorStage
                                            TypeName = col.TypeName
                                            Lineage = [idAt index, col.Name] }
                                    | _ -> None
                            let proofs =
                                comparisons |> List.map (fun (left, op, right) ->
                                    RewriteResourceBudget.checkpoint ()
                                    match bind left, bind right with
                                    | Some a, Some b
                                        when String.Equals(a.TypeName, b.TypeName,
                                                           StringComparison.Ordinal)
                                             && provenOnComparison op a.TypeName ->
                                        Some(a.Lineage @ b.Lineage)
                                    | _ -> None)
                            let fullJoinAnchor =
                                edge.Kind <> FullOuterJoin
                                || hasFullJoinEquiAnchor edge.Condition (fun left right ->
                                    match bind left, bind right with
                                    | Some a, Some b ->
                                        a.Side <> b.Side
                                        && String.Equals(a.TypeName, b.TypeName,
                                                         StringComparison.Ordinal)
                                        && provenOnComparison OrdinaryInequality a.TypeName
                                    | _ -> false)
                            if proofs.IsEmpty
                               || (proofs |> List.exists Option.isNone)
                               || not fullJoinAnchor then None
                            else
                                Some((visible |> withNullable extendLeft)
                                     @ (incoming |> withNullable extendRight),
                                     proofs |> List.choose id |> List.concat)
                    match next with
                    | None -> complete <- false
                    | Some(fields, refs) ->
                        visible <- fields
                        for reference in refs do dependencies.Add(reference)
                        match edge.Kind with
                        | InnerJoin -> ()
                        | LeftOuterJoin ->
                            nullableIndices <- Set.add rightIndex nullableIndices
                        | RightOuterJoin ->
                            for i in 0 .. edgeIndex do
                                nullableIndices <- Set.add i nullableIndices
                        | FullOuterJoin ->
                            for i in 0 .. rightIndex do
                                nullableIndices <- Set.add i nullableIndices
            if complete then
                Some(visible, dependencies |> Seq.toList, nullableIndices)
            else None

    type private BatchIndexResolution =
        | BatchIndexFound of SqlCatalogQualifiedName * string
        | BatchIndexAbsent
        | BatchIndexInvalid of string
        | BatchIndexUnproven of string

    let private readOnly (items: seq<'T>) : IReadOnlyList<'T> =
        (items |> Seq.toArray |> Array.AsReadOnly) :> IReadOnlyList<'T>

    let private diagnostic (syntax: SqlScriptStatementSyntax) code category message =
        SqlDiagnostic(code, SqlDiagnosticStage.SemanticValidation, category, message, syntax.Span)

    let private changed (syntax: SqlScriptStatementSyntax) name operation certainty =
        SqlCatalogChange(name, operation, certainty, syntax.Span)

    let private changedColumn (syntax: SqlScriptStatementSyntax) name column operation =
        SqlCatalogChange(name, operation, SqlCatalogKnowledge.Known, syntax.Span, column)

    let private changedExact (syntax: SqlScriptStatementSyntax)
                             (name: SqlCatalogQualifiedName) operation certainty =
        SqlCatalogChange(
            name.DisplayName, operation, certainty, syntax.Span, null, name)

    let private changedConstraintExact (syntax: SqlScriptStatementSyntax)
                                       (name: SqlCatalogQualifiedName) constraintName =
        SqlCatalogChange(
            name.DisplayName, SqlCatalogOperation.DropConstraint,
            SqlCatalogKnowledge.Known, syntax.Span, null, name, constraintName)

    let private changedColumnExact (syntax: SqlScriptStatementSyntax)
                                   (name: SqlCatalogQualifiedName) column operation =
        SqlCatalogChange(
            name.DisplayName, operation, SqlCatalogKnowledge.Known,
            syntax.Span, column, name)

    let private analyzeDdl sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null)
                           (scope: RewriteCatalogSchemaResolution.SchemaScope)
                           (state: SqlCatalogState) (syntax: SqlScriptStatementSyntax) =
        let tokens =
            RewriteResourceBudget.withStage "SchemaLexical" SqlDiagnosticStage.Lexical (fun () ->
                RewriteFacadeAdapter.tokenizeSourceValidated syntax.Text sourceDialect sourceProfile)
            |> List.toArray
        let parsed =
            RewriteResourceBudget.withStage "SchemaParsing" SqlDiagnosticStage.Parse (fun () ->
                RewriteSchemaAnalyzer.parseForWith sourceDialect sourceProfile tokens)
        let fail message =
            let error = diagnostic syntax "SQL_SCHEMA_SEMANTIC_INVALID" SqlDiagnosticCategory.Semantic message
            // A locally invalid DDL is rejected regardless of prior schema
            // state, but rejection must never promote an incomplete Catalog
            // into a known, closed-world proof. Keep a known rejection only
            // when the caller's current Catalog itself is completely known.
            let certainty =
                if state.Knowledge = SqlCatalogKnowledge.Known then
                    SqlCatalogKnowledge.Known
                else SqlCatalogKnowledge.Unknown
            SqlStatementAnalysisStatus.Invalid, certainty,
            state, ([]: SqlCatalogChange list), [error]
        let partial (name: SqlCatalogQualifiedName) operation =
            let next = RewriteCatalogModel.unknownTable state name
            SqlStatementAnalysisStatus.PartiallyAnalyzed, SqlCatalogKnowledge.Unknown,
            next, [changed syntax name.DisplayName operation SqlCatalogKnowledge.Unknown], []
        let noChange =
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            state, ([]: SqlCatalogChange list), ([]: SqlDiagnostic list)
        let exact (name: SqlCatalogQualifiedName) operation next =
            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
            next, [changedExact syntax name operation SqlCatalogKnowledge.Known], []
        let dependencyUnproven message =
            let unknown = RewriteCatalogModel.taintAll state
            let error =
                diagnostic syntax "SQL_SCHEMA_DEPENDENCY_UNPROVEN"
                    SqlDiagnosticCategory.Capability message
            SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
            unknown,
            [changed syntax "*" SqlCatalogOperation.Indeterminate SqlCatalogKnowledge.Unknown],
            [error]
        let unsupportedDeclaredScope =
            sourceDialect = SqlAgentToolType.Postgres
            && (match scope with
                | RewriteCatalogSchemaResolution.Unproven ->
                    not (isNull sourceProfile)
                    && not (isNull (sourceProfile.GetSessionSetting("SEARCH_PATH")))
                | _ -> false)
        let resolve = RewriteCatalogSchemaResolution.resolve scope
        let resolveConstraint (constraintInfo: SqlCatalogConstraint) =
            if isNull constraintInfo.ReferencedIdentity then constraintInfo
            else
                SqlCatalogConstraint(
                    constraintInfo.Name, constraintInfo.Kind,
                    constraintInfo.Columns,
                    resolve constraintInfo.ReferencedIdentity,
                    constraintInfo.ReferencedColumns)
        let resolvedOperation = function
            | Create(name, columns, constraints, conditional) ->
                Create(resolve name, columns, List.map resolveConstraint constraints, conditional)
            | Recreate(name, columns, constraints) ->
                Recreate(resolve name, columns, List.map resolveConstraint constraints)
            | CreateView(view, sources, projection, join, predicate, mode, header) ->
                CreateView(resolve view,
                           (sources |> List.map (fun item ->
                               { item with Relation = resolve item.Relation })),
                           projection, join, predicate, mode, header)
            | DropView(view, conditional, mode) ->
                DropView(resolve view, conditional, mode)
            | DropViewBatch(views, conditional, mode) ->
                DropViewBatch(List.map resolve views, conditional, mode)
            | Drop(name, conditional) -> Drop(resolve name, conditional)
            | DropTableBatch(names, conditional, mode) ->
                DropTableBatch(List.map resolve names, conditional, mode)
            | AlterColumns(name, actions) -> AlterColumns(resolve name, actions)
            | RenameColumn(name, oldName, newName) ->
                RenameColumn(resolve name, oldName, newName)
            | RenameConstraint(name, oldName, newName) ->
                RenameConstraint(resolve name, oldName, newName)
            | CreateIndex(name, indexName, cols, conditional) ->
                CreateIndex(resolve name, indexName, cols, conditional)
            | DropIndex(indexName, target, conditional) ->
                DropIndex(indexName, Option.map resolve target, conditional)
            | DropQualifiedIndex(indexIdentity, conditional) ->
                DropQualifiedIndex(indexIdentity, conditional)
            | DropIndexBatch(identities, conditional) ->
                DropIndexBatch(identities, conditional)
            | AddConstraint(name, constraintInfo) ->
                AddConstraint(resolve name, resolveConstraint constraintInfo)
            | DropConstraint(name, constraintName, conditional, mode) ->
                DropConstraint(resolve name, constraintName, conditional, mode)
        // Inspect raw AST parts before qualifying them; a converted two-part
        // name must never hide an unsupported one-part lookup.
        let rawNames =
            match parsed with
            | Recognized operation ->
                match operation with
                | Create(name, _, constraints, _)
                | Recreate(name, _, constraints) ->
                    name :: (constraints |> List.choose (fun c ->
                        if isNull c.ReferencedIdentity then None
                        else Some c.ReferencedIdentity))
                | CreateView(view, sources, _, _, _, _, _) ->
                    view :: (sources |> List.map (fun item -> item.Relation))
                | DropView(view, _, _) -> [view]
                | DropViewBatch(views, _, _) -> views
                | AddConstraint(name, constraintInfo) ->
                    [name] @
                    (if isNull constraintInfo.ReferencedIdentity then []
                     else [constraintInfo.ReferencedIdentity])
                | Drop(name, _)
                | AlterColumns(name, _)
                | RenameColumn(name, _, _)
                | RenameConstraint(name, _, _)
                | CreateIndex(name, _, _, _)
                | DropConstraint(name, _, _, _) -> [name]
                | DropIndex(_, target, _) -> Option.toList target
                | DropQualifiedIndex(indexIdentity, _) -> [indexIdentity]
                | DropIndexBatch(identities, _) -> identities
                | DropTableBatch(names, _, _) -> names
            | _ -> []
        let rawUnqualified =
            rawNames |> List.exists (fun n -> n.Parts.Count = 1)
        let rawSystemName =
            rawNames |> List.exists (fun n ->
                n.Parts.Count = 1 &&
                n.Parts[0].StartsWith("pg_", StringComparison.OrdinalIgnoreCase))
        let parsed =
            match parsed with
            | Recognized operation -> Recognized(resolvedOperation operation)
            | other -> other

        let referencedTableName = function
            | Create(name, _, _, _)
            | Recreate(name, _, _)
            | CreateView(name, _, _, _, _, _, _)
            | DropView(name, _, _)
            | Drop(name, _)
            | AlterColumns(name, _)
            | RenameColumn(name, _, _)
            | RenameConstraint(name, _, _)
            | CreateIndex(name, _, _, _)
            | AddConstraint(name, _)
            | DropConstraint(name, _, _, _) -> Some name
            | DropIndex(_, target, _) -> target
            | DropQualifiedIndex _
            | DropIndexBatch _
            | DropViewBatch _
            | DropTableBatch _ -> None
        // Indexes in PostgreSQL belong to their parent table's schema.
        // A caller-supplied one-part Catalog identity has no proven
        // physical schema: it can therefore collide with *any* explicit
        // schema and must never be filtered out by a qualified target.
        let possiblySameIndexSchema (left: SqlCatalogQualifiedName)
                                    (right: SqlCatalogQualifiedName) =
            sourceDialect <> SqlAgentToolType.Postgres
            || left.Parts.Count <> 2
            || right.Parts.Count <> 2
            || String.Equals(left.Parts[0], right.Parts[0], StringComparison.Ordinal)
        let ambiguousIndexSchema (left: SqlCatalogQualifiedName)
                                 (right: SqlCatalogQualifiedName) =
            // Keep existing conservative equality among two unqualified
            // Catalog owners; mixing an explicit and an unqualified
            // owner cannot establish a definite physical schema match.
            sourceDialect = SqlAgentToolType.Postgres
            && ((left.Parts.Count = 2) <> (right.Parts.Count = 2))
        let relationName (name: SqlCatalogQualifiedName) =
            name.Parts[name.Parts.Count - 1]
        match parsed with
        | Recognized (DropTableBatch _ | DropViewBatch _) when
            rawUnqualified &&
            not (match scope with
                 | RewriteCatalogSchemaResolution.SingleSchema _ ->
                     RewriteCatalogSchemaResolution.canProject scope state
                 | _ -> false) ->
            dependencyUnproven "Batch DROP TABLE requires a proven single search_path for unqualified members."
        | Recognized operation when
            unsupportedDeclaredScope && rawUnqualified ->
            dependencyUnproven "Declared PostgreSQL SEARCH_PATH is outside the supported single ordinary ASCII schema proof."
        | Recognized operation when
            rawSystemName &&
            (match scope with
             | RewriteCatalogSchemaResolution.SingleSchema _ -> true
             | _ -> false) ->
            dependencyUnproven "PostgreSQL pg_catalog/pg_temp implicit name resolution is not proven by a single user schema."
        | Recognized operation when
            (match scope with
             | RewriteCatalogSchemaResolution.SingleSchema _
             | RewriteCatalogSchemaResolution.SingleDatabase _ ->
                 not (RewriteCatalogSchemaResolution.canProject scope state)
                 && (referencedTableName operation
                     |> Option.exists (fun n -> n.Parts.Count = 2))
             | _ -> false) ->
            dependencyUnproven "Declared schema cannot be applied to a Catalog containing unqualified or system-schema identities."
        | Recognized operation when
            referencedTableName operation
            |> Option.exists (RewriteCatalogModel.hasUnqualifiedScopeConflict state) ->
            dependencyUnproven
                "Default schema/search_path is not modeled; unqualified table name may resolve to an existing qualified table."
        | Recognized _ when
            RewriteCatalogIdentifierProof.mode sourceDialect sourceProfile =
                RewriteCatalogIdentifierProof.FoldAscii
            && not (RewriteCatalogIdentifierProof.isAsciiSql syntax.Text) ->
            dependencyUnproven "Identifier names outside the ASCII-safe provider proof subset cannot establish a known DDL Catalog."
        | Invalid message -> fail message
        | Unsupported ->
            let unknown = RewriteCatalogModel.taintAll state
            let error =
                diagnostic syntax "SQL_SCHEMA_UNSUPPORTED_STATEMENT"
                    SqlDiagnosticCategory.Capability
                    "Source dialect DDL is outside the statically proven TABLE subset; catalog facts are invalidated."
            SqlStatementAnalysisStatus.Unsupported, SqlCatalogKnowledge.Unknown,
            unknown, [changed syntax "*" SqlCatalogOperation.Indeterminate SqlCatalogKnowledge.Unknown], [error]
        | Recognized (Recreate(name, columns, constraints)) ->
            // Firebird RECREATE must not be modeled as two independent
            // statements: all preconditions are checked over the existing
            // immutable Catalog before publishing one replacement state.
            if sourceDialect <> SqlAgentToolType.Firebird
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun t ->
                   t.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Firebird RECREATE TABLE requires a complete known Catalog."
            elif RewriteCatalogModel.lookupView state name |> Option.isSome then
                fail ("RECREATE TABLE " + name.DisplayName +
                      " names an existing View, not a table.")
            else
                let present = RewriteCatalogModel.lookup state name
                let blockers =
                    if present.IsNone then RewriteSchemaDependencies.Proven
                    elif not (RewriteCatalogModel.dependentViews
                                  state (Set.singleton name.Key)).IsEmpty then
                        RewriteSchemaDependencies.Invalid
                            ("RECREATE TABLE " + name.DisplayName +
                             " is blocked by dependent Views.")
                    else
                        RewriteSchemaDependencies.canDropTable state name
                match blockers with
                | RewriteSchemaDependencies.Invalid message -> fail message
                | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
                | RewriteSchemaDependencies.Proven ->
                    // Validate the new definition against the staged
                    // dropped table, so stale key/index facts never survive.
                    let withoutOld = RewriteCatalogModel.dropKnown state name
                    match RewriteSchemaDependencies.validateCreate
                              sourceDialect sourceProfile withoutOld name columns constraints with
                    | RewriteSchemaDependencies.Invalid message -> fail message
                    | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
                    | RewriteSchemaDependencies.Proven ->
                        let next =
                            RewriteCatalogModel.knownTableWithConstraints
                                withoutOld name columns constraints
                        let dropped =
                            if present.IsSome then
                                [changedExact syntax name SqlCatalogOperation.DropTable
                                    SqlCatalogKnowledge.Known]
                            else []
                        let created =
                            changedExact syntax name SqlCatalogOperation.CreateTable
                                SqlCatalogKnowledge.Known
                        SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                        next, dropped @ [created], ([]: SqlDiagnostic list)
        | Recognized (Create(name, _, _, ifNotExists)) when
            RewriteCatalogModel.lookupView state name |> Option.isSome ->
            // PostgreSQL Views and tables are relation-kind siblings.
            if ifNotExists then noChange
            else fail ("CREATE TABLE " + name.DisplayName +
                       " conflicts with a known View relation.")
        | Recognized (Create(name, columns, constraints, ifNotExists)) ->
            // PostgreSQL tables and indexes share the schema-local
            // relation namespace. A virtual Catalog that knows an index
            // with this name cannot simultaneously prove a new table.
            // Unknown owner schema requires an Unproven result, not a
            // confident declaration of a conflicting physical relation.
            let existingIndexOwner =
                if sourceDialect = SqlAgentToolType.Postgres then
                    state.Tables
                    |> Seq.tryFind (fun owner ->
                        possiblySameIndexSchema name owner.QualifiedName
                        && (owner.Indexes |> Seq.exists (fun idx ->
                            String.Equals(idx.Name, relationName name, StringComparison.Ordinal))))
                else None
            match existingIndexOwner with
            | Some owner when ambiguousIndexSchema name owner.QualifiedName ->
                dependencyUnproven "Index owner schema is not proven; CREATE TABLE may collide with an existing index relation."
            | Some _ when ifNotExists ->
                // PostgreSQL IF NOT EXISTS skips creation if *any*
                // relation (not only a table) has the requested name.
                noChange
            | Some _ ->
                fail ("CREATE TABLE " + name.DisplayName + " conflicts with a known index relation in its schema.")
            | None ->
                match RewriteCatalogModel.lookup state name with
                | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known && ifNotExists ->
                    noChange
                | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known ->
                    fail ("CREATE TABLE " + name.DisplayName + " already exists in the known catalog.")
                | _ when RewriteCatalogModel.isKnownAbsent state name ->
                    match RewriteSchemaDependencies.validateCreate sourceDialect sourceProfile state name columns constraints with
                    | RewriteSchemaDependencies.Proven ->
                        RewriteCatalogModel.knownTableWithConstraints state name columns constraints
                        |> exact name SqlCatalogOperation.CreateTable
                    | RewriteSchemaDependencies.Invalid message -> fail message
                    | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
                | _ -> partial name SqlCatalogOperation.CreateTable
        | Recognized (CreateView(view, sources, projection, joins, predicate,
                                 mode, outputHeader)) ->
            // The whole relational graph must be structurally proven before
            // creating a Known View. No join syntax is an execution permit.
            let existingView = RewriteCatalogModel.lookupView state view
            if sourceDialect <> SqlAgentToolType.Postgres
               || state.Knowledge <> SqlCatalogKnowledge.Known then
                dependencyUnproven "CREATE VIEW requires a complete PostgreSQL Catalog."
            elif view.Parts.Count <> 2
                 || (sources |> List.exists (fun s -> s.Relation.Parts.Count <> 2)) then
                dependencyUnproven "CREATE VIEW requires proven physical schemas for every source."
            elif RewriteCatalogModel.lookup state view |> Option.isSome
                 || (mode = CreateOnly && existingView.IsSome) then
                fail ("CREATE VIEW " + view.DisplayName + " conflicts with an existing relation.")
            elif state.Tables |> Seq.exists (fun table ->
                possiblySameIndexSchema view table.QualifiedName
                && ((table.Indexes |> Seq.exists (fun idx ->
                    String.Equals(idx.Name, relationName view, StringComparison.Ordinal)))
                    || (table.Constraints |> Seq.exists (fun info ->
                        (info.Kind = SqlCatalogConstraintKind.PrimaryKey
                         || info.Kind = SqlCatalogConstraintKind.Unique)
                        && not (String.IsNullOrWhiteSpace info.Name)
                        && String.Equals(info.Name, relationName view,
                                         StringComparison.Ordinal))))) then
                fail ("CREATE VIEW " + view.DisplayName +
                      " conflicts with a known index or key-backed index.")
            elif sources.IsEmpty || sources.Length > 4
                 || joins.Length <> sources.Length - 1
                 || (joins |> List.exists (fun edge ->
                     match edge.Condition with
                     | OnEquality pairs -> pairs.IsEmpty || pairs.Length > 8
                     | OnBoolean predicate ->
                         let comparisons = viewOnLeaves predicate
                         comparisons.IsEmpty || comparisons.Length > 8
                     | UsingKeys keys -> keys.IsEmpty || keys.Length > 8)) then
                dependencyUnproven "CREATE VIEW graph is outside bounded JOIN capabilities."
            else
                let bindings =
                    sources |> List.map (fun s ->
                        let columns =
                            match RewriteCatalogModel.lookup state s.Relation with
                            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                                Some(table.Columns |> Seq.toList)
                            | _ ->
                                match RewriteCatalogModel.lookupView state s.Relation with
                                | Some prior -> Some(prior.Columns |> Seq.toList)
                                | None -> None
                        let alias =
                            defaultArg s.Alias (relationName s.Relation)
                        s.Relation, alias, columns)
                if bindings |> List.exists (fun (_, _, cols) -> cols.IsNone) then
                    fail "CREATE VIEW references a missing or unproven source relation."
                elif bindings |> List.map (fun (_, alias, _) -> alias)
                     |> List.distinct |> List.length <> bindings.Length then
                    fail "CREATE VIEW has duplicate effective FROM aliases."
                else
                    let proven =
                        bindings |> List.choose (fun (id, alias, cols) ->
                            Option.map (fun x -> id, alias, x) cols)
                    // A closed JOIN-kind proof determines the physical
                    // value of a merged USING key. LEFT uses the left key,
                    // RIGHT the right key, FULL is COALESCE(left,right).
                    // Qualified key references always resolve to their
                    // physical owner and receive independent null extension.
                    let usingJoin =
                        match joins with
                        | [{ Kind = kind; Condition = UsingKeys keys }]
                            when sources.Length = 2 -> Some(kind, keys)
                        | _ -> None
                    let mergedUsingKeys =
                        match usingJoin with
                        | Some(_, keys) -> Set.ofList keys
                        | None -> Set.empty
                    let mergedKeyProjection name =
                        match proven, usingJoin with
                        | [(leftId, _, leftCols); (rightId, _, rightCols)],
                          Some(kind, keys) when List.contains name keys ->
                            let lookup (cols: SqlCatalogColumn list) =
                                cols |> List.tryFind (fun col ->
                                    String.Equals(col.Name, name, StringComparison.Ordinal))
                            match lookup leftCols, lookup rightCols with
                            | Some leftCol, Some rightCol when
                                String.Equals(leftCol.TypeName, rightCol.TypeName,
                                              StringComparison.Ordinal) ->
                                let nullable, owner =
                                    match kind with
                                    | InnerJoin | LeftOuterJoin ->
                                        leftCol.Nullable, leftId
                                    | RightOuterJoin ->
                                        rightCol.Nullable, rightId
                                    | FullOuterJoin ->
                                        // Unmatched rows from EITHER side
                                        // survive. COALESCE is non-null only
                                        // if both source keys are non-null.
                                        (leftCol.Nullable || rightCol.Nullable), leftId
                                Some(SqlCatalogColumn(name, leftCol.TypeName, nullable),
                                     (owner, name))
                            | _ -> None
                        | _ -> None
                    let resolveColumn qualifier name =
                        let matches =
                            proven |> List.collect (fun (identity, alias, columns) ->
                                if qualifier |> Option.exists (fun q ->
                                    not (String.Equals(q, alias, StringComparison.Ordinal))) then
                                    []
                                else
                                    columns |> List.filter (fun col ->
                                        String.Equals(col.Name, name, StringComparison.Ordinal))
                                    |> List.map (fun col -> identity, col))
                        match matches with
                        | [only] -> Some only
                        | [(leftId, leftCol); (rightId, rightCol)]
                            when qualifier.IsNone
                                 && Set.contains name mergedUsingKeys
                                 && String.Equals(leftCol.TypeName, rightCol.TypeName,
                                                  StringComparison.Ordinal) ->
                            // Used for WHERE type binding; final projection
                            // nullability is computed by mergedKeyProjection.
                            // JOIN references below retain both key owners.
                            match usingJoin with
                            | Some(RightOuterJoin, _) -> Some(rightId, rightCol)
                            | Some _ -> Some(leftId, leftCol)
                            | None -> None
                        | _ -> None
                    // Nullability is a fold over the left-deep JOIN
                    // graph. RIGHT null-extends *all* prior inputs; FULL
                    // extends the prior graph and the newly bound source.
                    // Previously extended columns remain conservative
                    // even if a subsequent INNER ON happens to reject nulls.
                    let nullableSources =
                        let mutable extended = Set.empty<string>
                        for index in 0 .. joins.Length - 1 do
                            RewriteResourceBudget.checkpoint ()
                            let edge = joins.[index]
                            let right = sources.[index + 1].Relation.Key
                            match edge.Kind with
                            | InnerJoin -> ()
                            | LeftOuterJoin ->
                                extended <- Set.add right extended
                            | RightOuterJoin ->
                                for prior in sources |> List.take (index + 1) do
                                    extended <- Set.add prior.Relation.Key extended
                            | FullOuterJoin ->
                                extended <- Set.add right extended
                                for prior in sources |> List.take (index + 1) do
                                    extended <- Set.add prior.Relation.Key extended
                        extended
                    let nullExtended (identity: SqlCatalogQualifiedName) =
                        Set.contains identity.Key nullableSources
                    let isSelfJoin =
                        (proven |> List.map (fun (id, _, _) -> id.Key)
                         |> List.distinct |> List.length) <> proven.Length
                    let instanceGraph =
                        if isSelfJoin then proveInstanceJoinGraph proven joins
                        else None
                    let bindInstance qualifier name =
                        match instanceGraph with
                        | None -> None
                        | Some(fields, _, nullableIndices) ->
                            match qualifier with
                            | None ->
                                match fields |> List.filter (fun f ->
                                    String.Equals(f.Output.Name, name,
                                                  StringComparison.Ordinal)) with
                                | [f] ->
                                    let identity, _, _ = proven.[f.SourceIndex]
                                    Some(f.Output, (identity, f.PhysicalName))
                                | _ -> None
                            | Some alias ->
                                let matches =
                                    proven |> List.mapi (fun index (identity, nodeAlias, cols) ->
                                        if not (String.Equals(alias, nodeAlias,
                                                              StringComparison.Ordinal))
                                        then []
                                        else
                                            cols |> List.filter (fun col ->
                                                String.Equals(col.Name, name,
                                                              StringComparison.Ordinal))
                                            |> List.map (fun col ->
                                                let nullable =
                                                    col.Nullable
                                                    || Set.contains index nullableIndices
                                                SqlCatalogColumn(
                                                    col.Name, col.TypeName, nullable),
                                                (identity, col.Name)))
                                    |> List.concat
                                match matches with
                                | [only] -> Some only
                                | _ -> None
                    let hasMultiUsing =
                        joins.Length > 1
                        && (joins |> List.exists (fun edge ->
                            match edge.Condition with
                            | UsingKeys _ -> true
                            | OnEquality _ | OnBoolean _ -> false))
                    let multiUsingGraph =
                        if hasMultiUsing && not isSelfJoin then
                            proveMultiUsingGraph proven joins
                        else None
                    let projected =
                        match projection with
                        | Wildcard None when isSelfJoin ->
                            instanceGraph |> Option.map (fun (fields, _, _) ->
                                fields |> List.map (fun f -> f.Output),
                                fields |> List.map (fun f ->
                                    let identity, _, _ = proven.[f.SourceIndex]
                                    identity, f.PhysicalName))
                        | Wildcard(Some alias) when isSelfJoin ->
                            match instanceGraph with
                            | None -> None
                            | Some(_, _, nullableIndices) ->
                                let matches =
                                    proven |> List.mapi (fun index (identity, nodeAlias, cols) ->
                                        if not (String.Equals(nodeAlias, alias,
                                                              StringComparison.Ordinal))
                                        then []
                                        else
                                            cols |> List.map (fun col ->
                                                SqlCatalogColumn(
                                                    col.Name, col.TypeName,
                                                    col.Nullable
                                                    || Set.contains index nullableIndices),
                                                (identity, col.Name)))
                                    |> List.concat
                                if matches.IsEmpty then None
                                else Some(matches |> List.map fst, matches |> List.map snd)
                        | NamedColumns members when isSelfJoin ->
                            let resolved =
                                members |> List.choose (fun memberInfo ->
                                    bindInstance memberInfo.Qualifier memberInfo.SourceColumn
                                    |> Option.map (fun (col, owner) ->
                                        SqlCatalogColumn(
                                            memberInfo.OutputColumn, col.TypeName, col.Nullable),
                                        owner))
                            if resolved.Length <> members.Length then None
                            else Some(resolved |> List.map fst, resolved |> List.map snd)
                        | Wildcard None when hasMultiUsing ->
                            multiUsingGraph |> Option.map (fun (fields, _) ->
                                fields |> List.map (fun field -> field.Output),
                                fields |> List.map (fun field ->
                                    field.Owner, field.PhysicalName))
                        | Wildcard None when not (Set.isEmpty mergedUsingKeys) ->
                            // PostgreSQL USING exposes keys once in USING
                            // order, then left nonkeys and right nonkeys in
                            // their physical order. Generic concatenation is
                            // wrong here. Both key owners remain in joinRefs.
                            match proven, joins with
                            | [(leftId, _, leftCols); (rightId, _, rightCols)],
                              [{ Condition = UsingKeys keys }] ->
                                let isKey (col: SqlCatalogColumn) =
                                    Set.contains col.Name mergedUsingKeys
                                let keyColumns = keys |> List.choose mergedKeyProjection
                                if keyColumns.Length <> keys.Length then None
                                else
                                    let remainingLeft =
                                        leftCols |> List.filter (isKey >> not)
                                        |> List.map (fun col ->
                                            SqlCatalogColumn(
                                                col.Name, col.TypeName,
                                                col.Nullable || nullExtended leftId),
                                            (leftId, col.Name))
                                    let remainingRight =
                                        rightCols |> List.filter (isKey >> not)
                                        |> List.map (fun col ->
                                            SqlCatalogColumn(
                                                col.Name, col.TypeName,
                                                col.Nullable || nullExtended rightId),
                                            (rightId, col.Name))
                                    let expanded =
                                        keyColumns @ remainingLeft @ remainingRight
                                    Some(expanded |> List.map fst,
                                         expanded |> List.map snd)
                            | _ -> None
                        | Wildcard qualifier ->
                            let candidates =
                                proven |> List.filter (fun (_, alias, _) ->
                                    qualifier |> Option.forall (fun q ->
                                        String.Equals(q, alias, StringComparison.Ordinal)))
                            if candidates.IsEmpty || (qualifier.IsSome && candidates.Length <> 1) then None
                            else
                                let expanded =
                                    candidates |> List.collect (fun (identity, _, columns) ->
                                        columns |> List.map (fun col ->
                                            SqlCatalogColumn(col.Name, col.TypeName,
                                                             col.Nullable || nullExtended identity),
                                            (identity, col.Name)))
                                Some(expanded |> List.map fst, expanded |> List.map snd)
                        | NamedColumns members ->
                            let found =
                                members |> List.choose (fun memberInfo ->
                                    if hasMultiUsing && memberInfo.Qualifier.IsNone then
                                        match multiUsingGraph with
                                        | None -> None
                                        | Some(fields, _) ->
                                            match fields |> List.filter (fun field ->
                                                String.Equals(
                                                    field.Output.Name,
                                                    memberInfo.SourceColumn,
                                                    StringComparison.Ordinal)) with
                                            | [field] ->
                                                Some(SqlCatalogColumn(
                                                    memberInfo.OutputColumn,
                                                    field.Output.TypeName,
                                                    field.Output.Nullable),
                                                     (field.Owner, field.PhysicalName))
                                            | _ -> None
                                    elif memberInfo.Qualifier.IsNone
                                         && Set.contains memberInfo.SourceColumn mergedUsingKeys then
                                        // Unqualified merged key is not the
                                        // same value as a qualified a.key or
                                        // b.key under RIGHT / FULL OUTER JOIN.
                                        mergedKeyProjection memberInfo.SourceColumn
                                        |> Option.map (fun (col, owner) ->
                                            SqlCatalogColumn(
                                                memberInfo.OutputColumn, col.TypeName,
                                                col.Nullable),
                                            owner)
                                    else
                                        resolveColumn memberInfo.Qualifier memberInfo.SourceColumn
                                        |> Option.map (fun (identity, col) ->
                                            SqlCatalogColumn(
                                                memberInfo.OutputColumn, col.TypeName,
                                                col.Nullable || nullExtended identity),
                                            (identity, col.Name)))
                            if found.Length <> members.Length then None
                            else Some(found |> List.map fst, found |> List.map snd)
                    // Every ON comparison must resolve inside this stage.
                    // Pure local ON predicates are valid PostgreSQL SQL;
                    // unlike WHERE, they retain unmatched OUTER JOIN rows.
                    // No arbitrary expression, unresolved alias, or
                    // unproven type is allowed to mint Catalog evidence.
                    let legacyJoinRefs =
                        let proofs =
                            joins |> List.mapi (fun index edge ->
                                let right = sources.[index + 1].Relation.Key
                                let prior =
                                    sources |> List.take (index + 1)
                                    |> List.map (fun item -> item.Relation.Key)
                                    |> Set.ofList
                                match edge.Condition with
                                | OnEquality _ | OnBoolean _ ->
                                    let pairs = viewOnCriteriaLeaves edge.Condition
                                    let atStage (id: SqlCatalogQualifiedName) =
                                        Set.contains id.Key prior
                                        || String.Equals(id.Key, right, StringComparison.Ordinal)
                                    let comparisons =
                                        pairs |> List.map (fun (left, operator, rightOperand) ->
                                            RewriteResourceBudget.checkpoint ()
                                            match resolveColumn left.Qualifier left.SourceColumn,
                                                  resolveColumn rightOperand.Qualifier rightOperand.SourceColumn with
                                            | Some(leftId, leftCol), Some(rightId, rightCol)
                                                when atStage leftId && atStage rightId
                                                     && String.Equals(
                                                        leftCol.TypeName,
                                                        rightCol.TypeName,
                                                        StringComparison.Ordinal)
                                                     && provenOnComparison operator leftCol.TypeName ->
                                                Some [leftId, leftCol.Name;
                                                      rightId, rightCol.Name]
                                            | _ -> None)
                                    let fullJoinAnchor =
                                        edge.Kind <> FullOuterJoin
                                        || hasFullJoinEquiAnchor edge.Condition (fun leftOperand rightOperand ->
                                            match resolveColumn leftOperand.Qualifier leftOperand.SourceColumn,
                                                  resolveColumn rightOperand.Qualifier rightOperand.SourceColumn with
                                            | Some(leftId, leftCol), Some(rightId, rightCol) ->
                                                let crossed =
                                                    (String.Equals(leftId.Key, right,
                                                                   StringComparison.Ordinal)
                                                     && Set.contains rightId.Key prior)
                                                    || (String.Equals(rightId.Key, right,
                                                                      StringComparison.Ordinal)
                                                        && Set.contains leftId.Key prior)
                                                crossed
                                                && String.Equals(leftCol.TypeName,
                                                                 rightCol.TypeName,
                                                                 StringComparison.Ordinal)
                                                && provenOnComparison OrdinaryInequality
                                                    leftCol.TypeName
                                            | _ -> false)
                                    if comparisons.IsEmpty
                                       || (comparisons |> List.exists Option.isNone)
                                       || not fullJoinAnchor
                                    then [None]
                                    else
                                        [Some(comparisons |> List.choose id |> List.concat)]
                                | UsingKeys keys ->
                                    match proven with
                                    | [(leftId, _, leftCols); (rightId, _, rightCols)]
                                        when joins.Length = 1
                                             && (keys |> List.distinct |> List.length) = keys.Length ->
                                        keys |> List.map (fun key ->
                                            let lookup (cols: SqlCatalogColumn list) =
                                                cols |> List.tryFind (fun col ->
                                                    String.Equals(col.Name, key, StringComparison.Ordinal))
                                            match lookup leftCols, lookup rightCols with
                                            | Some leftCol, Some rightCol
                                                when String.Equals(leftCol.TypeName, rightCol.TypeName,
                                                                   StringComparison.Ordinal) ->
                                                Some [leftId, leftCol.Name; rightId, rightCol.Name]
                                            | _ -> None)
                                    | _ -> [None])
                            |> List.concat
                        if proofs |> List.exists Option.isNone then None
                        else Some(proofs |> List.choose id |> List.concat)
                    let joinRefs =
                        if isSelfJoin then
                            instanceGraph |> Option.map (fun (_, refs, _) -> refs)
                        elif hasMultiUsing then multiUsingGraph |> Option.map snd
                        else legacyJoinRefs
                    let rec provePredicate expression =
                        let bind col =
                            if isSelfJoin then
                                bindInstance col.Qualifier col.SourceColumn
                                |> Option.map (fun (info, (identity, _)) -> identity, info)
                            elif hasMultiUsing && col.Qualifier.IsNone then
                                match multiUsingGraph with
                                | None -> None
                                | Some(fields, _) ->
                                    match fields |> List.filter (fun field ->
                                        String.Equals(
                                            field.Output.Name, col.SourceColumn,
                                            StringComparison.Ordinal)) with
                                    | [field] -> Some(field.Owner, field.Output)
                                    | _ -> None
                            else resolveColumn col.Qualifier col.SourceColumn
                        match expression with
                        | NullTest(col, _) ->
                            match bind col with
                            | Some(identity, info) -> Ok [identity, info.Name]
                            | None -> Error "unbound"
                        | Compare(col, op, literal) ->
                            match bind col with
                            | None -> Error "unbound"
                            | Some(identity, info) ->
                                let kind = info.TypeName.ToUpperInvariant()
                                let compatible =
                                    match literal with
                                    | IntegerValue _ ->
                                        List.contains kind
                                            ["SMALLINT"; "INTEGER"; "INT"; "BIGINT"]
                                    | StringValue _ ->
                                        kind = "TEXT"
                                        || kind.StartsWith("VARCHAR(", StringComparison.Ordinal)
                                        || kind.StartsWith("CHAR(", StringComparison.Ordinal)
                                    | BooleanValue _ -> kind = "BOOLEAN"
                                let supported =
                                    // Exhaustive operator proof: future comparison variants
                                    // must not silently inherit an execution-like capability.
                                    match literal, op with
                                    | _, (Equal | NotEqual | IsDistinctFrom | IsNotDistinctFrom) ->
                                        true
                                    | IntegerValue _, (Less | LessOrEqual | Greater | GreaterOrEqual) ->
                                        true
                                    | _ -> false
                                if compatible && supported then Ok [identity, info.Name]
                                else Error "type"
                        | Conjunction(left, right)
                        | Disjunction(left, right) ->
                            match provePredicate left, provePredicate right with
                            | Ok a, Ok b -> Ok(a @ b)
                            | Error "unbound", _
                            | _, Error "unbound" -> Error "unbound"
                            | _ -> Error "type"
                    let filterRefs =
                        match predicate with
                        | None -> Ok []
                        | Some expression -> provePredicate expression
                    match projected, joinRefs, filterRefs with
                    | None, _, _ ->
                        fail "CREATE VIEW projection contains an ambiguous or unbound source column."
                    | _, None, _ ->
                        fail "CREATE VIEW ON requires matching columns in two proven distinct sources with identical types."
                    | _, _, Error "unbound" ->
                        fail "CREATE VIEW WHERE refers to an unbound or ambiguous source column."
                    | _, _, Error _ ->
                        dependencyUnproven "CREATE VIEW WHERE literal/operator type compatibility is unproven."
                    | Some(shape, _), _, _ when shape.IsEmpty ->
                        fail "CREATE VIEW projection cannot have zero columns."
                    | Some(shape, projectedRefs), Some onRefs, Ok whereRefs ->
                        let visibleColumns =
                            match outputHeader with
                            | Inferred -> Some shape
                            | Explicit names when names.Length <= shape.Length ->
                                let specified = names |> List.toArray
                                Some(shape |> List.mapi (fun index col ->
                                    if index < specified.Length then
                                        SqlCatalogColumn(specified.[index], col.TypeName,
                                                         col.Nullable)
                                    else col))
                            | Explicit _ -> None
                        match visibleColumns with
                        | None ->
                            fail ("CREATE VIEW " + view.DisplayName +
                                  " column-name list cannot exceed the SELECT projection.")
                        | Some visibleShape when
                            (visibleShape |> List.distinctBy (fun c -> c.Name)
                             |> List.length) <> visibleShape.Length ->
                            fail ("CREATE VIEW " + view.DisplayName +
                                  " has duplicate output column names.")
                        | Some visibleShape ->
                            let compatiblePrefix (prior: SqlCatalogView) =
                                visibleShape.Length >= prior.Columns.Count
                                && (prior.Columns
                                    |> Seq.mapi (fun index existing ->
                                        let replacement = visibleShape.[index]
                                        String.Equals(existing.Name, replacement.Name, StringComparison.Ordinal)
                                        && String.Equals(existing.TypeName, replacement.TypeName,
                                                         StringComparison.Ordinal)
                                        )
                                    |> Seq.forall id)
                            let descendants =
                                if existingView.IsSome then
                                    RewriteCatalogModel.dependentViews state (Set.singleton view.Key)
                                    |> List.map (fun child -> child.QualifiedName.Key)
                                    |> Set.ofList
                                else Set.empty
                            let cycle =
                                sources |> List.exists (fun s ->
                                    String.Equals(s.Relation.Key, view.Key, StringComparison.Ordinal)
                                    || Set.contains s.Relation.Key descendants)
                            if cycle then
                                fail ("CREATE OR REPLACE VIEW " + view.DisplayName +
                                      " would introduce a View dependency cycle.")
                            elif existingView |> Option.exists (compatiblePrefix >> not) then
                                fail ("CREATE OR REPLACE VIEW " + view.DisplayName +
                                      " must preserve existing output column names, order and types.")
                            elif existingView |> Option.exists (fun prior ->
                                not (Set.isEmpty descendants)
                                && (prior.Columns
                                    |> Seq.mapi (fun index original ->
                                        original.Nullable <> visibleShape.[index].Nullable)
                                    |> Seq.exists id)) then
                                // PostgreSQL permits an unchanged output
                                // name/type across INNER-to-LEFT replacement,
                                // but downstream immutable Catalog Views would
                                // otherwise retain an unsound NOT NULL proof.
                                fail ("CREATE OR REPLACE VIEW " + view.DisplayName +
                                      " changes output nullability with dependent Views.")
                            else
                                // Preserve projection multiplicity, dedupe
                                // only additional ON/WHERE dependency names.
                                let exactRefs = projectedRefs @ onRefs @ whereRefs
                                let owned =
                                    proven |> List.distinctBy (fun (identity, _, _) ->
                                        identity.Key)
                                    |> List.map (fun (identity, _, _) ->
                                        let names =
                                            exactRefs |> List.choose (fun (refId, name) ->
                                                if String.Equals(identity.Key, refId.Key, StringComparison.Ordinal)
                                                then Some name else None)
                                        let projectionCount =
                                            projectedRefs |> List.filter (fun (refId, _) ->
                                                String.Equals(identity.Key, refId.Key, StringComparison.Ordinal))
                                            |> List.length
                                        let direct = names |> List.take projectionCount
                                        let extra =
                                            names |> List.skip projectionCount
                                            |> List.distinct
                                            |> List.filter (fun name -> not (List.contains name direct))
                                        SqlCatalogViewSourceBinding(
                                            identity, Array.AsReadOnly(List.toArray (direct @ extra))))
                                let operation =
                                    if existingView.IsSome then SqlCatalogOperation.ReplaceView
                                    else SqlCatalogOperation.CreateView
                                RewriteCatalogModel.withViewSources state view visibleShape owned
                                |> exact view operation
        | Recognized (DropViewBatch(views, conditional, mode)) ->
            // PostgreSQL multi-view DROP validates the complete set using
            // the ORIGINAL immutable Catalog. Treat a batch-internal view
            // dependency as satisfied under RESTRICT, but not an external one.
            let duplicate =
                (views |> List.distinctBy (fun name -> name.Key) |> List.length)
                <> List.length views
            let complete =
                sourceDialect = SqlAgentToolType.Postgres
                && state.Knowledge = SqlCatalogKnowledge.Known
            let missing =
                views |> List.filter (fun name ->
                    (RewriteCatalogModel.lookupView state name).IsNone)
            let wrongTable =
                views |> List.tryFind (fun name ->
                    RewriteCatalogModel.lookup state name |> Option.isSome)
            let knownIndex =
                missing |> List.tryFind (fun name ->
                    state.Tables |> Seq.exists (fun owner ->
                        possiblySameIndexSchema name owner.QualifiedName
                        && owner.QualifiedName.Parts.Count = 2
                        && (owner.Indexes |> Seq.exists (fun idx ->
                            String.Equals(idx.Name, relationName name,
                                          StringComparison.Ordinal))
                            || (owner.Constraints |> Seq.exists (fun key ->
                                (key.Kind = SqlCatalogConstraintKind.PrimaryKey
                                 || key.Kind = SqlCatalogConstraintKind.Unique)
                                && not (String.IsNullOrWhiteSpace key.Name)
                                && String.Equals(key.Name, relationName name,
                                                 StringComparison.Ordinal))))))
            let potentialImplicit =
                missing |> List.exists (fun name ->
                    state.Tables |> Seq.exists (fun owner ->
                        possiblySameIndexSchema name owner.QualifiedName
                        && (owner.Constraints |> Seq.exists (fun key ->
                            key.Kind = SqlCatalogConstraintKind.PrimaryKey
                            || key.Kind = SqlCatalogConstraintKind.Unique))))
            let unknownOwner =
                views |> List.exists (fun name ->
                    name.Parts.Count <> 2
                    || (state.Views |> Seq.exists (fun owner ->
                        owner.QualifiedName.Parts.Count = 1
                        && String.Equals(relationName owner.QualifiedName,
                                         relationName name, StringComparison.Ordinal)))
                    || (state.Tables |> Seq.exists (fun owner ->
                        owner.QualifiedName.Parts.Count = 1
                        && (String.Equals(relationName owner.QualifiedName,
                                          relationName name, StringComparison.Ordinal)
                            || (owner.Indexes |> Seq.exists (fun idx ->
                                String.Equals(idx.Name, relationName name,
                                              StringComparison.Ordinal)))))))
            if not complete then
                dependencyUnproven "Multi-view DROP requires a complete known PostgreSQL Catalog."
            elif duplicate then
                fail "DROP VIEW batch contains duplicate physical view identities."
            elif unknownOwner then
                dependencyUnproven "Multi-view DROP requires proven schema ownership for all relation names."
            elif wrongTable.IsSome then
                fail ("DROP VIEW batch refers to a known table, not a View: " +
                      (Option.get wrongTable).DisplayName)
            elif knownIndex.IsSome then
                fail ("DROP VIEW batch refers to a known index, not a View: " +
                      (Option.get knownIndex).DisplayName)
            elif potentialImplicit then
                dependencyUnproven "Unmodeled implicit key indexes prevent a proven batch View absence."
            elif not conditional && not missing.IsEmpty then
                fail ("DROP VIEW " + missing.Head.DisplayName + " does not exist.")
            else
                let requested =
                    views |> List.choose (fun name ->
                        RewriteCatalogModel.lookupView state name)
                let requestedKeys =
                    requested |> List.map (fun view -> view.QualifiedName.Key)
                    |> Set.ofList
                let closure =
                    RewriteCatalogModel.dependentViews state requestedKeys
                    |> List.filter (fun dependent ->
                        not (Set.contains dependent.QualifiedName.Key requestedKeys))
                if mode = Restrict && not closure.IsEmpty then
                    fail "DROP VIEW batch has external dependent Views under RESTRICT."
                else
                    let removal =
                        requested @ (if mode = Cascade then closure else [])
                    let next =
                        removal |> List.fold (fun catalog view ->
                            RewriteCatalogModel.dropView catalog view.QualifiedName) state
                    let deltas =
                        removal |> List.map (fun view ->
                            changedExact syntax view.QualifiedName
                                SqlCatalogOperation.DropView SqlCatalogKnowledge.Known)
                    SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                    next, deltas, ([]: SqlDiagnostic list)
        | Recognized (DropView(view, conditional, mode)) ->
            if sourceDialect <> SqlAgentToolType.Postgres
               || state.Knowledge <> SqlCatalogKnowledge.Known then
                dependencyUnproven "DROP VIEW requires a complete PostgreSQL Catalog."
            elif view.Parts.Count <> 2 then
                dependencyUnproven "DROP VIEW requires a proven physical schema."
            elif RewriteCatalogModel.lookup state view |> Option.isSome then
                fail ("DROP VIEW " + view.DisplayName + " refers to a known table.")
            elif state.Tables |> Seq.exists (fun owner ->
                possiblySameIndexSchema view owner.QualifiedName
                && ((owner.Indexes |> Seq.exists (fun index ->
                    String.Equals(index.Name, relationName view, StringComparison.Ordinal)))
                    || (owner.Constraints |> Seq.exists (fun constraintInfo ->
                        (constraintInfo.Kind = SqlCatalogConstraintKind.PrimaryKey
                         || constraintInfo.Kind = SqlCatalogConstraintKind.Unique)
                        && not (String.IsNullOrWhiteSpace constraintInfo.Name)
                        && String.Equals(constraintInfo.Name, relationName view,
                                         StringComparison.Ordinal))))) then
                fail ("DROP VIEW " + view.DisplayName + " refers to a known index.")
            elif (RewriteCatalogModel.lookupView state view |> Option.isNone)
                 && (state.Tables |> Seq.exists (fun owner ->
                     possiblySameIndexSchema view owner.QualifiedName
                     && (owner.Constraints |> Seq.exists (fun c ->
                         c.Kind = SqlCatalogConstraintKind.PrimaryKey
                         || c.Kind = SqlCatalogConstraintKind.Unique)))) then
                // Unknown names of implicit key-backed indexes may identify
                // a relation of the wrong kind. IF EXISTS cannot prove the
                // physical absence of this View name in that situation.
                dependencyUnproven "Implicit constraint-backed index names prevent a proven DROP VIEW absence."
            else
                match RewriteCatalogModel.lookupView state view with
                | None when conditional -> noChange
                | None -> fail ("DROP VIEW " + view.DisplayName + " does not exist.")
                | Some _ ->
                    let dependencies =
                        RewriteCatalogModel.dependentViews state (Set.singleton view.Key)
                    if mode = Restrict && not dependencies.IsEmpty then
                        fail ("DROP VIEW " + view.DisplayName +
                              " has dependent views under RESTRICT.")
                    else
                        let dropped =
                            view :: (dependencies |> List.map (fun d -> d.QualifiedName))
                        let next =
                            dropped |> List.fold (fun catalog name ->
                                RewriteCatalogModel.dropView catalog name) state
                        let changes =
                            dropped |> List.map (fun name ->
                                changedExact syntax name SqlCatalogOperation.DropView
                                    SqlCatalogKnowledge.Known)
                        SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                        next, changes, ([]: SqlDiagnostic list)
        | Recognized (DropTableBatch(names, conditional, mode)) ->
            // Batch names are fully parsed and scope-resolved before this
            // branch. Examine every target against the same ORIGINAL
            // Catalog; independent single-table DROP validations would
            // incorrectly block an FK between two tables dropped together.
            let distinctNames =
                names |> List.distinctBy (fun name -> name.Key)
            let duplicate = distinctNames.Length <> names.Length
            let ambiguousOwner =
                (state.Tables |> Seq.exists (fun owner ->
                    owner.QualifiedName.Parts.Count = 1
                    && (names |> List.exists (fun name ->
                        String.Equals(
                            relationName name,
                            relationName owner.QualifiedName,
                            StringComparison.Ordinal)))))
                || (state.Views |> Seq.exists (fun view ->
                    view.QualifiedName.Parts.Count = 1
                    && (names |> List.exists (fun name ->
                        String.Equals(relationName name,
                                      relationName view.QualifiedName, StringComparison.Ordinal)))))
            if sourceDialect <> SqlAgentToolType.Postgres
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun table ->
                   table.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Batch DROP TABLE requires a complete known PostgreSQL Catalog."
            elif ambiguousOwner then
                dependencyUnproven "Unqualified Catalog identities may shadow schema-qualified DROP TABLE targets."
            elif duplicate then
                fail "DROP TABLE batch contains duplicate physical table identities."
            else
                let existing =
                    names |> List.choose (fun name ->
                        match RewriteCatalogModel.lookup state name with
                        | Some _ -> Some name
                        | None -> None)
                let missing =
                    names |> List.tryFind (fun name ->
                        (RewriteCatalogModel.lookup state name).IsNone)
                let sameSchema (left: SqlCatalogQualifiedName)
                               (right: SqlCatalogQualifiedName) =
                    left.Parts.Count = 2
                    && right.Parts.Count = 2
                    && String.Equals(left.Parts[0], right.Parts[0], StringComparison.Ordinal)
                let wrongKind =
                    names |> List.tryFind (fun name ->
                        (RewriteCatalogModel.lookup state name).IsNone
                        && (RewriteCatalogModel.lookupView state name |> Option.isSome
                            || (state.Tables |> Seq.exists (fun owner ->
                            sameSchema name owner.QualifiedName
                            && (owner.Indexes |> Seq.exists (fun index ->
                                String.Equals(index.Name, relationName name,
                                              StringComparison.Ordinal)))))))
                // Build the entire inbound FK dependency graph from the
                // original immutable Catalog. Nothing is changed until all
                // table identities and external constraints are proven.
                let targets =
                    names |> List.map (fun name -> name.Key) |> Set.ofList
                let externalForeignKeys =
                    existing |> List.collect (fun target ->
                        RewriteCatalogModel.inboundForeignKeys state target
                        |> Seq.filter (fun (owner, _) ->
                            not (Set.contains owner.QualifiedName.Key targets))
                        |> Seq.toList)
                let unnamed =
                    externalForeignKeys
                    |> List.tryFind (fun (_, fk) ->
                        String.IsNullOrWhiteSpace fk.Name)
                let dependentViews =
                    RewriteCatalogModel.dependentViews state targets
                match wrongKind, missing, externalForeignKeys, unnamed with
                | Some name, _, _, _ ->
                    fail ("DROP TABLE " + name.DisplayName +
                          " names a tracked index relation, not a table.")
                | None, Some name, _, _ when not conditional ->
                    fail ("DROP TABLE " + name.DisplayName +
                          " does not exist in the known Catalog.")
                | None, _, _, _ when mode = Restrict && not dependentViews.IsEmpty ->
                    fail "DROP TABLE batch is blocked by dependent Views under RESTRICT."
                | None, _, (dependent, _)::_, _ when mode = Restrict ->
                    fail ("DROP TABLE batch is blocked by a FOREIGN KEY in non-batch table '" +
                          dependent.Name + "'.")
                | None, _, _, Some(dependent, _) ->
                    dependencyUnproven
                        ("CASCADE cannot prove the physical name of an unnamed FOREIGN KEY on '" +
                         dependent.Name + "'.")
                | _ ->
                    // CASCADE removes the specified tables and precisely
                    // the named FKs referencing them on surviving tables.
                    // Referencing tables and unrelated constraints survive.
                    let afterDrop =
                        existing |> List.fold (fun catalog name ->
                            RewriteCatalogModel.dropKnown catalog name) state
                    let next =
                        externalForeignKeys |> List.fold (fun catalog (owner, fk) ->
                            let current =
                                RewriteCatalogModel.lookup catalog owner.QualifiedName
                                |> Option.get
                            let retained =
                                current.Constraints |> Seq.filter (fun item ->
                                    not (item.Kind = SqlCatalogConstraintKind.ForeignKey
                                         && String.Equals(item.Name, fk.Name,
                                                          StringComparison.Ordinal)))
                            RewriteCatalogModel.withConstraints catalog owner.QualifiedName retained
                        ) afterDrop
                    let nextWithViews =
                        dependentViews |> List.fold (fun catalog view ->
                            RewriteCatalogModel.dropView catalog view.QualifiedName) next
                    let tableDeltas =
                        existing |> List.map (fun name ->
                            changedExact syntax name SqlCatalogOperation.DropTable
                                SqlCatalogKnowledge.Known)
                    let constraintDeltas =
                        externalForeignKeys |> List.map (fun (owner, fk) ->
                            changedConstraintExact syntax owner.QualifiedName fk.Name)
                    let viewDeltas =
                        dependentViews |> List.map (fun view ->
                            changedExact syntax view.QualifiedName SqlCatalogOperation.DropView
                                SqlCatalogKnowledge.Known)
                    SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                    nextWithViews, tableDeltas @ constraintDeltas @ viewDeltas,
                    ([]: SqlDiagnostic list)
        | Recognized (Drop(name, _)) when
            RewriteCatalogModel.lookupView state name |> Option.isSome ->
            fail ("DROP TABLE " + name.DisplayName + " names a known View, not a table.")
        | Recognized (Drop(name, ifExists)) ->
            match RewriteCatalogModel.lookup state name with
            | Some existing when existing.Knowledge = SqlCatalogKnowledge.Known ->
                let dependents =
                    RewriteCatalogModel.dependentViews state (Set.singleton name.Key)
                if not dependents.IsEmpty then
                    fail ("DROP TABLE " + name.DisplayName +
                          " is blocked by dependent Views under RESTRICT.")
                else
                    match RewriteSchemaDependencies.canDropTable state name with
                    | RewriteSchemaDependencies.Proven ->
                        RewriteCatalogModel.dropKnown state name
                        |> exact name SqlCatalogOperation.DropTable
                    | RewriteSchemaDependencies.Invalid message -> fail message
                    | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
            | _ when RewriteCatalogModel.isKnownAbsent state name && ifExists ->
                noChange
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                fail ("DROP TABLE " + name.DisplayName + " does not exist in the known catalog.")
            | _ -> partial name SqlCatalogOperation.DropTable
        | Recognized (CreateIndex(tableName, _, _, true))
            when sourceDialect = SqlAgentToolType.Sqlite
                 && (tableName.Parts.Count <> 1
                     || (state.Tables |> Seq.exists (fun table ->
                         table.QualifiedName.Parts.Count <> 1))
                     || (state.Views |> Seq.exists (fun view ->
                         view.QualifiedName.Parts.Count <> 1))) ->
            // Attached databases are not modeled as independent index namespaces.
            dependencyUnproven "SQLite conditional index requires a proven unqualified database namespace."
        | Recognized (CreateIndex(tableName, indexName, members, conditional)) ->
            match RewriteCatalogModel.lookup state tableName with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known
                              && state.IsClosedWorld ->
                // MySQL / SQL Server: owning table namespace.
                // PostgreSQL: parent table's schema-local relation namespace.
                // Unqualified PostgreSQL identities remain potentially in
                // every schema, so only two explicit *different* schemas may
                // be excluded from collision and implicit-index checks.
                // SQLite / Oracle / Firebird keep the original conservative
                // Catalog-wide namespace.
                let tableLocalNamespace =
                    sourceDialect = SqlAgentToolType.MySQL
                    || sourceDialect = SqlAgentToolType.MsSqlServer
                let indexNamespace : seq<SqlCatalogTable> =
                    if tableLocalNamespace then Seq.singleton table
                    elif sourceDialect = SqlAgentToolType.Postgres then
                        state.Tables
                        |> Seq.filter (fun owner ->
                            possiblySameIndexSchema tableName owner.QualifiedName)
                    else state.Tables :> seq<SqlCatalogTable>
                let indexOwner =
                    indexNamespace
                    |> Seq.tryFind (fun owner ->
                        owner.Indexes |> Seq.exists (fun idx ->
                            String.Equals(idx.Name, indexName, StringComparison.Ordinal)))
                // PostgreSQL indexes also conflict with ordinary tables
                // named the same within the target schema.
                let relationOwner =
                    if sourceDialect = SqlAgentToolType.Postgres
                       || sourceDialect = SqlAgentToolType.Sqlite then
                        indexNamespace |> Seq.tryFind (fun owner ->
                            String.Equals(relationName owner.QualifiedName, indexName,
                                          StringComparison.Ordinal))
                    else None
                let viewOwner =
                    if sourceDialect = SqlAgentToolType.Postgres
                       || sourceDialect = SqlAgentToolType.Sqlite then
                        state.Views |> Seq.tryFind (fun view ->
                            possiblySameIndexSchema tableName view.QualifiedName
                            && String.Equals(relationName view.QualifiedName, indexName,
                                             StringComparison.Ordinal))
                    else None
                match indexOwner, relationOwner, viewOwner with
                | Some owner, _, _ when ambiguousIndexSchema tableName owner.QualifiedName ->
                    dependencyUnproven "Index owner has no proven schema; index name collision cannot be resolved."
                | Some _, None, None
                    when conditional && sourceDialect = SqlAgentToolType.Sqlite ->
                    // SQLite IF NOT EXISTS checks the index-name namespace,
                    // not the declared indexed columns or their owning table.
                    // Lookup of a known target table has already succeeded.
                    noChange
                | Some _, _, _ ->
                    fail ("CREATE INDEX '" + indexName + "' already exists in the known Catalog.")
                | None, Some owner, _ when ambiguousIndexSchema tableName owner.QualifiedName ->
                    dependencyUnproven "Relation has no proven schema; index name may collide with an existing table."
                | None, Some _, _ ->
                    fail ("CREATE INDEX '" + indexName + "' conflicts with a known table relation in its schema.")
                | None, None, Some owner when ambiguousIndexSchema tableName owner.QualifiedName ->
                    dependencyUnproven "View schema is unproven; index name collision is possible."
                | None, None, Some _ ->
                    fail ("CREATE INDEX '" + indexName + "' conflicts with a known View relation.")
                | None, None, None when indexNamespace |> Seq.exists (fun t -> t.Constraints.Count > 0) ->
                    dependencyUnproven "Implicit constraint-backed index names are not modeled; index namespace uniqueness cannot be proven."
                | None, None, None ->
                    if members.IsEmpty || List.distinct members <> members then
                        fail "CREATE INDEX has duplicate or empty column members."
                    elif members |> List.exists (fun memberName ->
                        not (table.Columns |> Seq.exists (fun col ->
                            String.Equals(col.Name, memberName, StringComparison.Ordinal)))) then
                        fail "CREATE INDEX references an absent column."
                    else
                        let idx = SqlCatalogIndex(indexName, Array.AsReadOnly(List.toArray members))
                        let updated =
                            RewriteCatalogModel.withIndexes state tableName
                                (seq { yield! table.Indexes; yield idx })
                        exact tableName SqlCatalogOperation.CreateIndex updated
            | _ -> dependencyUnproven "Index target is not a proven table in a closed Catalog."
        | Recognized (DropIndexBatch(identities, conditional)) ->
            // This statement must never commit a virtual Catalog delta
            // before ALL requested index relations have been resolved.
            // Resolve structured identities first; compare fully qualified
            // physical keys, not dotted display-name strings.
            let resolved = identities |> List.map (resolve)
            let duplicate =
                (resolved |> List.distinctBy (fun identity -> identity.Key) |> List.length)
                <> List.length resolved
            if sourceDialect <> SqlAgentToolType.Postgres
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun table ->
                   table.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "A multi-index DROP requires a complete PostgreSQL Catalog."
            elif duplicate then
                fail "DROP INDEX batch contains duplicate index identities."
            else
                let prove (identity: SqlCatalogQualifiedName) =
                    let schemaAndName =
                        if identity.Parts.Count = 2 then
                            Some(identity.Parts[0], identity.Parts[1])
                        else
                            match scope with
                            | RewriteCatalogSchemaResolution.SingleSchema schema when
                                RewriteCatalogSchemaResolution.canProject scope state ->
                                Some(schema, identity.Parts[0])
                            | _ -> None
                    match schemaAndName with
                    | None ->
                        BatchIndexUnproven
                            "Multi-index DROP unqualified names require a proven single search_path."
                    | Some(schema, indexName) ->
                        let sameIndex (index: SqlCatalogIndex) =
                            String.Equals(index.Name, indexName, StringComparison.Ordinal)
                        let inSchema (owner: SqlCatalogTable) =
                            owner.QualifiedName.Parts.Count = 2
                            && String.Equals(owner.QualifiedName.Parts[0], schema,
                                             StringComparison.Ordinal)
                        let ambiguousOwner =
                            state.Tables |> Seq.exists (fun owner ->
                                owner.QualifiedName.Parts.Count = 1
                                && ((owner.Indexes |> Seq.exists sameIndex)
                                    || owner.Constraints.Count > 0
                                    || String.Equals(relationName owner.QualifiedName,
                                                     indexName, StringComparison.Ordinal)))
                        let possibleImplicit =
                            state.Tables |> Seq.exists (fun owner ->
                                inSchema owner && owner.Constraints.Count > 0)
                        let namedTable =
                            (state.Tables |> Seq.exists (fun owner ->
                                inSchema owner
                                && String.Equals(relationName owner.QualifiedName,
                                                 indexName, StringComparison.Ordinal)))
                            || (state.Views |> Seq.exists (fun view ->
                                view.QualifiedName.Parts.Count = 2
                                && String.Equals(view.QualifiedName.Parts[0], schema,
                                                 StringComparison.Ordinal)
                                && String.Equals(relationName view.QualifiedName, indexName,
                                                 StringComparison.Ordinal)))
                        let matchingOwners =
                            state.Tables |> Seq.filter (fun owner ->
                                inSchema owner && (owner.Indexes |> Seq.exists sameIndex))
                            |> Seq.toList
                        if ambiguousOwner then
                            BatchIndexUnproven
                                ("Index '" + identity.DisplayName +
                                 "' may belong to an unqualified Catalog owner.")
                        elif possibleImplicit then
                            BatchIndexUnproven
                                ("Implicit constraint-backed index identity '" +
                                 identity.DisplayName + "' cannot be proven.")
                        elif namedTable then
                            BatchIndexInvalid
                                ("DROP INDEX '" + identity.DisplayName +
                                 "' names a known table relation.")
                        else
                            match matchingOwners with
                            | [owner] -> BatchIndexFound(owner.QualifiedName, indexName)
                            | [] when conditional -> BatchIndexAbsent
                            | [] ->
                                BatchIndexUnproven
                                    ("DROP INDEX '" + identity.DisplayName +
                                     "' is not a proven standalone index.")
                            | _ ->
                                BatchIndexUnproven
                                    ("DROP INDEX '" + identity.DisplayName +
                                     "' resolves to multiple Catalog owners.")
                // All proofs are evaluated over the ORIGINAL immutable
                // Catalog, and no mutation occurs until all have succeeded.
                let proofs = identities |> List.map prove
                let invalid =
                    proofs |> List.tryPick (function
                        | BatchIndexInvalid message -> Some message
                        | _ -> None)
                let unproven =
                    proofs |> List.tryPick (function
                        | BatchIndexUnproven message -> Some message
                        | _ -> None)
                match invalid, unproven with
                | Some message, _ -> fail message
                | None, Some message -> dependencyUnproven message
                | None, None ->
                    let removals =
                        proofs |> List.choose (function
                            | BatchIndexFound(owner, indexName) -> Some(owner, indexName)
                            | _ -> None)
                    // A single statement publishes every exact delta or
                    // none. Two indexes may belong to the same table: read
                    // the updated table from the local fold state.
                    let next =
                        removals |> List.fold (fun catalog (owner, indexName) ->
                            let current = RewriteCatalogModel.lookup catalog owner |> Option.get
                            let remaining =
                                current.Indexes |> Seq.filter (fun index ->
                                    not (String.Equals(index.Name, indexName,
                                                       StringComparison.Ordinal)))
                            RewriteCatalogModel.withIndexes catalog owner remaining
                        ) state
                    let deltas =
                        removals |> List.map (fun (owner, _) ->
                            changedExact syntax owner SqlCatalogOperation.DropIndex
                                SqlCatalogKnowledge.Known)
                    SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                    next, deltas, ([]: SqlDiagnostic list)
        | Recognized (DropQualifiedIndex(indexIdentity, conditional)) ->
            // Index identity and table identity are separate structured
            // names. A qualified index cannot inherit current search_path
            // or be projected into a synthetic table name.
            let schema = indexIdentity.Parts[0]
            let indexName = indexIdentity.Parts[1]
            let inSchema (table: SqlCatalogTable) =
                table.QualifiedName.Parts.Count = 2
                && String.Equals(table.QualifiedName.Parts[0], schema, StringComparison.Ordinal)
            let sameIndex (index: SqlCatalogIndex) =
                String.Equals(index.Name, indexName, StringComparison.Ordinal)
            if sourceDialect <> SqlAgentToolType.Postgres
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun t ->
                   t.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Qualified index requires a complete PostgreSQL Catalog."
            elif state.Tables |> Seq.exists (fun t ->
                t.QualifiedName.Parts.Count = 1
                && ((t.Indexes |> Seq.exists sameIndex) || t.Constraints.Count > 0)) then
                dependencyUnproven "An unqualified Catalog owner may carry the specified schema's index or implicit index."
            else
                let matchingOwners =
                    state.Tables
                    |> Seq.filter (fun t ->
                        inSchema t && (t.Indexes |> Seq.exists sameIndex))
                    |> Seq.toList
                let namedTable =
                    (state.Tables |> Seq.exists (fun t ->
                        inSchema t
                        && String.Equals(relationName t.QualifiedName, indexName,
                                         StringComparison.Ordinal)))
                    || (state.Views |> Seq.exists (fun view ->
                        view.QualifiedName.Parts.Count = 2
                        && String.Equals(view.QualifiedName.Parts[0], schema,
                                         StringComparison.Ordinal)
                        && String.Equals(relationName view.QualifiedName, indexName,
                                         StringComparison.Ordinal)))
                let possibleImplicitIndex =
                    state.Tables
                    |> Seq.exists (fun t -> inSchema t && t.Constraints.Count > 0)
                match matchingOwners with
                | [owner] when not namedTable && not possibleImplicitIndex ->
                    let remaining = owner.Indexes |> Seq.filter (sameIndex >> not)
                    RewriteCatalogModel.withIndexes state owner.QualifiedName remaining
                    |> exact owner.QualifiedName SqlCatalogOperation.DropIndex
                | [] when namedTable ->
                    // IF EXISTS does not license a wrong-kind relation.
                    fail ("DROP INDEX " + indexIdentity.DisplayName +
                          " names a known table relation, not an index.")
                | [] when conditional && not possibleImplicitIndex ->
                    noChange
                | [] ->
                    dependencyUnproven "Qualified index owner or absence cannot be proven against implicit indexes."
                | _ ->
                    dependencyUnproven "Qualified index identity has conflicting or unproven Catalog owners."
        | Recognized (DropIndex(indexName, Some tableName, true))
            when sourceDialect = SqlAgentToolType.MsSqlServer ->
            // T-SQL indexes are table-local, unlike PostgreSQL's
            // schema-local index relations. IF EXISTS only proves that a
            // missing index on a *known existing target* is a no-op.
            // Constraint-backed index identities remain unmodeled, so an
            // absent standalone index cannot be certified when the target
            // has any potentially implicit PK/UNIQUE backing index.
            if not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun table ->
                   table.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "SQL Server conditional DROP INDEX requires a closed, fully known Catalog."
            else
                match RewriteCatalogModel.lookup state tableName with
                | Some owner when owner.Knowledge = SqlCatalogKnowledge.Known ->
                    let isNamed (idx: SqlCatalogIndex) =
                        String.Equals(idx.Name, indexName, StringComparison.Ordinal)
                    match owner.Indexes |> Seq.tryFind isNamed with
                    | Some _ ->
                        let remaining = owner.Indexes |> Seq.filter (isNamed >> not)
                        RewriteCatalogModel.withIndexes state owner.QualifiedName remaining
                        |> exact owner.QualifiedName SqlCatalogOperation.DropIndex
                    | None when owner.Constraints.Count > 0 ->
                        dependencyUnproven
                            "SQL Server constraint-backed index identity cannot be proven absent from the target."
                    | None -> noChange
                | _ ->
                    dependencyUnproven
                        "SQL Server DROP INDEX IF EXISTS requires a proven existing target table."
        | Recognized (DropIndex(indexName, None, true)) ->
            // IF EXISTS must not promote absence from an arbitrary SQL
            // search_path. The modeled single-schema session premise
            // provides the only safe name-resolution scope for this
            // unqualified conditional form. Caller-supplied unqualified
            // Catalog owners defeat that projection.
            let scopedSchema =
                match scope with
                | RewriteCatalogSchemaResolution.SingleSchema schema when
                    RewriteCatalogSchemaResolution.canProject scope state ->
                    Some schema
                | _ -> None
            if sourceDialect <> SqlAgentToolType.Postgres
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun t ->
                   t.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Unqualified IF EXISTS requires a fully known PostgreSQL Catalog."
            else
                match scopedSchema with
                | None ->
                    dependencyUnproven "Unqualified IF EXISTS requires a proven single PostgreSQL search_path schema."
                | Some schema ->
                    let inScope (owner: SqlCatalogTable) =
                        owner.QualifiedName.Parts.Count = 2
                        && String.Equals(owner.QualifiedName.Parts[0], schema,
                                         StringComparison.Ordinal)
                    let matchesIndex (idx: SqlCatalogIndex) =
                        String.Equals(idx.Name, indexName, StringComparison.Ordinal)
                    let possibleImplicit =
                        state.Tables |> Seq.exists (fun owner ->
                            inScope owner && owner.Constraints.Count > 0)
                    let namedTable =
                        (state.Tables |> Seq.exists (fun owner ->
                            inScope owner
                            && String.Equals(relationName owner.QualifiedName,
                                             indexName, StringComparison.Ordinal)))
                        || (state.Views |> Seq.exists (fun view ->
                            view.QualifiedName.Parts.Count = 2
                            && String.Equals(view.QualifiedName.Parts[0], schema,
                                             StringComparison.Ordinal)
                            && String.Equals(relationName view.QualifiedName, indexName,
                                             StringComparison.Ordinal)))
                    let matchingOwners =
                        state.Tables |> Seq.filter (fun owner ->
                            inScope owner && (owner.Indexes |> Seq.exists matchesIndex))
                        |> Seq.toList
                    // A modeled non-unique standalone index cannot be
                    // mistaken for an unmodeled PK / UNIQUE backing index.
                    // Reject the whole proof when implicit names are
                    // possible instead of guessing the collision behavior.
                    if possibleImplicit then
                        dependencyUnproven "Implicit constraint-backed index names in search_path are not proven."
                    elif namedTable then
                        fail ("DROP INDEX '" + indexName +
                              "' names a known table relation, not an index.")
                    else
                        match matchingOwners with
                        | [owner] ->
                            let remaining =
                                owner.Indexes |> Seq.filter (matchesIndex >> not)
                            RewriteCatalogModel.withIndexes state owner.QualifiedName remaining
                            |> exact owner.QualifiedName SqlCatalogOperation.DropIndex
                        | [] -> noChange
                        | _ ->
                            dependencyUnproven "Unqualified index identity has conflicting proven owners."
        | Recognized (DropIndex(indexName, target, _)) ->
            if not state.IsClosedWorld ||
               (state.Tables |> Seq.exists (fun t -> t.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Index owner and absence require a complete known Catalog."
            else
                let owners =
                    state.Tables |> Seq.filter (fun t ->
                        let matchesScope =
                            match scope, target with
                            | RewriteCatalogSchemaResolution.SingleSchema schema, None ->
                                t.QualifiedName.Parts.Count = 2
                                && String.Equals(t.QualifiedName.Parts[0], schema, StringComparison.Ordinal)
                            | _ -> true
                        let matchesTarget =
                            match target with
                            | Some tableName ->
                                String.Equals(tableName.Key, t.QualifiedName.Key, StringComparison.Ordinal)
                            | None -> true
                        matchesScope && matchesTarget
                        && (t.Indexes |> Seq.exists (fun idx ->
                            String.Equals(idx.Name, indexName, StringComparison.Ordinal))))
                    |> Seq.toList
                match owners with
                | [owner] when sourceDialect = SqlAgentToolType.Postgres
                               && Option.isNone target
                               && owner.QualifiedName.IsQualified
                               && (match scope with
                                   | RewriteCatalogSchemaResolution.Unproven -> true
                                   | _ -> false) ->
                    // A sole known qualified Catalog owner does not
                    // prove that unqualified DROP INDEX resolves there:
                    // actual search_path could point elsewhere.
                    dependencyUnproven "Unqualified DROP INDEX requires a proven search_path for its qualified Catalog owner."
                | [owner] ->
                    let remaining =
                        owner.Indexes |> Seq.filter (fun idx ->
                            not (String.Equals(idx.Name, indexName, StringComparison.Ordinal)))
                    RewriteCatalogModel.withIndexes state owner.QualifiedName remaining
                    |> exact owner.QualifiedName SqlCatalogOperation.DropIndex
                | [] ->
                    dependencyUnproven
                        ("DROP INDEX '" + indexName +
                         "' is not a tracked standalone index; implicit constraint indexes cannot be ruled out.")
                | _ -> dependencyUnproven "Index name resolves to more than one modeled owner."
        | Recognized (AddConstraint(tableName, newConstraint)) ->
            match RewriteCatalogModel.lookup state tableName with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                match RewriteSchemaDependencies.validateNewConstraint sourceDialect sourceProfile state
                          tableName newConstraint with
                | RewriteSchemaDependencies.Proven ->
                    let updated =
                        RewriteCatalogModel.withConstraints state tableName
                            (seq { yield! table.Constraints; yield newConstraint })
                    exact tableName SqlCatalogOperation.AddConstraint updated
                | RewriteSchemaDependencies.Invalid message -> fail message
                | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
            | _ -> dependencyUnproven "Cannot establish the table's known constraint set."
        | Recognized (DropConstraint(tableName, constraintName, conditional, mode)) ->
            match RewriteCatalogModel.lookup state tableName with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                match table.Constraints |> Seq.tryFind (fun item ->
                    String.Equals(item.Name, constraintName, StringComparison.Ordinal)) with
                | None when conditional
                            && sourceDialect = SqlAgentToolType.Postgres
                            && state.IsClosedWorld
                            && (state.Tables |> Seq.forall (fun t ->
                                t.Knowledge = SqlCatalogKnowledge.Known)) ->
                    // A missing constraint is a true PostgreSQL no-op,
                    // not a speculative DropConstraint effect.
                    noChange
                | None when conditional ->
                    dependencyUnproven "DROP CONSTRAINT IF EXISTS requires a complete known Catalog."
                | None ->
                    fail ("DROP CONSTRAINT '" + constraintName + "' does not exist.")
                | Some target when mode = Cascade
                                   && sourceDialect = SqlAgentToolType.Postgres ->
                    if not state.IsClosedWorld
                       || (state.Tables |> Seq.exists (fun t ->
                           t.Knowledge <> SqlCatalogKnowledge.Known)) then
                        dependencyUnproven "DROP CONSTRAINT CASCADE requires a complete known Catalog."
                    else
                        let inbound =
                            if target.Kind = SqlCatalogConstraintKind.PrimaryKey
                               || target.Kind = SqlCatalogConstraintKind.Unique then
                                RewriteCatalogModel.inboundForeignKeys state tableName
                                |> Seq.filter (fun (_, fk) ->
                                    fk.ReferencedColumns.Count = target.Columns.Count
                                    && Seq.forall2
                                        (fun a b -> String.Equals(a, b, StringComparison.Ordinal))
                                        fk.ReferencedColumns target.Columns)
                                |> Seq.toList
                            else []
                        let ambiguousParentKey =
                            not inbound.IsEmpty
                            && (table.Constraints
                                |> Seq.filter (fun key ->
                                    (key.Kind = SqlCatalogConstraintKind.PrimaryKey
                                     || key.Kind = SqlCatalogConstraintKind.Unique)
                                    && key.Columns.Count = target.Columns.Count
                                    && Seq.forall2
                                        (fun a b -> String.Equals(a, b, StringComparison.Ordinal))
                                        key.Columns target.Columns)
                                |> Seq.length) > 1
                        if ambiguousParentKey then
                            dependencyUnproven
                                "Multiple same-column parent keys make the exact referenced FK constraint unproven."
                        else
                            // Resolve the entire set against ONE pre-drop
                            // Catalog before publishing any state changes.
                            let removals =
                                (tableName, constraintName)
                                :: (inbound |> List.map (fun (owner, fk) ->
                                    owner.QualifiedName, fk.Name))
                            if removals |> List.exists (fun (_, name) ->
                                String.IsNullOrWhiteSpace name) then
                                dependencyUnproven "Cascade cannot name an unmodeled inbound FK constraint."
                            else
                                let next =
                                    RewriteCatalogModel.dropProvenConstraintsAtomically
                                        state removals
                                let changes =
                                    removals |> List.map (fun (owner, key) ->
                                        changedConstraintExact syntax owner key)
                                SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                                next, changes, ([]: SqlDiagnostic list)
                | Some target ->
                    match RewriteSchemaDependencies.canDropConstraint state tableName target with
                    | RewriteSchemaDependencies.Proven ->
                        let updated =
                            RewriteCatalogModel.withConstraints state tableName
                                (table.Constraints |> Seq.filter (fun c ->
                                    not (String.Equals(c.Name, constraintName, StringComparison.Ordinal))))
                        SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                        updated, [changedConstraintExact syntax tableName constraintName],
                        ([]: SqlDiagnostic list)
                    | RewriteSchemaDependencies.Invalid message -> fail message
                    | RewriteSchemaDependencies.Unproven message -> dependencyUnproven message
            | _ -> dependencyUnproven "Constraint owner is not a proven table."
        | Recognized (RenameConstraint(name, oldName, newName)) ->
            // A named-constraint rename cannot be proved using only its
            // visible string: PostgreSQL also renames a PK/UNIQUE backing
            // index; Oracle constraint names are schema-global.
            let same a b = String.Equals(a, b, StringComparison.Ordinal)
            let providerSupported =
                sourceDialect = SqlAgentToolType.Postgres
                || sourceDialect = SqlAgentToolType.Oracle
            if not providerSupported
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun t ->
                   t.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Constraint rename requires a complete known provider Catalog."
            else
                match RewriteCatalogModel.lookup state name with
                | Some owner when owner.Knowledge = SqlCatalogKnowledge.Known ->
                    match owner.Constraints |> Seq.tryFind (fun c -> same c.Name oldName) with
                    | None -> fail ("RENAME CONSTRAINT '" + oldName +
                                    "' does not exist on table '" + name.DisplayName + "'.")
                    | Some _ when same oldName newName ->
                        fail "RENAME CONSTRAINT target must differ from the old name."
                    | Some target ->
                        let localCollision =
                            owner.Constraints |> Seq.exists (fun c -> same c.Name newName)
                        let oracleGlobalCollision =
                            sourceDialect = SqlAgentToolType.Oracle
                            && (state.Tables |> Seq.exists (fun table ->
                                table.Constraints |> Seq.exists (fun c ->
                                    same c.Name newName)))
                        // For PostgreSQL, PK/UNIQUE owns an index with the
                        // constraint's name. Indexes/tables/views share a
                        // schema relation namespace. Unqualified owners can
                        // overlap any explicit schema: do not infer search_path.
                        let samePossibleIndexSchema (other: SqlCatalogQualifiedName) =
                            name.Parts.Count <> 2
                            || other.Parts.Count <> 2
                            || same name.Parts[0] other.Parts[0]
                        let indexBacked =
                            target.Kind = SqlCatalogConstraintKind.PrimaryKey
                            || target.Kind = SqlCatalogConstraintKind.Unique
                        let indexOwners =
                            state.Tables
                            |> Seq.filter (fun t ->
                                samePossibleIndexSchema t.QualifiedName)
                            |> Seq.toList
                        let ambiguousImplicitIndex =
                            sourceDialect = SqlAgentToolType.Postgres && indexBacked
                            && (indexOwners |> List.exists (fun t ->
                                t.Constraints |> Seq.exists (fun c ->
                                    (c.Kind = SqlCatalogConstraintKind.PrimaryKey
                                     || c.Kind = SqlCatalogConstraintKind.Unique)
                                    && String.IsNullOrWhiteSpace c.Name)))
                        let postgresIndexCollision =
                            sourceDialect = SqlAgentToolType.Postgres && indexBacked
                            && ((indexOwners |> List.exists (fun t ->
                                    same t.QualifiedName.Parts[t.QualifiedName.Parts.Count - 1]
                                        newName
                                    || (t.Indexes |> Seq.exists (fun idx ->
                                        same idx.Name newName))
                                    || (t.Constraints |> Seq.exists (fun c ->
                                        (c.Kind = SqlCatalogConstraintKind.PrimaryKey
                                         || c.Kind = SqlCatalogConstraintKind.Unique)
                                        && same c.Name newName))))
                                || (state.Views |> Seq.exists (fun v ->
                                    samePossibleIndexSchema v.QualifiedName
                                    && same v.QualifiedName.Parts[v.QualifiedName.Parts.Count - 1]
                                        newName)))
                        if localCollision || oracleGlobalCollision || postgresIndexCollision then
                            fail ("RENAME CONSTRAINT target '" + newName +
                                  "' collides with a proven provider constraint/index namespace.")
                        elif ambiguousImplicitIndex then
                            dependencyUnproven
                                "Unnamed PK/UNIQUE backing indexes make the target name namespace unproven."
                        else
                            let updated =
                                RewriteCatalogModel.renameKnownConstraint
                                    state name oldName newName
                            let delta =
                                SqlCatalogChange(
                                    name.DisplayName, SqlCatalogOperation.RenameConstraint,
                                    SqlCatalogKnowledge.Known, syntax.Span,
                                    null, name, newName)
                            SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                            updated, [delta], ([]: SqlDiagnostic list)
                | _ when RewriteCatalogModel.isKnownAbsent state name ->
                    fail ("ALTER TABLE " + name.DisplayName +
                          " does not exist in the known Catalog.")
                | _ ->
                    dependencyUnproven "Constraint owner is not a proven known table."
        | Recognized (RenameColumn(name, oldName, newName)) ->
            // Oracle and PostgreSQL retarget physical indexes and modeled
            // local constraints on rename. Preserve typed dependency
            // lineage, not just the visible column header.
            if (sourceDialect <> SqlAgentToolType.Oracle
                && sourceDialect <> SqlAgentToolType.Postgres)
               || not state.IsClosedWorld
               || (state.Tables |> Seq.exists (fun item ->
                   item.Knowledge <> SqlCatalogKnowledge.Known)) then
                dependencyUnproven "Column rename requires a closed, known Catalog."
            else
                match RewriteCatalogModel.lookup state name with
                | Some owner when owner.Knowledge = SqlCatalogKnowledge.Known ->
                    let same left right =
                        String.Equals(left, right, StringComparison.Ordinal)
                    let hasColumn columnName =
                        owner.Columns |> Seq.exists (fun col ->
                            same col.Name columnName)
                    if not (hasColumn oldName) then
                        fail ("ALTER TABLE " + name.DisplayName +
                              " column " + oldName + " does not exist.")
                    elif hasColumn newName then
                        fail ("ALTER TABLE " + name.DisplayName +
                              " target column " + newName + " already exists.")
                    elif sourceDialect = SqlAgentToolType.Oracle
                         && not (RewriteCatalogModel.dependentViews
                                      state (Set.singleton name.Key)).IsEmpty then
                        // Oracle dependent View validity is not yet proven.
                        // PostgreSQL retains the View's public output schema
                        // while retargeting only directly owned source columns.
                        dependencyUnproven "Oracle View dependency invalidation after column rename is unproven."
                    else
                        // Both providers atomically retarget modeled indexes,
                        // keys and exact inbound/self-FK referenced columns.
                        // Oracle's prior dependent-View invalidation guard
                        // prevents it from inheriting PostgreSQL View proof.
                        let updated =
                            RewriteCatalogModel.renameColumnWithProvenDependents
                                state name oldName newName
                        SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                        updated,
                        [changedColumnExact syntax name newName SqlCatalogOperation.RenameColumn],
                        ([]: SqlDiagnostic list)
                | _ when RewriteCatalogModel.isKnownAbsent state name ->
                    fail ("ALTER TABLE " + name.DisplayName +
                          " does not exist in the known Catalog.")
                | _ ->
                    dependencyUnproven "Column rename target table is not proven known."
        | Recognized (AlterColumns(name, actions)) ->
            match RewriteCatalogModel.lookup state name with
            | Some table when table.Knowledge = SqlCatalogKnowledge.Known ->
                // Transactional *analysis* within one ALTER statement: stage
                // every operation in local working state, then publish a single
                // immutable snapshot. If any action fails, publish no deltas.
                let working = ResizeArray<SqlCatalogColumn>(table.Columns)
                let pending = ResizeArray<SqlCatalogChange>()
                let mutable failure: string option = None
                let mutable unknownDependency: string option = None
                let locate column =
                    working |> Seq.tryFindIndex (fun c ->
                        String.Equals(c.Name, column, StringComparison.Ordinal))
                for action in actions do
                    RewriteResourceBudget.checkpoint ()
                    if failure.IsNone then
                        match action with
                        | Add column ->
                            if locate column.Name |> Option.isSome then
                                failure <- Some(
                                    "ALTER TABLE " + name.DisplayName + " column " + column.Name +
                                    " already exists.")
                            else
                                working.Add column
                                pending.Add(changedColumnExact syntax name column.Name SqlCatalogOperation.AddColumn)
                        | Remove(columnName, ifExists) ->
                            match locate columnName with
                            | Some index when working.Count = 1 ->
                                failure <- Some(
                                    "ALTER TABLE " + name.DisplayName + " cannot drop its last column.")
                            | Some index ->
                                match RewriteSchemaDependencies.canDropColumn state name columnName with
                                | RewriteSchemaDependencies.Proven ->
                                    working.RemoveAt index
                                    pending.Add(changedColumnExact syntax name columnName SqlCatalogOperation.DropColumn)
                                | RewriteSchemaDependencies.Invalid message ->
                                    failure <- Some message
                                | RewriteSchemaDependencies.Unproven message ->
                                    unknownDependency <- Some message
                            | None when ifExists -> ()
                            | None ->
                                failure <- Some(
                                    "ALTER TABLE " + name.DisplayName + " column " + columnName +
                                    " does not exist.")
                match failure, unknownDependency with
                | Some message, _ -> fail message
                | None, Some message -> dependencyUnproven message
                | None, None when pending.Count = 0 -> noChange
                | None, None ->
                    let updated = RewriteCatalogModel.knownTable state name working
                    SqlStatementAnalysisStatus.Analyzed, SqlCatalogKnowledge.Known,
                    updated, (pending |> Seq.toList), []
            | _ when RewriteCatalogModel.isKnownAbsent state name ->
                fail ("ALTER TABLE " + name.DisplayName + " does not exist in the known catalog.")
            | _ ->
                // The referenced table/columns are not proven. A composite
                // ALTER cannot claim its individual mutations succeeded.
                partial name SqlCatalogOperation.Indeterminate

    let analyze sql sourceDialect (sourceProfile: SqlProviderCapabilityProfile | null) (initial: SqlCatalogState) =
        if Object.ReferenceEquals(initial, null) then nullArg "initialCatalog"
        RewriteResourceBudget.withCompilation (fun () ->
            let parsed = RewriteScriptParser.parse sql sourceDialect sourceProfile
            let results = ResizeArray<SqlStatementAnalysis>()
            let errors = ResizeArray<SqlDiagnostic>()
            let mutable current = initial
            let mutable transaction = RewriteTransactionAnalyzer.Outside
            let mutable session = RewriteSessionAnalyzer.initial sourceDialect sourceProfile

            RewriteResourceBudget.withStage "ScriptAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                for syntax in parsed.Statements do
                    RewriteResourceBudget.checkpoint ()
                    let before = current
                    let transactionBefore = RewriteTransactionAnalyzer.status transaction
                    let sessionBefore = RewriteSessionAnalyzer.snapshot session
                    let mutable securityIntents : SqlSecurityIntent list = []
                    let mutable securityDependencies : SqlSecurityDependency list = []
                    let mutable utilityIntents : SqlUtilityIntent list = []
                    let mutable proceduralIntents : SqlProceduralIntent list = []
                    let mutable proceduralDefinitions : SqlProceduralDefinition list = []
                    let mutable sessionOperation = SqlStatementEffectOperation.Unknown
                    let mutable controlOperation = SqlTransactionControlOperation.None
                    let status, effects, certainty, bindingStatus, nameStatus, typeStatus, next, changes, diagnostics =
                        if syntax.Status = SqlScriptParseStatus.Invalid then
                            SqlStatementAnalysisStatus.Invalid, SqlEffectKind.None,
                            SqlCatalogKnowledge.Unknown, SqlCatalogBindingStatus.Rejected,
                            SqlCatalogBindingStatus.Rejected, SqlCatalogBindingStatus.NotApplicable, current, [],
                            (if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                             else [syntax.Diagnostic])
                        elif syntax.Kind = SqlScriptStatementKind.Transaction then
                            let status, certainty, next, nextSession, newTransaction, changes, diagnostics, parsedControl =
                                RewriteTransactionAnalyzer.analyze
                                    sourceDialect sourceProfile current session transaction syntax
                            controlOperation <- parsedControl
                            // A transaction boundary or savepoint rollback is
                            // responsible for BOTH catalog and session state.
                            // No separate coordinator-side rollback heuristic.
                            transaction <- newTransaction
                            session <- nextSession
                            let effects =
                                if changes.IsEmpty then SqlEffectKind.ChangeTransaction
                                else SqlEffectKind.ChangeTransaction ||| SqlEffectKind.ChangeSchema
                            status, effects, certainty, SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable, SqlCatalogBindingStatus.NotApplicable,
                            next, changes, diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Security then
                            let status, intents, dependencies, diagnostics =
                                RewriteResourceBudget.withStage "SecurityAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                                    RewriteSecurityAnalyzer.analyze sourceDialect sourceProfile syntax)
                            securityIntents <- intents
                            securityDependencies <- dependencies
                            status, SqlEffectKind.ChangePermissions, SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            current, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Utility then
                            let status, intents, next, diagnostics =
                                RewriteUtilityAnalyzer.analyze sourceDialect sourceProfile current syntax
                            utilityIntents <- intents
                            let flags =
                                intents |> List.fold (fun all item -> all ||| item.PotentialEffects)
                                    SqlEffectKind.Administrative
                            let flags =
                                if status = SqlStatementAnalysisStatus.Unsupported
                                   || status = SqlStatementAnalysisStatus.Invalid then
                                    flags ||| SqlEffectKind.UnknownEffects
                                else flags
                            status, flags, SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            next, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Procedural then
                            let status, intents, definitions, next, diagnostics =
                                RewriteResourceBudget.withStage "ProceduralAnalysis" SqlDiagnosticStage.SemanticValidation (fun () ->
                                    RewriteProceduralAnalyzer.analyze sourceDialect sourceProfile current syntax)
                            proceduralIntents <- intents
                            proceduralDefinitions <- definitions
                            // Stored procedures may change rows, schema, session,
                            // transaction boundaries or permissions. Neither CALL
                            // syntax nor a procedure header proves the body.
                            let flags =
                                SqlEffectKind.ReadData ||| SqlEffectKind.WriteData
                                ||| SqlEffectKind.ChangeSchema ||| SqlEffectKind.ChangePermissions
                                ||| SqlEffectKind.ChangeTransaction ||| SqlEffectKind.ChangeSession
                                ||| SqlEffectKind.Administrative ||| SqlEffectKind.UnknownEffects
                            let flags =
                                if not definitions.IsEmpty then
                                    // Creating a routine changes metadata;
                                    // statements in its body have NOT run.
                                    SqlEffectKind.ChangeSchema ||| SqlEffectKind.UnknownEffects
                                else flags
                            status, flags, SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            next, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Session then
                            let analyzedStatus, analyzedCertainty, analyzedSession, analyzedCatalog, diagnostics, operation =
                                RewriteSessionAnalyzer.analyze sourceDialect sourceProfile session current syntax
                            // A recognized SET/USE is syntax evidence, not proof
                            // that it took effect in an unproven transaction
                            // (for example, one that might already be aborted).
                            // Keep the typed operation but never re-mint a
                            // known Session/Catalog premise while Transaction
                            // remains Unknown. Unsupported syntax already
                            // invalidates these premises in the analyzer.
                            let status, certainty, nextSession, nextCatalog =
                                if transactionBefore = SqlTransactionState.Unknown
                                   && analyzedStatus <> SqlStatementAnalysisStatus.Unsupported then
                                    SqlStatementAnalysisStatus.PartiallyAnalyzed,
                                    SqlCatalogKnowledge.Unknown,
                                    RewriteSessionAnalyzer.unknown,
                                    RewriteCatalogModel.taintAll analyzedCatalog
                                else
                                    analyzedStatus, analyzedCertainty, analyzedSession, analyzedCatalog
                            session <- nextSession
                            sessionOperation <- operation
                            let sessionEffects =
                                if status = SqlStatementAnalysisStatus.PartiallyAnalyzed then
                                    SqlEffectKind.ChangeSession ||| SqlEffectKind.UnknownEffects
                                else SqlEffectKind.ChangeSession
                            status, sessionEffects, certainty,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            nextCatalog, ([]: SqlCatalogChange list), diagnostics
                        elif syntax.Kind = SqlScriptStatementKind.Schema
                             && sourceDialect = SqlAgentToolType.MySQL
                             && transactionBefore = SqlTransactionState.Active then
                            // MySQL non-temporary DDL implicitly commits, but
                            // temporary-table variants and storage engines
                            // have distinct rules. Do not run the generic DDL
                            // virtual-Catalog mutation then pretend an outer
                            // ROLLBACK can restore its original state.
                            let warning =
                                diagnostic syntax "SQL_TRANSACTION_IMPLICIT_COMMIT_UNPROVEN"
                                    SqlDiagnosticCategory.Capability
                                    "MySQL DDL may cause an implicit commit or use nontransactional storage; transaction/catalog rollback proof is invalid."
                            SqlStatementAnalysisStatus.Unsupported,
                            SqlEffectKind.ChangeSchema ||| SqlEffectKind.ChangeTransaction
                                ||| SqlEffectKind.UnknownEffects,
                            SqlCatalogKnowledge.Unknown,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable,
                            RewriteCatalogModel.taintAll current,
                            ([]: SqlCatalogChange list), [warning]
                        elif syntax.Kind = SqlScriptStatementKind.Schema then
                            let status, certainty, next, changes, diagnostics =
                                analyzeDdl sourceDialect sourceProfile (RewriteSessionAnalyzer.scope session) current syntax
                            let effects =
                                if sourceDialect = SqlAgentToolType.Oracle
                                   || sourceDialect = SqlAgentToolType.MySQL then
                                    // Oracle and non-temporary MySQL DDL implicitly
                                    // commit even outside an explicit BEGIN.
                                    // The static Outside context cannot prove
                                    // that a MySQL session has autocommit enabled.
                                    // Runtime DML effects remain unproven.
                                    SqlEffectKind.ChangeSchema ||| SqlEffectKind.ChangeTransaction
                                else SqlEffectKind.ChangeSchema
                            status, effects, certainty, SqlCatalogBindingStatus.NotApplicable,
                            SqlCatalogBindingStatus.NotApplicable, SqlCatalogBindingStatus.NotApplicable,
                            next, changes, diagnostics
                        else
                            let effects =
                                match syntax.Kind with
                                | SqlScriptStatementKind.Query -> SqlEffectKind.ReadData
                                | SqlScriptStatementKind.Mutation -> SqlEffectKind.WriteData
                                | SqlScriptStatementKind.Security -> SqlEffectKind.ChangePermissions
                                | SqlScriptStatementKind.Transaction -> SqlEffectKind.ChangeTransaction
                                | SqlScriptStatementKind.Session -> SqlEffectKind.ChangeSession
                                | SqlScriptStatementKind.Utility -> SqlEffectKind.Administrative
                                | _ -> SqlEffectKind.None
                            let bindingStatus, nameStatus, typeStatus, status, certainty, catalogErrors =
                                if syntax.Status = SqlScriptParseStatus.Parsed
                                   && (syntax.Kind = SqlScriptStatementKind.Query
                                       || syntax.Kind = SqlScriptStatementKind.Mutation) then
                                    RewriteCatalogBinding.verify sourceDialect sourceProfile
                                        (RewriteSessionAnalyzer.scope session) current syntax
                                else
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlCatalogBindingStatus.NotApplicable,
                                    SqlStatementAnalysisStatus.Unsupported,
                                    SqlCatalogKnowledge.Unknown,
                                    []
                            let next =
                                if syntax.Kind = SqlScriptStatementKind.Procedural
                                   || syntax.Kind = SqlScriptStatementKind.Unknown then
                                    RewriteCatalogModel.taintAll current
                                else current
                            let diagnostics =
                                let parserDiagnostics =
                                    if Object.ReferenceEquals(syntax.Diagnostic, null) then []
                                    else [syntax.Diagnostic]
                                parserDiagnostics @ catalogErrors
                            status, effects, certainty, bindingStatus, nameStatus, typeStatus, next, [], diagnostics
                    // Unrecognized or invalid SQL has no exhaustive effect proof.
                    // Apply the same conservative flag to every family before
                    // transaction provenance and structured details are derived.
                    // This never promotes a statement to Analyzed or authorizes SQL.
                    let effects =
                        if status = SqlStatementAnalysisStatus.Unsupported
                           || status = SqlStatementAnalysisStatus.Invalid then
                            effects ||| SqlEffectKind.UnknownEffects
                        else effects
                    // Only opaque, potentially executing families lose the
                    // static Outside premise. Unsupported DDL/DCL/Session and
                    // unknown SQL have unmodeled transaction behavior; routine
                    // calls and executing utilities may run transaction control
                    // indirectly. A syntactically invalid statement has no
                    // modeled execution, and planning-only EXPLAIN remains safe.
                    let outsideBoundaryUnproven =
                        if status = SqlStatementAnalysisStatus.Invalid then false
                        else
                            match syntax.Kind with
                            | SqlScriptStatementKind.Unknown
                            | SqlScriptStatementKind.Procedural -> true
                            | SqlScriptStatementKind.Session
                            | SqlScriptStatementKind.Schema
                            | SqlScriptStatementKind.Security ->
                                status = SqlStatementAnalysisStatus.Unsupported
                            | SqlScriptStatementKind.Utility ->
                                status = SqlStatementAnalysisStatus.Unsupported
                                || (utilityIntents |> List.exists (fun intent ->
                                    intent.ExecutionMode <> SqlUtilityExecutionMode.PlanningOnly))
                            | _ -> false
                    let nextTransaction =
                        RewriteTransactionAnalyzer.invalidateIfUnproven
                            sourceDialect transaction syntax.Kind status outsideBoundaryUnproven
                    let transactionAfter = RewriteTransactionAnalyzer.status nextTransaction
                    let stateBecameUnknown =
                        transactionBefore = SqlTransactionState.Active
                        && transactionAfter = SqlTransactionState.Unknown
                    let outsideProofLost =
                        transactionBefore = SqlTransactionState.Outside
                        && transactionAfter = SqlTransactionState.Unknown
                        && syntax.Kind <> SqlScriptStatementKind.Transaction
                    let transactionProofLost = stateBecameUnknown || outsideProofLost
                    transaction <- nextTransaction
                    // An Unsupported statement can change Session state even
                    // if its surface family is not Session (e.g. DISCARD ALL
                    // or ALTER SESSION). A procedure invocation and an
                    // executing utility also have opaque Session side effects.
                    // Pure EXPLAIN planning and parsed-but-invalid syntax do
                    // not invent a session transition.
                    let sessionUnproven =
                        status = SqlStatementAnalysisStatus.Unsupported
                        || (syntax.Kind = SqlScriptStatementKind.Procedural
                            && not proceduralIntents.IsEmpty)
                        || (syntax.Kind = SqlScriptStatementKind.Utility
                            && (utilityIntents |> List.exists (fun intent ->
                                intent.ExecutionMode <> SqlUtilityExecutionMode.PlanningOnly)))
                    if transactionProofLost || sessionUnproven then
                        session <- RewriteSessionAnalyzer.unknown
                    let after =
                        if transactionProofLost then RewriteCatalogModel.taintAll next else next
                    let additionalErrors =
                        if stateBecameUnknown then
                            [diagnostic syntax "SQL_TRANSACTION_CONTEXT_UNPROVEN" SqlDiagnosticCategory.Capability
                                "An unproven statement or failure has invalidated the transaction-state and Catalog proofs."]
                        else []
                    let allDiagnostics = diagnostics @ additionalErrors
                    // Semantic grammar/analysis may be recognized even while
                    // the input transaction state is unproven (notably Oracle
                    // COMMIT/ROLLBACK). Recognition does not re-establish
                    // certainty for the enclosing statement or its effects.
                    let runtimeCertainty =
                        if transactionProofLost
                           || transactionBefore = SqlTransactionState.Unknown then
                            SqlCatalogKnowledge.Unknown
                        else certainty
                    let provenance =
                        RewriteResourceBudget.withStage "TransactionProvenance" SqlDiagnosticStage.SemanticValidation (fun () ->
                            RewriteTransactionProvenance.describe
                                sourceDialect syntax status runtimeCertainty effects
                                transactionBefore (RewriteTransactionAnalyzer.status transaction)
                                controlOperation (readOnly allDiagnostics))
                    current <- after
                    for item in allDiagnostics do errors.Add item
                    let detailedEffects, effectCoverage =
                        RewriteResourceBudget.withStage "ScriptEffects" SqlDiagnosticStage.SemanticValidation (fun () ->
                            RewriteStatementEffects.describe sourceDialect sourceProfile syntax
                                status runtimeCertainty effects (readOnly changes)
                                (readOnly securityIntents) (readOnly securityDependencies)
                                (readOnly utilityIntents) (readOnly proceduralIntents)
                                (readOnly proceduralDefinitions) sessionOperation provenance)
                    results.Add(
                        SqlStatementAnalysis(
                            syntax, status, effects,
                            runtimeCertainty,
                            bindingStatus, nameStatus, typeStatus, before, after,
                            transactionBefore, RewriteTransactionAnalyzer.status transaction,
                            sessionBefore, RewriteSessionAnalyzer.snapshot session,
                            readOnly changes, detailedEffects, effectCoverage,
                            readOnly securityIntents, readOnly securityDependencies,
                            readOnly utilityIntents, readOnly proceduralIntents,
                            readOnly proceduralDefinitions,
                            provenance, readOnly allDiagnostics)))
            SqlScriptAnalysisResult(
                readOnly results, readOnly errors, current,
                RewriteTransactionAnalyzer.status transaction,
                RewriteSessionAnalyzer.snapshot session))
