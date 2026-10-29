namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Catalog
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Optional metadata-backed name checking on the already bound closed AST.
/// It never queries a database or falls back to the compatibility AST.
module internal RewriteSchemaValidation =

    type private Catalog =
        { Dialect: SqlAgentToolType
          // Only physical relation identities are valid mutation targets.
          Tables: Map<string, string list>
          // Query and mutation input relations may include proven Views.
          ReadRelations: Map<string, string list>
          ProvenInsertColumnOrder: bool }
    type private Source = { Qualifiers: string list; Columns: string list }
    // Unqualified row shape follows JOIN USING coalescing; qualified sources stay separate.
    type private Scope =
        { Parent: Scope option
          Sources: Source list
          Unqualified: string list }

    let private makeScope parent sources =
        { Parent = parent
          Sources = sources
          Unqualified = sources |> List.collect (fun source ->
              RewriteResourceBudget.charge (int64 source.Columns.Length)
              source.Columns) }

    let private reject code span message : 'T =
        let diagnosticSpan =
            if Span.isKnown span then SqlDiagnosticSpan(span.Start, span.Length)
            else Unchecked.defaultof<SqlDiagnosticSpan>
        let diagnostic =
            SqlDiagnostic(code, SqlDiagnosticStage.Binding, SqlDiagnosticCategory.Binding, message, diagnosticSpan)
        raise (SqlCompilationException(message, diagnostic))

    let private keyPart dialect (part: IdentifierPart) =
        match dialect with
        | SqlAgentToolType.Postgres when not part.WasQuoted -> part.Value.ToLowerInvariant()
        | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird when not part.WasQuoted ->
            part.Value.ToUpperInvariant()
        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
            part.Value.ToUpperInvariant()
        | _ -> part.Value

    let private catalogPart dialect (value: string) =
        RewriteResourceBudget.charge (max 1L (int64 value.Length))
        match dialect with
        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
            value.ToUpperInvariant()
        | _ -> value

    let private qualifiedKey parts =
        parts |> List.map (fun (part: string) ->
            part.Replace("\\", "\\\\").Replace(".", "\\.")) |> String.concat "."

    let private keyIdentifier dialect identifier =
        Identifier.parts identifier |> List.map (keyPart dialect) |> qualifiedKey

    let private keyCatalog dialect (value: string) =
        RewriteResourceBudget.charge (max 1L (int64 value.Length))
        value.Split('.') |> Array.map (catalogPart dialect) |> String.concat "."

    let private buildCatalog dialect (snapshot: SqlSchemaSnapshot) =
        if snapshot.Provider <> dialect then
            invalidArg "schemaSnapshot" (
                "Schema provider " + string snapshot.Provider + " does not match source " + string dialect + ".")
        let tables =
            snapshot.Tables
            |> Seq.map (fun table ->
                RewriteResourceBudget.checkpoint ()
                keyCatalog dialect table.Name,
                (table.Columns |> Seq.map (catalogPart dialect) |> Seq.toList))
            |> Seq.toList
        if (tables |> List.map fst |> List.distinct |> List.length) <> tables.Length then
            invalidArg "schemaSnapshot" "Schema table names collide under provider identifier rules."
        tables |> List.iter (fun (name, columns) ->
            if (columns |> List.distinct |> List.length) <> columns.Length then
                invalidArg "schemaSnapshot" ("Schema columns collide in table '" + name + "'."))
        let physical = Map.ofList tables
        { Dialect = dialect; Tables = physical; ReadRelations = physical
          ProvenInsertColumnOrder = false }

    let private physicalTable catalog name =
        match Map.tryFind (keyIdentifier catalog.Dialect name) catalog.Tables with
        | Some columns -> columns
        | None ->
            reject "SQL_SCHEMA_TABLE_NOT_FOUND" Span.unknown (
                "Schema does not contain physical table '" + Identifier.text name + "'.")

    let private readableTable catalog name =
        match Map.tryFind (keyIdentifier catalog.Dialect name) catalog.ReadRelations with
        | Some columns -> columns
        | None ->
            reject "SQL_SCHEMA_TABLE_NOT_FOUND" Span.unknown (
                "Schema does not contain readable relation '" + Identifier.text name + "'.")

    let private namedSource dialect name alias columns =
        let full = keyIdentifier dialect name
        let tail = Identifier.parts name |> List.last |> keyPart dialect |> List.singleton |> qualifiedKey
        let keys =
            match alias with
            | Some value -> [ qualifiedKey [keyPart dialect value] ]
            | None when tail = full -> [ full ]
            | None -> [ full; tail ]
        { Qualifiers = keys; Columns = columns }

    let private aliasedSource dialect alias columns =
        { Qualifiers = [ qualifiedKey [keyPart dialect alias] ]; Columns = columns }

    let private countColumn source key =
        source.Columns |> List.sumBy (fun name ->
            RewriteResourceBudget.checkpoint ()
            if name = key then 1 else 0)

    let private hasQualifier name source =
        RewriteResourceBudget.checkpoint ()
        source.Qualifiers |> List.exists (fun qualifier ->
            RewriteResourceBudget.checkpoint ()
            qualifier = name)

    // Keep the original nearest-scope and alias-hiding rules without
    // recursively calling through a chain of correlated SELECT scopes.
    let private resolveColumn scope qualifier key span =
        let mutable current = Some scope
        while current.IsSome do
            RewriteResourceBudget.checkpoint ()
            let active = Option.get current
            let matchingSources =
                match qualifier with
                | None -> active.Sources
                | Some name ->
                    active.Sources |> List.filter (hasQualifier name)
            let hits =
                match qualifier with
                | None -> active.Unqualified |> List.sumBy (fun name ->
                    RewriteResourceBudget.checkpoint ()
                    if name = key then 1 else 0)
                | Some _ -> matchingSources |> List.sumBy (fun item -> countColumn item key)
            if hits > 1 then
                reject "SQL_SCHEMA_COLUMN_AMBIGUOUS" span (
                    "Column '" + key + "' is ambiguous across the current schema scope.")
            elif hits = 1 then
                current <- None
            else
                match qualifier, active.Parent with
                | None, Some parent -> current <- Some parent
                | Some _, Some parent when matchingSources.IsEmpty ->
                    current <- Some parent
                | _ ->
                    reject "SQL_SCHEMA_COLUMN_NOT_FOUND" span (
                        "Column '" + key + "' is missing from the resolved schema scope.")

    let private column catalog scope span identifier =
        let parts = Identifier.parts identifier
        let key = parts |> List.last |> keyPart catalog.Dialect
        let qualifier =
            if parts.Length < 2 then None
            else
                parts
                |> List.take (parts.Length - 1)
                |> List.map (keyPart catalog.Dialect)
                |> String.concat "."
                |> Some
        resolveColumn scope qualifier key span

    let private qualifiedSource scope name span =
        let mutable current = Some scope
        let mutable resolved = None
        while current.IsSome && resolved.IsNone do
            RewriteResourceBudget.checkpoint ()
            let active = Option.get current
            match active.Sources |> List.filter (hasQualifier name) with
            | [ found ] -> resolved <- Some found
            | [] -> current <- active.Parent
            | _ ->
                reject "SQL_SCHEMA_QUALIFIER_UNPROVABLE" span ("Ambiguous table qualifier '" + name + "'.")
        match resolved with
        | Some source -> source
        | None -> reject "SQL_SCHEMA_QUALIFIER_UNPROVABLE" span ("Unknown table qualifier '" + name + "'.")

    let private wildcard catalog scope span qualifier =
        match qualifier with
        | None when scope.Sources.IsEmpty ->
            reject "SQL_SCHEMA_SHAPE_UNPROVABLE" span "Unqualified wildcard has no row source."
        | None -> ()
        | Some name -> qualifiedSource scope (keyIdentifier catalog.Dialect name) span |> ignore


    // A linear correlated WHERE EXISTS graph does not require a CLR
    // Query→Expr→Query callback. Only select shapes whose other expressions
    // are terminal participate; everything else retains full validation.
    let private tryLinearExists (query: Query) =
        let terminalProjection =
            query.Head.Projection |> List.forall (fun item ->
                match Expr.unspan item.Expression with
                | Literal _ | Column _ | BoundColumn _ | Wildcard _
                | OrderOrdinal _ | Interval _ -> true
                | _ -> false)
        if query.Head.Ctes.IsEmpty && query.SetOperations.IsEmpty &&
           query.Head.Joins.IsEmpty && query.Head.GroupBy.IsEmpty &&
           query.Head.Having.IsNone && query.OrderBy.IsEmpty &&
           query.Head.DistinctMode = SelectDistinct.AllRows && terminalProjection then
            match query.Head.From, query.Head.Where |> Option.map Expr.unspan with
            | (None | Some(NamedTable _ | CteTable _)), Some(Exists(inner, _)) -> Some inner
            | _ -> None
        else None


    // Match only the terminal correlated IN shape whose left operand can
    // be checked before suspending into the child query.
    let private tryLinearIn (query: Query) =
        let terminalProjection =
            query.Head.Projection |> List.forall (fun item ->
                match Expr.unspan item.Expression with
                | Literal _ | Column _ | BoundColumn _ | Wildcard _
                | OrderOrdinal _ | Interval _ -> true
                | _ -> false)
        if query.Head.Ctes.IsEmpty && query.SetOperations.IsEmpty &&
           query.Head.Joins.IsEmpty && query.Head.GroupBy.IsEmpty &&
           query.Head.Having.IsNone && query.OrderBy.IsEmpty &&
           query.Head.DistinctMode = SelectDistinct.AllRows && terminalProjection then
            match query.Head.From, query.Head.Where |> Option.map Expr.unspan with
            | Some (NamedTable _ | CteTable _), Some(InSubquery(value, inner, false)) ->
                match Expr.unspan value with
                | Column _ | BoundColumn _ -> Some(value, inner)
                | _ -> None
            | _ -> None
        else None


    // A projection consisting solely of a scalar Query can be resumed
    // after its inner Query proof. No row source or other expression is
    // skipped; other projection shapes retain the full proof traversal.
    let private tryLinearScalarProjection (query: Query) =
        let head = query.Head
        if head.Ctes.IsEmpty && query.SetOperations.IsEmpty &&
           head.DistinctMode = SelectDistinct.AllRows &&
           head.From.IsNone && head.Joins.IsEmpty &&
           head.Where.IsNone && head.GroupBy.IsEmpty && head.Having.IsNone &&
           query.OrderBy.IsEmpty && query.Limit.IsNone && query.Offset.IsNone &&
           query.FetchPercent.IsNone && not query.FetchWithTies then
            match head.Projection with
            | [ { Alias = None; Expression = projection } ] ->
                match Expr.unspan projection with
                | ScalarSubquery child -> Some child
                | _ -> None
            | _ -> None
        else None


    // Only one CASE branch with one suspended scalar Query participates.
    // The condition and fallback remain fully validated and type-checked.
    let private tryLinearCaseProjection (query: Query) =
        let head = query.Head
        if head.Ctes.IsEmpty && query.SetOperations.IsEmpty &&
           head.DistinctMode = SelectDistinct.AllRows &&
           head.From.IsNone && head.Joins.IsEmpty &&
           head.Where.IsNone && head.GroupBy.IsEmpty && head.Having.IsNone &&
           query.OrderBy.IsEmpty && query.Limit.IsNone && query.Offset.IsNone &&
           query.FetchPercent.IsNone && not query.FetchWithTies then
            match head.Projection with
            | [ { Alias = None; Expression = projection } ] ->
                match Expr.unspan projection with
                | SearchedCase(branches, Some fallback) ->
                    match NonEmpty.toList branches, Expr.unspan fallback with
                    | [ branch ], Literal _ ->
                        match Expr.unspan branch.Result, Expr.unspan branch.Condition with
                        | ScalarSubquery inner, Binary _ ->
                            Some(branch.Condition, fallback, inner)
                        | _ -> None
                    | _ -> None
                | _ -> None
            | _ -> None
        else None

    // Visit scalar children on an explicit heap stack. Query proof still
    // crosses through queryColumns, which has its own stack-reserve guard.
    type private SchemaExprFrame =
        | VisitSchemaExpr of Span * Expr
        | VisitSchemaQuery of Query

    type private SchemaQueryFrame =
        | ResumeSchemaCte of Map<string, string list> * Scope option * Query * Cte
        | ResumeSchemaDerived of Map<string, string list> * Scope option * Query * Span * IdentifierPart
        | ResumeSchemaJoinedLateral of Map<string, string list> * Scope option * Query * Span * IdentifierPart * int
        | ResumeSchemaExists of string list
        | ResumeSchemaIn of string list
        | ResumeSchemaScalarProjection
        | ResumeSchemaCaseProjection

    let private createValidation () =
        let evaluator = RewriteScopedContinuations.Evaluator<string list>()
        let rec expression catalog ctes scope span expr =
            let pending = ResizeArray<SchemaExprFrame>()
            let inline push span expression = pending.Add(VisitSchemaExpr(span, expression))
            let pushReverse values =
                let expressions = List.toArray values
                for index = expressions.Length - 1 downto 0 do
                    push Span.unknown expressions.[index]
            push span expr
            while pending.Count > 0 do
                RewriteResourceBudget.checkpoint ()
                let last = pending.Count - 1
                let current = pending.[last]
                pending.RemoveAt last
                match current with
                | VisitSchemaQuery query ->
                    queryColumns catalog ctes (Some scope) query |> ignore
                | VisitSchemaExpr(currentSpan, expression) ->
                    match expression with
                    | Spanned(span, inner) -> push span inner
                    | Column name -> column catalog scope currentSpan name
                    | BoundColumn(_, ColumnBinding.ProjectionAlias) -> ()
                    | BoundColumn(name, _) -> column catalog scope currentSpan name
                    | Wildcard qualifier -> wildcard catalog scope currentSpan qualifier
                    | OrderOrdinal _ | Literal _ | Interval _ -> ()
                    | DateAdd(_, a, b) | DateDiff(_, a, b)
                    | Binary(_, a, b) | RegexMatch(a, b) | FilteredAggregate(a, b)
                    | Like(a, b, _, _, _) ->
                        push Span.unknown b
                        push Span.unknown a
                    | Unary(_, a) | Cast(a, _) | Extract(_, a)
                    | PostgresJsonAccess(a, _, _) | IsNull(a, _) ->
                        push Span.unknown a
                    | RawRegexCall(args, _) -> pushReverse args
                    | FunctionCall call ->
                        pushReverse (call.AggregateOrderBy |> List.map (fun order -> order.Expression))
                        pushReverse call.Arguments
                    | Windowed(value, window) ->
                        pushReverse (window.OrderBy |> List.map (fun order -> order.Expression))
                        pushReverse window.PartitionBy
                        push Span.unknown value
                    | SimpleCase(value, branches, fallback) ->
                        fallback |> Option.iter (push Span.unknown)
                        branches |> NonEmpty.iterReverse (fun branch ->
                            push Span.unknown branch.Result
                            push Span.unknown branch.Match)
                        push Span.unknown value
                    | SearchedCase(branches, fallback) ->
                        fallback |> Option.iter (push Span.unknown)
                        branches |> NonEmpty.iterReverse (fun branch ->
                            push Span.unknown branch.Result
                            push Span.unknown branch.Condition)
                    | InList(value, items, _) ->
                        items |> NonEmpty.iterReverse (push Span.unknown)
                        push Span.unknown value
                    | InSubquery(value, query, _) ->
                        pending.Add(VisitSchemaQuery query)
                        push Span.unknown value
                    | Between(value, lower, upper, _) ->
                        push Span.unknown upper
                        push Span.unknown lower
                        push Span.unknown value
                    | ScalarSubquery query | Exists(query, _) ->
                        pending.Add(VisitSchemaQuery query)

        and source catalog ctes parent value =
            match value with
            | NamedTable(name, alias) ->
                namedSource catalog.Dialect name alias (readableTable catalog name)
            | CteTable(name, alias) ->
                match Map.tryFind (keyIdentifier catalog.Dialect name) ctes with
                | Some columns -> namedSource catalog.Dialect name alias columns
                | None ->
                    reject "SQL_SCHEMA_CTE_UNPROVABLE" Span.unknown (
                        "CTE '" + Identifier.text name + "' is not in the proven scope.")
            | DerivedTable(query, alias) ->
                queryColumns catalog ctes None query
                |> requireNamedProjection query.Span
                |> aliasedSource catalog.Dialect alias
            | LateralDerivedTable(query, alias) ->
                queryColumns catalog ctes parent query
                |> requireNamedProjection query.Span
                |> aliasedSource catalog.Dialect alias

        and requireNamedProjection span names =
            if names |> List.exists String.IsNullOrEmpty then
                reject "SQL_SCHEMA_SHAPE_UNPROVABLE" span (
                    "Computed columns in CTEs and derived tables need explicit aliases.")
            names

        and selectColumns catalog inheritedCtes parent (select: Select) =
            selectColumnsWithFirst catalog inheritedCtes parent select None

        and selectColumnsWithFirst catalog inheritedCtes parent (select: Select) firstOverride =
            selectColumnsWithJoinOverride catalog inheritedCtes parent select firstOverride None

        // A previously proved JOIN LATERAL source can be supplied at its exact
        // index. Earlier joins must be proved in their original lexical scope
        // before the child query; all JOIN predicates are rechecked in order.
        and selectColumnsWithJoinOverride catalog inheritedCtes parent (select: Select) firstOverride joinOverride =
            let mutable ctes = inheritedCtes
            for cte in select.Ctes do
                if cte.RecursiveScope then
                    reject "SQL_SCHEMA_RECURSIVE_CTE_UNPROVABLE" cte.Span (
                        "Recursive CTE schema projection is not proven by this strict metadata validator.")
                let columns = queryColumns catalog ctes None cte.Query
                let names =
                    match cte.ColumnAliases with
                    | [] -> requireNamedProjection cte.Span columns
                    | aliases when aliases.Length = columns.Length ->
                        aliases |> List.map (keyPart catalog.Dialect)
                    | _ ->
                        reject "SQL_SCHEMA_SHAPE_UNPROVABLE" cte.Span "CTE projection and column-alias widths differ."
                ctes <- Map.add (keyPart catalog.Dialect cte.Name) names ctes

            let first =
                match firstOverride with
                | Some resolved -> makeScope parent [ resolved ]
                | None ->
                    match select.From with
                    | None -> makeScope parent []
                    | Some item -> makeScope parent [ source catalog ctes parent item ]
            let mutable current = first
            let mutable joinIndex = 0
            for join in select.Joins do
                let added =
                    match joinOverride with
                    | Some (targetIndex, resolved) when targetIndex = joinIndex -> resolved
                    | _ ->
                        let sourceParent =
                            match join.Source with
                            | LateralDerivedTable _ -> Some current
                            | _ -> current.Parent
                        source catalog ctes sourceParent join.Source
                joinIndex <- joinIndex + 1
                let mergeKeys =
                    match join with
                    | UsingJoin(_, _, columns) ->
                        Some(columns |> NonEmpty.toList |> List.map (keyPart catalog.Dialect))
                    | NaturalJoin _ ->
                        // NATURAL JOIN is schema-dependent: derive its implicit USING list
                        // from the complete left row shape, in left-column order.
                        let right = Set.ofList added.Columns
                        Some(current.Unqualified |> List.filter right.Contains)
                    | _ -> None
                let unqualified =
                    match mergeKeys with
                    | None -> current.Unqualified @ added.Columns
                    | Some keys ->
                        for key in keys do
                            // A merged key must belong to the current join inputs, not an
                            // outer scope. Duplicate left/right keys cannot be proven.
                            resolveColumn { current with Parent = None } None key select.Span
                            if countColumn added key <> 1 then
                                reject "SQL_SCHEMA_COLUMN_NOT_FOUND" select.Span (
                                    "JOIN merged column '" + key + "' is missing or ambiguous on the joined table.")
                        let isMerged name = List.contains name keys
                        keys
                        @ (current.Unqualified |> List.filter (not << isMerged))
                        @ (added.Columns |> List.filter (not << isMerged))
                let combined =
                    { current with
                        Sources = current.Sources @ [ added ]
                        Unqualified = unqualified }
                match join with
                | OnJoin(_, _, predicate) -> expression catalog ctes combined Span.unknown predicate
                | UsingJoin _ | NaturalJoin _ | CrossJoin _ -> ()
                current <- combined

            let inspect = expression catalog ctes current Span.unknown
            match select.DistinctMode with
            | SelectDistinct.DistinctOn values -> values |> NonEmpty.iter inspect
            | _ -> ()
            select.Projection |> List.iter (fun item -> inspect item.Expression)
            select.Where |> Option.iter inspect
            select.GroupBy |> List.iter inspect
            select.Having |> Option.iter inspect
            let output =
                select.Projection
                |> List.collect (fun item ->
                    match item.Alias, Expr.unspan item.Expression with
                    | Some alias, _ -> [ keyPart catalog.Dialect alias ]
                    | None, Column name | None, BoundColumn(name, _) ->
                        [ Identifier.parts name |> List.last |> keyPart catalog.Dialect ]
                    | None, Wildcard None -> current.Unqualified
                    | None, Wildcard(Some qualifier) ->
                        (qualifiedSource current (keyIdentifier catalog.Dialect qualifier) select.Span).Columns
                    | _ -> [ String.Empty ])
            current, ctes, output

        // A single-CTE nesting chain has post-order projection dependencies
        // but no lateral/correlated dependency between its query bodies.
        // Unwind with heap continuations rather than consuming CLR call stack.
        // Multi-CTE, SET and derived-source shapes use the full proof path.
        and queryColumns catalog ctes parent (query: Query) =
            let action () = queryColumnsCore catalog ctes parent query
            if RewriteAstTraversal.isLocalQuery query then
                evaluator.EvaluateLocal action
            else evaluator.Evaluate(action, false)

        and queryColumnsCore catalog ctes parent (query: Query) =
            RewriteStackSafety.compile SqlDiagnosticStage.Binding
            let pending = ResizeArray<SchemaQueryFrame>()
            let mutable current = query
            let mutable currentParent = parent
            let mutable scanning = true
            while scanning do
                RewriteResourceBudget.checkpoint ()
                match current.Head.Ctes, current.SetOperations, tryLinearExists current, tryLinearIn current, tryLinearScalarProjection current, tryLinearCaseProjection current with
                | [], [], Some inner, _, _, _ ->
                    // Schema projection/row-source validation precedes WHERE.
                    let withoutWhere = { current.Head with Where = None }
                    let scope, _, columns =
                        selectColumns catalog ctes currentParent withoutWhere
                    pending.Add(ResumeSchemaExists columns)
                    currentParent <- Some scope
                    current <- inner
                | [], [], _, Some(value, inner), _, _ ->
                    let withoutWhere = { current.Head with Where = None }
                    let scope, _, columns =
                        selectColumns catalog ctes currentParent withoutWhere
                    expression catalog ctes scope Span.unknown value
                    pending.Add(ResumeSchemaIn columns)
                    currentParent <- Some scope
                    current <- inner
                | [], [], _, _, Some inner, _ ->
                    pending.Add ResumeSchemaScalarProjection
                    currentParent <- Some(makeScope currentParent [])
                    current <- inner
                | [], [], _, _, _, Some(condition, fallback, inner) ->
                    let scope = makeScope currentParent []
                    expression catalog ctes scope Span.unknown condition
                    expression catalog ctes scope Span.unknown fallback
                    pending.Add ResumeSchemaCaseProjection
                    currentParent <- Some scope
                    current <- inner
                | [ cte ], [], _, _, _, _ when not cte.RecursiveScope ->
                    pending.Add(ResumeSchemaCte(ctes, currentParent, current, cte))
                    current <- cte.Query
                    currentParent <- None
                | [], [], _, _, _, _ when not current.Head.Joins.IsEmpty ->
                    match current.Head.From, List.rev current.Head.Joins with
                    | Some ((NamedTable _ | CteTable _) as first),
                      OnJoin(OnJoinKind.Inner, LateralDerivedTable(inner, alias), _) :: precedingReversed
                        when precedingReversed |> List.forall (function
                            | OnJoin(OnJoinKind.Inner, (NamedTable _ | CteTable _ | LateralDerivedTable _), _) -> true
                            | _ -> false) ->
                        // Replay arbitrary INNER JOIN prefixes on the current
                        // row-scope only; the deepest final LATERAL is suspended
                        // on the Query heap. Earlier LATERALs see only the FROM
                        // and JOIN sources that precede them, not future aliases.
                        let mutable visible =
                            makeScope currentParent [ source catalog ctes currentParent first ]
                        for preceding in List.rev precedingReversed do
                            RewriteResourceBudget.checkpoint ()
                            match preceding with
                            | OnJoin(_, table, predicate) ->
                                let sourceParent =
                                    match table with
                                    | LateralDerivedTable _ -> Some visible
                                    | _ -> currentParent
                                let added = source catalog ctes sourceParent table
                                let combined =
                                    { visible with
                                        Sources = visible.Sources @ [ added ]
                                        Unqualified = visible.Unqualified @ added.Columns }
                                expression catalog ctes combined Span.unknown predicate
                                visible <- combined
                            | _ -> invalidOp "Proven JOIN prefix requires an INNER JOIN ON predicate."
                        let lastIndex = current.Head.Joins.Length - 1
                        pending.Add(ResumeSchemaJoinedLateral(ctes, currentParent, current, inner.Span, alias, lastIndex))
                        currentParent <- Some visible
                        current <- inner
                    | _ -> scanning <- false
                | [], [], _, _, _, _ when current.Head.Joins.IsEmpty ->
                    match current.Head.From with
                    | Some (DerivedTable(inner, alias)) ->
                        pending.Add(ResumeSchemaDerived(
                            ctes, currentParent, current, inner.Span, alias))
                        current <- inner
                        // Ordinary derived sources never inherit outer correlation.
                        currentParent <- None
                    | Some (LateralDerivedTable(inner, alias)) ->
                        // A primary FROM LATERAL has no preceding local
                        // source. Its input may still see the parent's outer
                        // scope; unlike ordinary derived, do not erase it.
                        pending.Add(ResumeSchemaDerived(
                            ctes, currentParent, current, inner.Span, alias))
                        current <- inner
                    | _ -> scanning <- false
                | _ -> scanning <- false

            let mutable projected = queryColumnsSingle catalog ctes currentParent current
            for index = pending.Count - 1 downto 0 do
                RewriteResourceBudget.checkpoint ()
                match pending.[index] with
                | ResumeSchemaCte(inherited, originalParent, outer, cte) ->
                    let names =
                        match cte.ColumnAliases with
                        | [] -> requireNamedProjection cte.Span projected
                        | aliases when aliases.Length = projected.Length ->
                            aliases |> List.map (keyPart catalog.Dialect)
                        | _ ->
                            reject "SQL_SCHEMA_SHAPE_UNPROVABLE" cte.Span
                                "CTE projection and column-alias widths differ."
                    let visible = Map.add (keyPart catalog.Dialect cte.Name) names inherited
                    let withoutCte = { outer.Head with Ctes = [] }
                    let scope, afterCtes, columns =
                        selectColumns catalog visible originalParent withoutCte
                    outer.OrderBy |> List.iter (fun item ->
                        expression catalog afterCtes scope Span.unknown item.Expression)
                    projected <- columns
                | ResumeSchemaDerived(inherited, originalParent, outer, innerSpan, alias) ->
                    let columns = requireNamedProjection innerSpan projected
                    let resolved = aliasedSource catalog.Dialect alias columns
                    let scope, visibleCtes, output =
                        selectColumnsWithFirst catalog inherited originalParent
                            outer.Head (Some resolved)
                    outer.OrderBy |> List.iter (fun item ->
                        expression catalog visibleCtes scope Span.unknown item.Expression)
                    projected <- output
                | ResumeSchemaJoinedLateral(inherited, originalParent, outer, innerSpan, alias, joinIndex) ->
                    let resolved =
                        projected
                        |> requireNamedProjection innerSpan
                        |> aliasedSource catalog.Dialect alias
                    let scope, visibleCtes, output =
                        selectColumnsWithJoinOverride catalog inherited originalParent
                            outer.Head None (Some(joinIndex, resolved))
                    outer.OrderBy |> List.iter (fun item ->
                        expression catalog visibleCtes scope Span.unknown item.Expression)
                    projected <- output
                | ResumeSchemaExists columns
                | ResumeSchemaIn columns ->
                    projected <- columns
                | ResumeSchemaScalarProjection
                | ResumeSchemaCaseProjection ->
                    projected <- [ String.Empty ]
            projected

        and queryColumnsSingle catalog ctes parent (query: Query) =
            RewriteStackSafety.compile SqlDiagnosticStage.Binding
            let scope, visibleCtes, output = selectColumns catalog ctes parent query.Head
            // A SELECT's set-operation branches form a Query graph, but checking
            // them need not consume one CLR stack frame per nested set term.
            // Schedule exactly the former DFS order: a branch's head, then its
            // nested branches, then its ORDER BY and finally the parent's width
            // check. Expressions and source/CTE proofs retain their own scopes.
            let pending = ResizeArray<unit -> unit>()
            let rec scheduleBranches inheritedCtes inheritedParent expectedWidth branches =
                let reversed = List.rev branches
                for branch in reversed do
                    pending.Add(fun () ->
                        let child = branch.Query
                        let childScope, childCtes, childOutput =
                            selectColumns catalog inheritedCtes inheritedParent child.Head
                        pending.Add(fun () ->
                            if childOutput.Length <> expectedWidth then
                                reject "SQL_SCHEMA_SHAPE_UNPROVABLE" child.Span (
                                    "SET-operation branch has an unproven output width."))
                        pending.Add(fun () ->
                            child.OrderBy |> List.iter (fun item ->
                                expression catalog childCtes childScope Span.unknown item.Expression))
                        scheduleBranches childCtes inheritedParent childOutput.Length child.SetOperations)
            pending.Add(fun () ->
                query.OrderBy |> List.iter (fun item ->
                    expression catalog visibleCtes scope Span.unknown item.Expression))
            scheduleBranches visibleCtes parent output.Length query.SetOperations
            while pending.Count > 0 do
                RewriteResourceBudget.checkpoint ()
                let last = pending.Count - 1
                let next = pending.[last]
                pending.RemoveAt(last)
                next()
            output

        let returning catalog target (items: ReturningItem list) =
            let scope = makeScope None [ target ]
            items |> List.iter (function
                | ReturningColumn(name, _) -> column catalog scope Span.unknown name
                | ReturningWildcard _ -> ()
                | ReturningExpression(value, _) ->
                    expression catalog Map.empty scope Span.unknown value)

        let document catalog (doc: Document) =
            let emptyCtes = Map.empty<string, string list>
            let emptyScope = makeScope None []
            match doc.Statement with
            | QueryStatement query ->
                queryColumns catalog emptyCtes None query |> ignore
            | InsertStatement insert ->
                let target = namedSource catalog.Dialect insert.Target None (physicalTable catalog insert.Target)
                insert.Columns |> List.iter (fun name ->
                    if countColumn target (keyPart catalog.Dialect name) <> 1 then
                        reject "SQL_SCHEMA_COLUMN_NOT_FOUND" doc.Span (
                            "INSERT column '" + name.Value + "' is not in the target table."))
                // The target role is always physical. Explicit column lists
                // are already checked by the parser; only Virtual Catalog
                // DDL can prove the order of an omitted column list.
                let layout =
                    RewriteInsertTargetLayout.select
                        (insert.Columns |> List.map (keyPart catalog.Dialect))
                        target.Columns catalog.ProvenInsertColumnOrder
                let checkWidth width =
                    match layout with
                    | ProvenTargets columns when width <> columns.Length ->
                        reject "SQL_SCHEMA_SHAPE_UNPROVABLE" doc.Span (
                            "INSERT source width differs from its proven target-column layout.")
                    | _ -> ()
                match insert.Input with
                | Values rows ->
                    rows |> NonEmpty.iter (fun row ->
                        let values = NonEmpty.toList row
                        values |> List.iter (
                            expression catalog emptyCtes emptyScope Span.unknown)
                        checkWidth values.Length)
                | QuerySource query ->
                    let projected = queryColumns catalog emptyCtes None query
                    checkWidth projected.Length
                | DefaultValues -> ()
                if insert.Conflict.IsSome then
                    reject "SQL_SCHEMA_CONFLICT_UNPROVABLE" doc.Span (
                        "Strict snapshot mode does not yet prove UPSERT key and assignment columns.")
                returning catalog target insert.Returning
            | UpdateStatement update ->
                let target = namedSource catalog.Dialect update.Target update.TargetAlias (physicalTable catalog update.Target)
                let otherSources = update.From |> List.map (source catalog emptyCtes None)
                let current = makeScope None (target :: otherSources)
                let targetScope = makeScope None [ target ]
                update.Assignments |> List.iter (fun assignment ->
                    column catalog targetScope doc.Span assignment.Target
                    expression catalog emptyCtes current Span.unknown assignment.Value)
                update.Where |> Option.iter (expression catalog emptyCtes current Span.unknown)
                returning catalog target update.Returning
            | DeleteStatement delete ->
                let target = namedSource catalog.Dialect delete.Target delete.TargetAlias (physicalTable catalog delete.Target)
                let others = delete.Using |> List.map (source catalog emptyCtes None)
                let current = makeScope None (target :: others)
                delete.Where |> Option.iter (expression catalog emptyCtes current Span.unknown)
                returning catalog target delete.Returning
            | MergeStatement _ ->
                reject "SQL_SCHEMA_MERGE_UNPROVABLE" doc.Span (
                    "Strict snapshot mode does not yet prove MERGE source and target key semantics.")


        document

    let verify dialect snapshot (bound: BoundSql) =
        let catalog = buildCatalog dialect snapshot
        (createValidation ()) catalog (Bound.value bound)
        bound

    /// Static analysis only. A complete Virtual Catalog is consumed directly,
    /// without fabricating a runtime SqlSchemaSnapshot or execution proof.
    /// The same scope/name/CTE algorithm as host-provided snapshots is used.
    let verifyVirtual dialect (state: SqlCatalogState) (bound: BoundSql) =
        if not state.IsClosedWorld
           || (state.Tables |> Seq.exists (fun t -> t.Knowledge <> SqlCatalogKnowledge.Known)) then
            invalidArg "state" "Strict catalog binding requires a complete known closed-world catalog."
        let physical =
            state.Tables
            |> Seq.map (fun table ->
                RewriteResourceBudget.checkpoint ()
                table.QualifiedName.BinderKey,
                (table.Columns |> Seq.map (fun col -> catalogPart dialect col.Name) |> Seq.toList))
            |> Seq.toList
        // The read map is permitted to contain proven View OUTPUT columns.
        // The write map above intentionally does not: a View cannot become
        // a physical INSERT/UPDATE/DELETE target through this feature.
        let readable =
            seq {
                yield! physical
                for view in RewriteQueryAnalyzer.readableViews dialect state bound do
                    RewriteResourceBudget.checkpoint ()
                    yield view.QualifiedName.BinderKey,
                          (view.Columns |> Seq.map (fun col -> catalogPart dialect col.Name) |> Seq.toList)
                for view in RewriteMutationAnalyzer.readableViews dialect state bound do
                    RewriteResourceBudget.checkpoint ()
                    yield view.QualifiedName.BinderKey,
                          (view.Columns |> Seq.map (fun col -> catalogPart dialect col.Name) |> Seq.toList)
            }
            |> Seq.toList
        if physical.Length <> (physical |> List.map fst |> List.distinct |> List.length)
           || readable.Length <> (readable |> List.map fst |> List.distinct |> List.length) then
            invalidArg "state" "Virtual Catalog contains duplicate structured identifier identities."
        let catalog =
            { Dialect = dialect
              Tables = Map.ofList physical
              ReadRelations = Map.ofList readable
              ProvenInsertColumnOrder = true }
        (createValidation ()) catalog (Bound.value bound)
        bound
