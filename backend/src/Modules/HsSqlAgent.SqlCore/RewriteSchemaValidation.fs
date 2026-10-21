namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Optional metadata-backed name checking on the already bound closed AST.
/// It never queries a database or falls back to the compatibility AST.
module internal RewriteSchemaValidation =

    type private Catalog = { Dialect: SqlAgentToolType; Tables: Map<string, string list> }
    type private Source = { Qualifiers: string list; Columns: string list }
    type private Scope = { Parent: Scope option; Sources: Source list }

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
        match dialect with
        | SqlAgentToolType.MySQL | SqlAgentToolType.MsSqlServer | SqlAgentToolType.Sqlite ->
            value.ToUpperInvariant()
        | _ -> value

    let private keyIdentifier dialect identifier =
        Identifier.parts identifier |> List.map (keyPart dialect) |> String.concat "."

    let private keyCatalog dialect (value: string) =
        value.Split('.') |> Array.map (catalogPart dialect) |> String.concat "."

    let private buildCatalog dialect (snapshot: SqlSchemaSnapshot) =
        if snapshot.Provider <> dialect then
            invalidArg "schemaSnapshot" (
                "Schema provider " + string snapshot.Provider + " does not match source " + string dialect + ".")
        let tables =
            snapshot.Tables
            |> Seq.map (fun table ->
                keyCatalog dialect table.Name,
                (table.Columns |> Seq.map (catalogPart dialect) |> Seq.toList))
            |> Seq.toList
        if (tables |> List.map fst |> List.distinct |> List.length) <> tables.Length then
            invalidArg "schemaSnapshot" "Schema table names collide under provider identifier rules."
        tables |> List.iter (fun (name, columns) ->
            if (columns |> List.distinct |> List.length) <> columns.Length then
                invalidArg "schemaSnapshot" ("Schema columns collide in table '" + name + "'."))
        { Dialect = dialect; Tables = Map.ofList tables }

    let private physicalTable catalog name =
        match Map.tryFind (keyIdentifier catalog.Dialect name) catalog.Tables with
        | Some columns -> columns
        | None ->
            reject "SQL_SCHEMA_TABLE_NOT_FOUND" Span.unknown (
                "Schema does not contain physical table '" + Identifier.text name + "'.")

    let private namedSource dialect name alias columns =
        let full = keyIdentifier dialect name
        let tail = Identifier.parts name |> List.last |> keyPart dialect
        let keys =
            match alias with
            | Some value -> [ keyPart dialect value ]
            | None when tail = full -> [ full ]
            | None -> [ full; tail ]
        { Qualifiers = keys; Columns = columns }

    let private aliasedSource dialect alias columns =
        { Qualifiers = [ keyPart dialect alias ]; Columns = columns }

    let private countColumn source key = source.Columns |> List.filter ((=) key) |> List.length

    let rec private resolveColumn scope qualifier key span =
        let matchingSources =
            match qualifier with
            | None -> scope.Sources
            | Some name -> scope.Sources |> List.filter (fun item -> List.contains name item.Qualifiers)
        let hits = matchingSources |> List.sumBy (fun item -> countColumn item key)
        if hits > 1 then
            reject "SQL_SCHEMA_COLUMN_AMBIGUOUS" span (
                "Column '" + key + "' is ambiguous across the current schema scope.")
        elif hits = 1 then ()
        else
            match qualifier, scope.Parent with
            | None, Some parent -> resolveColumn parent None key span
            | Some name, Some parent when matchingSources.IsEmpty ->
                resolveColumn parent (Some name) key span
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

    let rec private qualifiedSource scope name span =
        match scope.Sources |> List.filter (fun source -> List.contains name source.Qualifiers) with
        | [ found ] -> found
        | [] ->
            match scope.Parent with
            | Some parent -> qualifiedSource parent name span
            | None -> reject "SQL_SCHEMA_QUALIFIER_UNPROVABLE" span ("Unknown table qualifier '" + name + "'.")
        | _ -> reject "SQL_SCHEMA_QUALIFIER_UNPROVABLE" span ("Ambiguous table qualifier '" + name + "'.")

    let private wildcard catalog scope span qualifier =
        match qualifier with
        | None when scope.Sources.IsEmpty ->
            reject "SQL_SCHEMA_SHAPE_UNPROVABLE" span "Unqualified wildcard has no row source."
        | None -> ()
        | Some name -> qualifiedSource scope (keyIdentifier catalog.Dialect name) span |> ignore

    let rec private expression catalog ctes scope span expr =
        RewriteStackSafety.compile SqlDiagnosticStage.Binding
        let child = expression catalog ctes scope Span.unknown
        match expr with
        | Spanned(current, value) -> expression catalog ctes scope current value
        | Column name -> column catalog scope span name
        | BoundColumn(_, ColumnBinding.ProjectionAlias) -> ()
        | BoundColumn(name, _) -> column catalog scope span name
        | Wildcard qualifier -> wildcard catalog scope span qualifier
        | OrderOrdinal _ | Literal _ | Interval _ -> ()
        | DateAdd(_, a, b) | DateDiff(_, a, b)
        | Binary(_, a, b) | RegexMatch(a, b) | FilteredAggregate(a, b) ->
            child a; child b
        | Unary(_, a) | Cast(a, _) | Extract(_, a)
        | PostgresJsonAccess(a, _, _) | IsNull(a, _) -> child a
        | Like(a, b, _, _, _) -> child a; child b
        | RawRegexCall(args, _) -> args |> List.iter child
        | FunctionCall call ->
            call.Arguments |> List.iter child
            call.AggregateOrderBy |> List.iter (fun order -> child order.Expression)
        | Windowed(value, window) ->
            child value
            window.PartitionBy |> List.iter child
            window.OrderBy |> List.iter (fun order -> child order.Expression)
        | SimpleCase(value, branches, fallback) ->
            child value
            branches |> NonEmpty.iter (fun branch -> child branch.Match; child branch.Result)
            fallback |> Option.iter child
        | SearchedCase(branches, fallback) ->
            branches |> NonEmpty.iter (fun branch -> child branch.Condition; child branch.Result)
            fallback |> Option.iter child
        | InList(value, items, _) -> child value; items |> NonEmpty.iter child
        | InSubquery(value, query, _) ->
            child value
            queryColumns catalog ctes (Some scope) query |> ignore
        | Between(value, a, b, _) -> child value; child a; child b
        | ScalarSubquery query | Exists(query, _) ->
            queryColumns catalog ctes (Some scope) query |> ignore

    and private source catalog ctes parent value =
        match value with
        | NamedTable(name, alias) ->
            namedSource catalog.Dialect name alias (physicalTable catalog name)
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

    and private requireNamedProjection span names =
        if names |> List.exists String.IsNullOrEmpty then
            reject "SQL_SCHEMA_SHAPE_UNPROVABLE" span (
                "Computed columns in CTEs and derived tables need explicit aliases.")
        names

    and private selectColumns catalog inheritedCtes parent (select: Select) =
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
            match select.From with
            | None -> { Parent = parent; Sources = [] }
            | Some item ->
                { Parent = parent; Sources = [ source catalog ctes parent item ] }
        let mutable current = first
        for join in select.Joins do
            let sourceParent =
                match join.Source with
                | LateralDerivedTable _ -> Some current
                | _ -> current.Parent
            let added = source catalog ctes sourceParent join.Source
            let combined = { current with Sources = current.Sources @ [ added ] }
            match join with
            | OnJoin(_, _, predicate) -> expression catalog ctes combined Span.unknown predicate
            | UsingJoin(_, _, columns) ->
                columns |> NonEmpty.iter (fun item ->
                    let key = keyPart catalog.Dialect item
                    resolveColumn current None key select.Span
                    if countColumn added key <> 1 then
                        reject "SQL_SCHEMA_COLUMN_NOT_FOUND" select.Span (
                            "JOIN USING column '" + key + "' is missing on the joined table."))
            | NaturalJoin _ ->
                reject "SQL_SCHEMA_JOIN_UNPROVABLE" select.Span (
                    "Strict metadata validation does not yet prove NATURAL JOIN coalesced column semantics.")
            | CrossJoin _ -> ()
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
                | None, Wildcard None ->
                    current.Sources |> List.collect (fun item -> item.Columns)
                | None, Wildcard(Some qualifier) ->
                    (qualifiedSource current (keyIdentifier catalog.Dialect qualifier) select.Span).Columns
                | _ -> [ String.Empty ])
        current, ctes, output

    and private queryColumns catalog ctes parent (query: Query) =
        let scope, visibleCtes, output = selectColumns catalog ctes parent query.Head
        query.SetOperations |> List.iter (fun branch ->
            let other = queryColumns catalog visibleCtes parent branch.Query
            if other.Length <> output.Length then
                reject "SQL_SCHEMA_SHAPE_UNPROVABLE" branch.Query.Span (
                    "SET-operation branch has an unproven output width."))
        query.OrderBy |> List.iter (fun item ->
            expression catalog visibleCtes scope Span.unknown item.Expression)
        output

    let private returning catalog target (items: ReturningItem list) =
        let scope = { Parent = None; Sources = [ target ] }
        items |> List.iter (function
            | ReturningColumn(name, _) -> column catalog scope Span.unknown name
            | ReturningWildcard _ -> ()
            | ReturningExpression(value, _) ->
                expression catalog Map.empty scope Span.unknown value)

    let private document catalog (doc: Document) =
        let emptyCtes = Map.empty<string, string list>
        let emptyScope = { Parent = None; Sources = [] }
        match doc.Statement with
        | QueryStatement query ->
            queryColumns catalog emptyCtes None query |> ignore
        | InsertStatement insert ->
            let target = namedSource catalog.Dialect insert.Target None (physicalTable catalog insert.Target)
            insert.Columns |> List.iter (fun name ->
                if countColumn target (keyPart catalog.Dialect name) <> 1 then
                    reject "SQL_SCHEMA_COLUMN_NOT_FOUND" doc.Span (
                        "INSERT column '" + name.Value + "' is not in the target table."))
            match insert.Input with
            | Values rows ->
                rows |> NonEmpty.iter (NonEmpty.iter (
                    expression catalog emptyCtes emptyScope Span.unknown))
            | QuerySource query ->
                let projected = queryColumns catalog emptyCtes None query
                if not insert.Columns.IsEmpty && projected.Length <> insert.Columns.Length then
                    reject "SQL_SCHEMA_SHAPE_UNPROVABLE" doc.Span (
                        "INSERT target-column count and SELECT projection width differ in strict snapshot mode.")
            | DefaultValues -> ()
            if insert.Conflict.IsSome then
                reject "SQL_SCHEMA_CONFLICT_UNPROVABLE" doc.Span (
                    "Strict snapshot mode does not yet prove UPSERT key and assignment columns.")
            returning catalog target insert.Returning
        | UpdateStatement update ->
            let target = namedSource catalog.Dialect update.Target update.TargetAlias (physicalTable catalog update.Target)
            let otherSources = update.From |> List.map (source catalog emptyCtes None)
            let current = { Parent = None; Sources = target :: otherSources }
            let targetScope = { Parent = None; Sources = [ target ] }
            update.Assignments |> List.iter (fun assignment ->
                column catalog targetScope doc.Span assignment.Target
                expression catalog emptyCtes current Span.unknown assignment.Value)
            update.Where |> Option.iter (expression catalog emptyCtes current Span.unknown)
            returning catalog target update.Returning
        | DeleteStatement delete ->
            let target = namedSource catalog.Dialect delete.Target delete.TargetAlias (physicalTable catalog delete.Target)
            let others = delete.Using |> List.map (source catalog emptyCtes None)
            let current = { Parent = None; Sources = target :: others }
            delete.Where |> Option.iter (expression catalog emptyCtes current Span.unknown)
            returning catalog target delete.Returning
        | MergeStatement _ ->
            reject "SQL_SCHEMA_MERGE_UNPROVABLE" doc.Span (
                "Strict snapshot mode does not yet prove MERGE source and target key semantics.")

    let verify dialect snapshot (bound: BoundSql) =
        let catalog = buildCatalog dialect snapshot
        document catalog (Bound.value bound)
        bound
