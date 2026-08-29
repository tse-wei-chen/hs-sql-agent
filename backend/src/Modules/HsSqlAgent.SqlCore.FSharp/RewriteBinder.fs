namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteBinder =

    type private SourceBinding =
        { Qualifiers: string list
          Alias: string option }

    type private Scope =
        { Id: int
          Parent: Scope option
          Sources: SourceBinding list
          VisibleCtes: string list }

    let private identifierParts = Identifier.parts
    let private identifierText = Identifier.text
    let private equalsName (left: string) (right: string) = StringComparer.OrdinalIgnoreCase.Equals(left, right)
    let private containsQualifier qualifier (source: SourceBinding) = source.Qualifiers |> List.exists (equalsName qualifier)
    let private containsName name values = values |> List.exists (equalsName name)

    let rec private qualifierExists qualifier (scope: Scope) =
        if scope.Sources |> List.exists (containsQualifier qualifier) then true
        else match scope.Parent with Some parent -> qualifierExists qualifier parent | None -> false

    let private scopeId parentScope = parentScope |> Option.map (fun (scope: Scope) -> scope.Id + 1) |> Option.defaultValue 0

    let private sourceBinding (source: TableSource) : SourceBinding =
        match source with
        | NamedTable(name, alias) | CteTable(name, alias) ->
            let parts = identifierParts name
            let tableTail = parts |> List.last |> fun part -> part.Value
            let fullName = identifierText name
            let qualifiers =
                match alias with
                | Some value -> [ value.Value ]
                | None when equalsName tableTail fullName -> [ tableTail ]
                | None -> [ tableTail; fullName ]
            { Qualifiers = qualifiers; Alias = alias |> Option.map (fun value -> value.Value) }
        | DerivedTable(_, alias) -> { Qualifiers = [ alias.Value ]; Alias = Some alias.Value }

    let private ensureDistinctAliases (scope: Scope) =
        let aliases = scope.Sources |> List.choose (fun source -> source.Alias)
        aliases
        |> List.iteri (fun index alias ->
            aliases
            |> List.skip (index + 1)
            |> List.tryFind (equalsName alias)
            |> Option.iter (fun _ -> invalidOp ("Duplicate table alias '" + alias + "' in SQL scope " + string scope.Id + ".")))

    let private ensureQualifier scope qualifier context =
        if not (qualifierExists qualifier scope) then
            invalidOp (context + " references unknown table/alias qualifier '" + qualifier + "'.")

    let private bindColumn (scope: Scope) (identifier: Identifier) : Identifier =
        match identifierParts identifier with
        | [] -> invalidOp "Column identifier cannot be empty."
        | [ _ ] -> identifier
        | parts ->
            let qualifier = parts |> List.take (parts.Length - 1) |> List.map (fun part -> part.Value) |> String.concat "."
            ensureQualifier scope qualifier ("Column '" + identifierText identifier + "'")
            identifier

    let rec private bindExpr (scope: Scope) (expression: Expr) : Expr =
        match expression with
        | Column identifier -> Column(bindColumn scope identifier)
        | Wildcard(Some identifier) ->
            let qualifier = identifierText identifier
            ensureQualifier scope qualifier ("Wildcard '" + qualifier + ".*'")
            expression
        | Wildcard None | OrderOrdinal _ | Literal _ | Interval _ -> expression
        | Unary(op, operand) -> Unary(op, bindExpr scope operand)
        | Binary(op, left, right) -> Binary(op, bindExpr scope left, bindExpr scope right)
        | Like(value, pattern, escape, negated, caseInsensitive) ->
            Like(bindExpr scope value, bindExpr scope pattern, escape |> Option.map (bindExpr scope), negated, caseInsensitive)
        | FunctionCall call -> FunctionCall { call with Arguments = call.Arguments |> List.map (bindExpr scope) }
        | FilteredAggregate(value, predicate) -> FilteredAggregate(bindExpr scope value, bindExpr scope predicate)
        | Windowed(value, window) -> Windowed(bindExpr scope value, bindWindow scope window)
        | Cast(value, targetType) -> Cast(bindExpr scope value, targetType)
        | Extract(field, value) -> Extract(field, bindExpr scope value)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(bindExpr scope input, branches |> NonEmpty.map (fun (branch: SimpleCaseBranch) -> { Match = bindExpr scope branch.Match; Result = bindExpr scope branch.Result }), fallback |> Option.map (bindExpr scope))
        | SearchedCase(branches, fallback) ->
            SearchedCase(branches |> NonEmpty.map (fun (branch: SearchedCaseBranch) -> { Condition = bindExpr scope branch.Condition; Result = bindExpr scope branch.Result }), fallback |> Option.map (bindExpr scope))
        | InList(value, items, negated) -> InList(bindExpr scope value, items |> NonEmpty.map (bindExpr scope), negated)
        | InSubquery(value, query, negated) -> InSubquery(bindExpr scope value, bindQuery (Some scope) scope.VisibleCtes query, negated)
        | Between(value, lower, upper, negated) -> Between(bindExpr scope value, bindExpr scope lower, bindExpr scope upper, negated)
        | IsNull(value, negated) -> IsNull(bindExpr scope value, negated)
        | ScalarSubquery query -> ScalarSubquery(bindQuery (Some scope) scope.VisibleCtes query)
        | Exists(query, negated) -> Exists(bindQuery (Some scope) scope.VisibleCtes query, negated)

    and private bindOrderBy (scope: Scope) (orderBy: OrderBy) : OrderBy =
        { orderBy with Expression = bindExpr scope orderBy.Expression }

    and private bindWindow (scope: Scope) (window: WindowSpec) : WindowSpec =
        { window with PartitionBy = window.PartitionBy |> List.map (bindExpr scope); OrderBy = window.OrderBy |> List.map (bindOrderBy scope) }

    and private bindTableSource (parentScope: Scope option) visibleCtes (source: TableSource) : TableSource =
        match source with
        | NamedTable(name, alias) when containsName (identifierText name) visibleCtes -> CteTable(name, alias)
        | NamedTable _ | CteTable _ -> source
        | DerivedTable(query, alias) -> DerivedTable(bindQuery parentScope visibleCtes query, alias)

    and private bindCtes inheritedCtes (ctes: Cte list) =
        let mutable visible = inheritedCtes
        let bound = ResizeArray<Cte>()
        for cte in ctes do
            let query = bindQuery None visible cte.Query
            bound.Add { cte with Query = query }
            visible <- visible @ [ cte.Name.Value ]
        bound |> Seq.toList, visible

    and private bindJoin (scope: Scope) (join: Join) : Join * Scope =
        let source = bindTableSource (Some scope) scope.VisibleCtes join.Source
        let extended = { scope with Sources = scope.Sources @ [ sourceBinding source ] }
        ensureDistinctAliases extended
        let boundJoin =
            match join with
            | CrossJoin _ -> CrossJoin source
            | OnJoin(kind, _, predicate) -> OnJoin(kind, source, bindExpr extended predicate)
        boundJoin, extended

    and private bindSelect parentScope inheritedCtes (select: Select) : Select * Scope * string list =
        let ctes, visibleCtes = bindCtes inheritedCtes select.Ctes
        let from = select.From |> Option.map (bindTableSource parentScope visibleCtes)
        let initialSources = from |> Option.map sourceBinding |> Option.toList
        let initialScope : Scope = { Id = scopeId parentScope; Parent = parentScope; Sources = initialSources; VisibleCtes = visibleCtes }
        ensureDistinctAliases initialScope
        let joins, scope =
            (([], initialScope), select.Joins)
            ||> List.fold (fun (boundJoins: Join list, currentScope: Scope) join ->
                let boundJoin, nextScope = bindJoin currentScope join
                boundJoins @ [ boundJoin ], nextScope)
        let projectionItems = select.ProjectionItems |> NonEmpty.map (fun (item: SelectItem) -> { item with Expression = bindExpr scope item.Expression })
        { select with
            Ctes = ctes
            From = from
            Joins = joins
            ProjectionItems = projectionItems
            Where = select.Where |> Option.map (bindExpr scope)
            GroupBy = select.GroupBy |> List.map (bindExpr scope)
            Having = select.Having |> Option.map (bindExpr scope) }, scope, visibleCtes

    and private bindQuery parentScope inheritedCtes (query: Query) : Query =
        let head, headScope, visibleCtes = bindSelect parentScope inheritedCtes query.Head
        let setOperations = query.SetOperations |> List.map (fun (branch: SetBranch) -> { branch with Query = bindQuery parentScope visibleCtes branch.Query })
        { query with Head = head; SetOperations = setOperations; OrderBy = query.OrderBy |> List.map (bindOrderBy headScope) }

    let private extendScopeWithSources (scope: Scope) sources =
        (([], scope), sources)
        ||> List.fold (fun (bound: TableSource list, current: Scope) source ->
            let value = bindTableSource None current.VisibleCtes source
            let next = { current with Sources = current.Sources @ [ sourceBinding value ] }
            ensureDistinctAliases next
            bound @ [ value ], next)

    let private bindAssignment scope (assignment: Assignment) = { assignment with Value = bindExpr scope assignment.Value }
    let private bindReturning scope items = items |> List.map (fun (item: SelectItem) -> { item with Expression = bindExpr scope item.Expression })

    let private bindDocument (document: Document) : Document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(bindQuery None [] query)
            | InsertStatement insert ->
                let emptyScope : Scope = { Id = 0; Parent = None; Sources = []; VisibleCtes = [] }
                let targetScope = { emptyScope with Sources = [ sourceBinding (NamedTable(insert.Target, None)) ] }
                let input =
                    match insert.Input with
                    | Values rows -> Values(rows |> NonEmpty.map (NonEmpty.map (bindExpr emptyScope)))
                    | QuerySource query -> QuerySource(bindQuery None [] query)
                    | DefaultValues -> DefaultValues
                InsertStatement { insert with Input = input; Returning = bindReturning targetScope insert.Returning }
            | UpdateStatement update ->
                let baseScope : Scope = { Id = 0; Parent = None; Sources = [ sourceBinding (NamedTable(update.Target, None)) ]; VisibleCtes = [] }
                let from, scope = extendScopeWithSources baseScope update.From
                UpdateStatement
                    { update with
                        From = from
                        AssignmentItems = update.AssignmentItems |> NonEmpty.map (bindAssignment scope)
                        Where = update.Where |> Option.map (bindExpr scope)
                        Returning = bindReturning scope update.Returning }
            | DeleteStatement delete ->
                let baseScope : Scope = { Id = 0; Parent = None; Sources = [ sourceBinding (NamedTable(delete.Target, None)) ]; VisibleCtes = [] }
                let usingSources, scope = extendScopeWithSources baseScope delete.Using
                DeleteStatement
                    { delete with
                        Using = usingSources
                        Where = delete.Where |> Option.map (bindExpr scope)
                        Returning = bindReturning scope delete.Returning }
        { document with Statement = statement }

    let bind parsed = Transition.bind bindDocument parsed
