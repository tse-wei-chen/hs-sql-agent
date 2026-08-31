namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteBinder =

    type private SourceBinding =
        { QualifierKeys: string list
          Alias: string option
          AliasKey: string option }

    type private Scope =
        { Id: int
          Parent: Scope option
          Sources: SourceBinding list
          VisibleCtes: string list
          Dialect: SqlAgentToolType }

    let private identifierParts = Identifier.parts
    let private identifierText = Identifier.text

    let private canonicalPart dialect (part: IdentifierPart) =
        if part.WasQuoted then
            match dialect with
            | SqlAgentToolType.MySQL
            | SqlAgentToolType.MsSqlServer
            | SqlAgentToolType.Sqlite -> part.Value.ToUpperInvariant()
            | _ -> part.Value
        else
            match dialect with
            | SqlAgentToolType.Postgres -> part.Value.ToLowerInvariant()
            | SqlAgentToolType.Oracle
            | SqlAgentToolType.Firebird -> part.Value.ToUpperInvariant()
            | SqlAgentToolType.MySQL
            | SqlAgentToolType.MsSqlServer
            | SqlAgentToolType.Sqlite -> part.Value.ToUpperInvariant()
            | _ -> part.Value

    let private partsKey dialect parts =
        parts
        |> List.map (fun part ->
            let value = canonicalPart dialect part
            string value.Length + ":" + value + ";")
        |> String.concat String.Empty

    let private identifierKey dialect identifier =
        identifier |> identifierParts |> partsKey dialect

    let private partKey dialect part = partsKey dialect [ part ]
    let private equivalentPart dialect left right = StringComparer.Ordinal.Equals(partKey dialect left, partKey dialect right)
    let private containsQualifier qualifierKey (source: SourceBinding) = source.QualifierKeys |> List.contains qualifierKey
    let private containsName nameKey values = values |> List.contains nameKey

    let private localQualifierExists qualifier (scope: Scope) =
        scope.Sources |> List.exists (containsQualifier qualifier)

    let rec private ancestorQualifierExists qualifier (scope: Scope) =
        match scope.Parent with
        | Some parent when localQualifierExists qualifier parent -> true
        | Some parent -> ancestorQualifierExists qualifier parent
        | None -> false

    let rec private ancestorHasSources (scope: Scope) =
        match scope.Parent with
        | Some parent when not parent.Sources.IsEmpty -> true
        | Some parent -> ancestorHasSources parent
        | None -> false

    let private scopeId parentScope = parentScope |> Option.map (fun (scope: Scope) -> scope.Id + 1) |> Option.defaultValue 0

    let private sourceBinding dialect (source: TableSource) : SourceBinding =
        match source with
        | NamedTable(name, alias) | CteTable(name, alias) ->
            let parts = identifierParts name
            let tailKey = parts |> List.last |> partKey dialect
            let fullKey = identifierKey dialect name
            let aliasKey = alias |> Option.map (partKey dialect)
            let qualifierKeys =
                match aliasKey with
                | Some key -> [ key ]
                | None when StringComparer.Ordinal.Equals(tailKey, fullKey) -> [ tailKey ]
                | None -> [ tailKey; fullKey ]
            { QualifierKeys = qualifierKeys
              Alias = alias |> Option.map (fun value -> value.Value)
              AliasKey = aliasKey }
        | DerivedTable(_, alias) ->
            { QualifierKeys = [ partKey dialect alias ]
              Alias = Some alias.Value
              AliasKey = Some(partKey dialect alias) }

    let private ensureDistinctAliases (scope: Scope) =
        let aliases =
            scope.Sources
            |> List.choose (fun source ->
                source.AliasKey |> Option.map (fun key -> key, source.Alias |> Option.defaultValue key))
        aliases
        |> List.iteri (fun index (aliasKey, aliasDisplay) ->
            aliases
            |> List.skip (index + 1)
            |> List.tryFind (fun (candidateKey, _) -> StringComparer.Ordinal.Equals(candidateKey, aliasKey))
            |> Option.iter (fun _ ->
                invalidOp ("Duplicate table alias '" + aliasDisplay + "' in SQL scope " + string scope.Id + ".")))

    let private ensureQualifier scope qualifierKey qualifierDisplay context =
        if not (localQualifierExists qualifierKey scope || ancestorQualifierExists qualifierKey scope) then
            invalidOp (context + " references unknown table/alias qualifier '" + qualifierDisplay + "'.")

    let private bindColumn (scope: Scope) (identifier: Identifier) : Expr =
        match identifierParts identifier with
        | [] -> invalidOp "Column identifier cannot be empty."
        | [ _ ] when not scope.Sources.IsEmpty ->
            BoundColumn(identifier, ColumnBinding.LocalRowSource)
        | [ _ ] when ancestorHasSources scope ->
            BoundColumn(identifier, ColumnBinding.OuterRowSource)
        | [ _ ] ->
            Column identifier
        | parts ->
            let qualifierParts = parts |> List.take (parts.Length - 1)
            let qualifier = qualifierParts |> List.map (fun part -> part.Value) |> String.concat "."
            let qualifierKey = partsKey scope.Dialect qualifierParts
            let context = "Column '" + identifierText identifier + "'"
            if localQualifierExists qualifierKey scope then
                BoundColumn(identifier, ColumnBinding.LocalRowSource)
            elif ancestorQualifierExists qualifierKey scope then
                BoundColumn(identifier, ColumnBinding.OuterRowSource)
            else
                invalidOp (context + " references unknown table/alias qualifier '" + qualifier + "'.")

    let rec private bindExpr (scope: Scope) (expression: Expr) : Expr =
        match expression with
        | Column identifier -> bindColumn scope identifier
        | BoundColumn _ -> expression
        | Wildcard(Some identifier) ->
            let qualifier = identifierText identifier
            ensureQualifier
                scope
                (identifierKey scope.Dialect identifier)
                qualifier
                ("Wildcard '" + qualifier + ".*'")
            expression
        | Wildcard None | OrderOrdinal _ | Literal _ | Interval _ -> expression
        | Unary(op, operand) -> Unary(op, bindExpr scope operand)
        | Binary(op, left, right) -> Binary(op, bindExpr scope left, bindExpr scope right)
        | Like(value, pattern, escape, negated, caseInsensitive) ->
            Like(bindExpr scope value, bindExpr scope pattern, escape, negated, caseInsensitive)
        | RawRegexCall(arguments, isDistinct) ->
            RawRegexCall(arguments |> List.map (bindExpr scope), isDistinct)
        | RegexMatch(value, pattern) ->
            RegexMatch(bindExpr scope value, bindExpr scope pattern)
        | FunctionCall call ->
            FunctionCall
                { call with
                    Arguments = call.Arguments |> List.map (bindExpr scope)
                    AggregateOrderBy = call.AggregateOrderBy |> List.map (bindOrderBy scope []) }
        | FilteredAggregate(value, predicate) -> FilteredAggregate(bindExpr scope value, bindExpr scope predicate)
        | Windowed(value, window) -> Windowed(bindExpr scope value, bindWindow scope window)
        | Cast(value, targetType) -> Cast(bindExpr scope value, targetType)
        | Extract(field, value) -> Extract(field, bindExpr scope value)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(bindExpr scope input, branches |> NonEmpty.map (fun (branch: SimpleCaseBranch) -> { Match = bindExpr scope branch.Match; Result = bindExpr scope branch.Result }), fallback |> Option.map (bindExpr scope))
        | SearchedCase(branches, fallback) ->
            SearchedCase(branches |> NonEmpty.map (fun (branch: SearchedCaseBranch) -> { Condition = bindExpr scope branch.Condition; Result = bindExpr scope branch.Result }), fallback |> Option.map (bindExpr scope))
        | InList(value, items, negated) -> InList(bindExpr scope value, items |> NonEmpty.map (bindExpr scope), negated)
        | InSubquery(value, query, negated) -> InSubquery(bindExpr scope value, bindQuery scope.Dialect (Some scope) scope.VisibleCtes query, negated)
        | Between(value, lower, upper, negated) -> Between(bindExpr scope value, bindExpr scope lower, bindExpr scope upper, negated)
        | IsNull(value, negated) -> IsNull(bindExpr scope value, negated)
        | ScalarSubquery query -> ScalarSubquery(bindQuery scope.Dialect (Some scope) scope.VisibleCtes query)
        | Exists(query, negated) -> Exists(bindQuery scope.Dialect (Some scope) scope.VisibleCtes query, negated)

    and private bindOrderBy (scope: Scope) projectionAliases (orderBy: OrderBy) : OrderBy =
        let expression =
            match orderBy.Expression with
            | Column identifier when identifierParts identifier |> List.length = 1 ->
                let reference = identifierParts identifier |> List.head
                let name = reference.Value
                let matches =
                    projectionAliases
                    |> List.filter (fun (candidate: IdentifierPart) -> equivalentPart scope.Dialect candidate reference)
                match matches with
                | [ candidate ] when candidate.PreserveSpelling ->
                    BoundColumn(Identifier.create [ candidate ], ColumnBinding.ProjectionAlias)
                | [ _ ] -> BoundColumn(identifier, ColumnBinding.ProjectionAlias)
                | _ :: _ :: _ ->
                    if scope.Sources.IsEmpty then
                        invalidOp ("ORDER BY projection alias '" + name + "' is ambiguous in a no-FROM query.")
                    else
                        invalidOp ("ORDER BY alias '" + name + "' is ambiguous.")
                | [] -> bindExpr scope orderBy.Expression
            | _ -> bindExpr scope orderBy.Expression
        { orderBy with Expression = expression }

    and private bindWindow (scope: Scope) (window: WindowSpec) : WindowSpec =
        { window with
            PartitionBy = window.PartitionBy |> List.map (bindExpr scope)
            OrderBy = window.OrderBy |> List.map (bindOrderBy scope []) }

    and private bindTableSource dialect (parentScope: Scope option) visibleCtes (source: TableSource) : TableSource =
        match source with
        | NamedTable(name, alias) when containsName (identifierKey dialect name) visibleCtes -> CteTable(name, alias)
        | NamedTable _ | CteTable _ -> source
        | DerivedTable(query, alias) -> DerivedTable(bindQuery dialect parentScope visibleCtes query, alias)

    and private bindCtes dialect inheritedCtes (ctes: Cte list) =
        let mutable visible = inheritedCtes
        let bound = ResizeArray<Cte>()
        for cte in ctes do
            let query = bindQuery dialect None visible cte.Query
            bound.Add { cte with Query = query }
            visible <- visible @ [ partKey dialect cte.Name ]
        bound |> Seq.toList, visible

    and private bindJoin (scope: Scope) (join: Join) : Join * Scope =
        let source = bindTableSource scope.Dialect (Some scope) scope.VisibleCtes join.Source
        let extended = { scope with Sources = scope.Sources @ [ sourceBinding scope.Dialect source ] }
        ensureDistinctAliases extended
        let boundJoin =
            match join with
            | CrossJoin _ -> CrossJoin source
            | OnJoin(kind, _, predicate) -> OnJoin(kind, source, bindExpr extended predicate)
            | UsingJoin(kind, _, columns) -> UsingJoin(kind, source, columns)
        boundJoin, extended

    and private bindSelect dialect parentScope inheritedCtes (select: Select) : Select * Scope * string list =
        let ctes, visibleCtes = bindCtes dialect inheritedCtes select.Ctes
        let from = select.From |> Option.map (bindTableSource dialect parentScope visibleCtes)
        let initialSources = from |> Option.map (sourceBinding dialect) |> Option.toList
        let initialScope : Scope =
            { Id = scopeId parentScope
              Parent = parentScope
              Sources = initialSources
              VisibleCtes = visibleCtes
              Dialect = dialect }
        ensureDistinctAliases initialScope
        let joins, scope =
            (([], initialScope), select.Joins)
            ||> List.fold (fun (boundJoins: Join list, currentScope: Scope) join ->
                let boundJoin, nextScope = bindJoin currentScope join
                boundJoins @ [ boundJoin ], nextScope)
        let projectionItems = select.ProjectionItems |> NonEmpty.map (fun (item: SelectItem) -> { item with Expression = bindExpr scope item.Expression })
        let distinctMode =
            match select.DistinctMode with
            | SelectDistinct.DistinctOn expressions ->
                expressions
                |> NonEmpty.map (bindExpr scope)
                |> SelectDistinct.DistinctOn
            | mode -> mode
        { select with
            Ctes = ctes
            From = from
            Joins = joins
            DistinctMode = distinctMode
            ProjectionItems = projectionItems
            Where = select.Where |> Option.map (bindExpr scope)
            GroupBy = select.GroupBy |> List.map (bindExpr scope)
            Having = select.Having |> Option.map (bindExpr scope) }, scope, visibleCtes

    and private bindQuery dialect parentScope inheritedCtes (query: Query) : Query =
        let head, headScope, visibleCtes = bindSelect dialect parentScope inheritedCtes query.Head
        let setOperations =
            query.SetOperations
            |> List.map (fun (branch: SetBranch) ->
                { branch with Query = bindQuery dialect parentScope visibleCtes branch.Query })
        let explicitAliases =
            head.Projection
            |> List.choose (fun item -> item.Alias)
        let setOutputNames =
            head.Projection
            |> List.choose (fun item ->
                match item.Alias, item.Expression with
                | Some alias, _ -> Some alias
                | None, Column identifier
                | None, BoundColumn(identifier, _) ->
                    Identifier.parts identifier |> List.tryLast
                | _ -> None)
        let orderAliases = if setOperations.IsEmpty then explicitAliases else setOutputNames
        { query with
            Head = head
            SetOperations = setOperations
            OrderBy = query.OrderBy |> List.map (bindOrderBy headScope orderAliases) }

    let private extendScopeWithSources (scope: Scope) sources =
        (([], scope), sources)
        ||> List.fold (fun (bound: TableSource list, current: Scope) source ->
            let value = bindTableSource current.Dialect None current.VisibleCtes source
            let next = { current with Sources = current.Sources @ [ sourceBinding current.Dialect value ] }
            ensureDistinctAliases next
            bound @ [ value ], next)

    let private bindAssignment scope (assignment: Assignment) = { assignment with Value = bindExpr scope assignment.Value }

    let private bindReturning scope (items: ReturningItem list) =
        items
        |> List.map (function
            | ReturningColumn(identifier, alias) ->
                match bindExpr scope (Column identifier) with
                | BoundColumn(boundIdentifier, _)
                | Column boundIdentifier -> ReturningColumn(boundIdentifier, alias)
                | _ -> invalidOp "RETURNING column binding produced a non-column expression."
            | ReturningWildcard alias ->
                ReturningWildcard alias
            | ReturningExpression(expression, alias) ->
                ReturningExpression(bindExpr scope expression, alias))

    let private bindDocument dialect (document: Document) : Document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(bindQuery dialect None [] query)
            | InsertStatement insert ->
                let emptyScope : Scope =
                    { Id = 0; Parent = None; Sources = []; VisibleCtes = []; Dialect = dialect }
                let targetScope =
                    { emptyScope with Sources = [ sourceBinding dialect (NamedTable(insert.Target, None)) ] }
                let input =
                    match insert.Input with
                    | Values rows -> Values(rows |> NonEmpty.map (NonEmpty.map (bindExpr emptyScope)))
                    | QuerySource query -> QuerySource(bindQuery dialect None [] query)
                    | DefaultValues -> DefaultValues
                InsertStatement { insert with Input = input; Returning = bindReturning targetScope insert.Returning }
            | UpdateStatement update ->
                let baseScope : Scope =
                    { Id = 0
                      Parent = None
                      Sources = [ sourceBinding dialect (NamedTable(update.Target, update.TargetAlias)) ]
                      VisibleCtes = []
                      Dialect = dialect }
                let from, scope = extendScopeWithSources baseScope update.From
                UpdateStatement
                    { update with
                        From = from
                        AssignmentItems = update.AssignmentItems |> NonEmpty.map (bindAssignment scope)
                        Where = update.Where |> Option.map (bindExpr scope)
                        Returning = bindReturning scope update.Returning }
            | DeleteStatement delete ->
                let baseScope : Scope =
                    { Id = 0
                      Parent = None
                      Sources = [ sourceBinding dialect (NamedTable(delete.Target, delete.TargetAlias)) ]
                      VisibleCtes = []
                      Dialect = dialect }
                let usingSources, scope = extendScopeWithSources baseScope delete.Using
                DeleteStatement
                    { delete with
                        Using = usingSources
                        Where = delete.Where |> Option.map (bindExpr scope)
                        Returning = bindReturning scope delete.Returning }
        { document with Statement = statement }

    let bind sourceDialect parsed = Transition.bind (bindDocument sourceDialect) parsed
