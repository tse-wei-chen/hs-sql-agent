namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
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
        | DerivedTable(_, alias)
        | LateralDerivedTable(_, alias) ->
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
        | Spanned(span, inner) -> bindExpr scope inner |> Expr.withSpan span
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
        | DateAdd(unit, amount, value) -> DateAdd(unit, bindExpr scope amount, bindExpr scope value)
        | DateDiff(unit, startValue, finishValue) -> DateDiff(unit, bindExpr scope startValue, bindExpr scope finishValue)
        | Unary(op, operand) -> Unary(op, bindExpr scope operand)
        | Binary(op, left, right) -> Binary(op, bindExpr scope left, bindExpr scope right)
        | Like(value, pattern, escape, negated, caseInsensitive) ->
            Like(bindExpr scope value, bindExpr scope pattern, escape, negated, caseInsensitive)
        | RawRegexCall(arguments, isDistinct) ->
            RawRegexCall(arguments |> List.map (bindExpr scope), isDistinct)
        | RegexMatch(value, pattern) ->
            RegexMatch(bindExpr scope value, bindExpr scope pattern)
        | PostgresJsonAccess(value, selector, resultKind) ->
            PostgresJsonAccess(bindExpr scope value, selector, resultKind)
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
        let sourceSpan = Expr.span orderBy.Expression
        let expression =
            match Expr.unspan orderBy.Expression with
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
            | Spanned _ -> invalidOp "Expr.unspan returned a spanned expression."
            | _ -> bindExpr scope orderBy.Expression
        let expression =
            if Span.isKnown sourceSpan then Expr.withSpan sourceSpan expression
            else expression
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
        | LateralDerivedTable(query, alias) ->
            LateralDerivedTable(bindQuery dialect parentScope visibleCtes query, alias)

    and private countRecursiveReferencesExpr dialect cteKey expression =
        let recurse = countRecursiveReferencesExpr dialect cteKey
        match expression with
        | Spanned(_, inner) -> recurse inner
        | Column _
        | BoundColumn _
        | Wildcard _
        | OrderOrdinal _
        | Literal _
        | Interval _ -> 0
        | Unary(_, operand)
        | Cast(operand, _)
        | Extract(_, operand)
        | PostgresJsonAccess(operand, _, _)
        | IsNull(operand, _) -> recurse operand
        | Binary(_, left, right)
        | RegexMatch(left, right)
        | FilteredAggregate(left, right)
        | DateAdd(_, left, right)
        | DateDiff(_, left, right) -> recurse left + recurse right
        | Like(value, pattern, _, _, _) -> recurse value + recurse pattern
        | RawRegexCall(arguments, _) -> arguments |> List.sumBy recurse
        | FunctionCall call ->
            (call.Arguments |> List.sumBy recurse)
            + (call.AggregateOrderBy |> List.sumBy (fun item -> recurse item.Expression))
        | Windowed(value, window) ->
            recurse value
            + (window.PartitionBy |> List.sumBy recurse)
            + (window.OrderBy |> List.sumBy (fun item -> recurse item.Expression))
        | SimpleCase(input, branches, fallback) ->
            recurse input
            + (branches |> NonEmpty.toList |> List.sumBy (fun branch -> recurse branch.Match + recurse branch.Result))
            + (fallback |> Option.map recurse |> Option.defaultValue 0)
        | SearchedCase(branches, fallback) ->
            (branches |> NonEmpty.toList |> List.sumBy (fun branch -> recurse branch.Condition + recurse branch.Result))
            + (fallback |> Option.map recurse |> Option.defaultValue 0)
        | InList(value, items, _) ->
            recurse value + (items |> NonEmpty.toList |> List.sumBy recurse)
        | InSubquery(value, query, _) ->
            recurse value + countRecursiveReferencesQuery dialect cteKey query
        | Between(value, lower, upper, _) ->
            recurse value + recurse lower + recurse upper
        | ScalarSubquery query
        | Exists(query, _) ->
            countRecursiveReferencesQuery dialect cteKey query

    and private hasRestrictedRecursiveExpr expression =
        let recurse = hasRestrictedRecursiveExpr
        match expression with
        | Spanned(_, inner) -> recurse inner
        | Column _
        | BoundColumn _
        | Wildcard _
        | OrderOrdinal _
        | Literal _
        | Interval _ -> false
        | Unary(_, operand)
        | Cast(operand, _)
        | Extract(_, operand)
        | PostgresJsonAccess(operand, _, _)
        | IsNull(operand, _) -> recurse operand
        | Binary(_, left, right)
        | RegexMatch(left, right)
        | DateAdd(_, left, right)
        | DateDiff(_, left, right) -> recurse left || recurse right
        | FilteredAggregate _ -> true
        | Like(value, pattern, _, _, _) -> recurse value || recurse pattern
        | RawRegexCall(arguments, _) -> arguments |> List.exists recurse
        | FunctionCall call ->
            let name = FunctionName.value call.Name
            let knownRestricted =
                not (FunctionName.hasQuotedParts call.Name)
                && (SqlCanonicalFunctionRegistry.IsAggregate(name)
                    || SqlCanonicalFunctionRegistry.IsWindow(name))
            knownRestricted
            || (call.Arguments |> List.exists recurse)
            || (call.AggregateOrderBy |> List.exists (fun item -> recurse item.Expression))
        | Windowed _ -> true
        | SimpleCase(input, branches, fallback) ->
            recurse input
            || (branches |> NonEmpty.toList |> List.exists (fun branch -> recurse branch.Match || recurse branch.Result))
            || (fallback |> Option.exists recurse)
        | SearchedCase(branches, fallback) ->
            (branches |> NonEmpty.toList |> List.exists (fun branch -> recurse branch.Condition || recurse branch.Result))
            || (fallback |> Option.exists recurse)
        | InList(value, items, _) ->
            recurse value || (items |> NonEmpty.toList |> List.exists recurse)
        | InSubquery(value, query, _) ->
            recurse value || hasRestrictedRecursiveQuery query
        | Between(value, lower, upper, _) ->
            recurse value || recurse lower || recurse upper
        | ScalarSubquery query
        | Exists(query, _) ->
            hasRestrictedRecursiveQuery query

    and private hasRestrictedRecursiveSource source =
        match source with
        | NamedTable _
        | CteTable _ -> false
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) -> hasRestrictedRecursiveQuery query

    and private hasRestrictedRecursiveSelect (select: Select) =
        select.DistinctMode <> SelectDistinct.AllRows
        || not select.GroupBy.IsEmpty
        || select.Having.IsSome
        || (select.Projection |> List.exists (fun item -> hasRestrictedRecursiveExpr item.Expression))
        || (select.Where |> Option.exists hasRestrictedRecursiveExpr)
        || (select.From |> Option.exists hasRestrictedRecursiveSource)
        || (select.Joins
            |> List.exists (fun join ->
                hasRestrictedRecursiveSource join.Source
                || (join.Predicate |> Option.exists hasRestrictedRecursiveExpr)))

    and private hasRestrictedRecursiveQuery (query: Query) =
        hasRestrictedRecursiveSelect query.Head
        || not query.OrderBy.IsEmpty
        || query.Limit.IsSome
        || query.Offset.IsSome
        || query.FetchWithTies
        || (query.SetOperations |> List.exists (fun branch -> hasRestrictedRecursiveQuery branch.Query))

    and private countRecursiveReferencesSource dialect cteKey source =
        match source with
        | NamedTable(name, _)
        | CteTable(name, _) ->
            if StringComparer.Ordinal.Equals(identifierKey dialect name, cteKey) then 1 else 0
        | DerivedTable(query, _)
        | LateralDerivedTable(query, _) ->
            countRecursiveReferencesQuery dialect cteKey query

    and private countDirectRecursiveSources dialect cteKey (select: Select) =
        let countDirect source =
            match source with
            | NamedTable(name, _)
            | CteTable(name, _) when StringComparer.Ordinal.Equals(identifierKey dialect name, cteKey) -> 1
            | _ -> 0
        (select.From |> Option.map countDirect |> Option.defaultValue 0)
        + (select.Joins |> List.sumBy (fun join -> countDirect join.Source))

    and private countRecursiveReferencesSelect dialect cteKey (select: Select) =
        let expr = countRecursiveReferencesExpr dialect cteKey
        (select.From |> Option.map (countRecursiveReferencesSource dialect cteKey) |> Option.defaultValue 0)
        + (select.Joins
           |> List.sumBy (fun join ->
               countRecursiveReferencesSource dialect cteKey join.Source
               + (join.Predicate |> Option.map expr |> Option.defaultValue 0)))
        + (select.Projection |> List.sumBy (fun item -> expr item.Expression))
        + (select.Where |> Option.map expr |> Option.defaultValue 0)
        + (select.GroupBy |> List.sumBy expr)
        + (select.Having |> Option.map expr |> Option.defaultValue 0)

    and private countRecursiveReferencesQuery dialect cteKey (query: Query) =
        countRecursiveReferencesSelect dialect cteKey query.Head
        + (query.SetOperations |> List.sumBy (fun branch -> countRecursiveReferencesQuery dialect cteKey branch.Query))
        + (query.OrderBy |> List.sumBy (fun item -> countRecursiveReferencesExpr dialect cteKey item.Expression))

    and private validateRecursiveCteShape dialect (cte: Cte) =
        if cte.RecursiveScope then
            let cteKey = partKey dialect cte.Name
            let totalReferences = countRecursiveReferencesQuery dialect cteKey cte.Query
            if totalReferences > 0 then
                let anchorReferences = countRecursiveReferencesSelect dialect cteKey cte.Query.Head
                if anchorReferences <> 0 then
                    invalidOp (
                        "SQL capability 'select.recursive_cte' requires a non-recursive anchor term; CTE '"
                        + cte.Name.Value + "' references itself in the anchor.")
                match cte.Query.SetOperations with
                | [ branch ]
                    when (branch.Operator = SetOperator.Union || branch.Operator = SetOperator.UnionAll)
                         && branch.Query.SetOperations.IsEmpty ->
                    let recursiveReferences = countRecursiveReferencesQuery dialect cteKey branch.Query
                    let directSources = countDirectRecursiveSources dialect cteKey branch.Query.Head
                    if recursiveReferences <> 1 || directSources <> 1 then
                        invalidOp (
                            "SQL capability 'select.recursive_cte' requires exactly one direct self-reference in the recursive UNION term for CTE '"
                            + cte.Name.Value + "'.")
                    if dialect = SqlAgentToolType.Firebird && branch.Operator <> SetOperator.UnionAll then
                        invalidOp (
                            "SQL capability 'select.recursive_cte' requires UNION ALL for Firebird recursive members.")
                    if dialect <> SqlAgentToolType.Postgres && hasRestrictedRecursiveQuery branch.Query then
                        invalidOp (
                            "SQL capability 'select.recursive_cte' currently admits only the proven portable recursive-member subset for "
                            + string dialect
                            + ": no DISTINCT, GROUP BY, HAVING, ORDER BY, LIMIT/OFFSET, aggregate, window, or filtered-aggregate constructs.")
                    if dialect <> SqlAgentToolType.Postgres then
                        let selfSource source =
                            match source with
                            | NamedTable(name, _)
                            | CteTable(name, _) ->
                                StringComparer.Ordinal.Equals(identifierKey dialect name, cteKey)
                            | _ -> false
                        let outerJoinTouchesSelf =
                            branch.Query.Head.Joins
                            |> List.exists (function
                                | NaturalJoin((OnJoinKind.Left | OnJoinKind.Right | OnJoinKind.Full), source)
                                | OnJoin((OnJoinKind.Left | OnJoinKind.Right | OnJoinKind.Full), source, _)
                                | UsingJoin((OnJoinKind.Left | OnJoinKind.Right | OnJoinKind.Full), source, _) ->
                                    selfSource source
                                | _ -> false)
                        if outerJoinTouchesSelf then
                            invalidOp (
                                "SQL capability 'select.recursive_cte' does not admit an outer-join recursive self-reference for "
                                + string dialect + ".")
                | _ ->
                    invalidOp (
                        "SQL capability 'select.recursive_cte' requires self-reference to use one anchor UNION or UNION ALL recursive term for CTE '"
                        + cte.Name.Value + "'.")

    and private bindCtes dialect inheritedCtes (ctes: Cte list) =
        let mutable visible = inheritedCtes
        let bound = ResizeArray<Cte>()
        for cte in ctes do
            validateRecursiveCteShape dialect cte
            let cteKey = partKey dialect cte.Name
            let bindingVisible =
                if cte.RecursiveScope then visible @ [ cteKey ]
                else visible
            let query = bindQuery dialect None bindingVisible cte.Query
            bound.Add { cte with Query = query }
            visible <- visible @ [ cteKey ]
        bound |> Seq.toList, visible

    and private bindJoin (scope: Scope) (join: Join) : Join * Scope =
        let correlationScope =
            match join.Source with
            | LateralDerivedTable _ -> Some scope
            | _ -> scope.Parent
        let source = bindTableSource scope.Dialect correlationScope scope.VisibleCtes join.Source
        let extended = { scope with Sources = scope.Sources @ [ sourceBinding scope.Dialect source ] }
        ensureDistinctAliases extended
        let boundJoin =
            match join with
            | CrossJoin _ -> CrossJoin source
            | NaturalJoin(kind, _) -> NaturalJoin(kind, source)
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
                match item.Alias, Expr.unspan item.Expression with
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

    let private bindTargetOnlyReturning dialect scope items =
        try
            bindReturning scope items
        with
        | :? InvalidOperationException as ex ->
            let prefix =
                if dialect = SqlAgentToolType.MsSqlServer then
                    "SQL Server OUTPUT may reference only the modified target row image in the portable subset. "
                else
                    "SQLite RETURNING expressions may reference only the modified target table; auxiliary UPDATE FROM sources are not visible to RETURNING. "
            raise (InvalidOperationException(prefix + ex.Message, ex))

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
                        Returning =
                            if dialect = SqlAgentToolType.Sqlite || dialect = SqlAgentToolType.MsSqlServer then
                                bindTargetOnlyReturning dialect baseScope update.Returning
                            else
                                bindReturning scope update.Returning }
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
                        Returning =
                            if dialect = SqlAgentToolType.MsSqlServer then
                                bindTargetOnlyReturning dialect baseScope delete.Returning
                            else
                                bindReturning scope delete.Returning }
        { document with Statement = statement }

    let private diagnosticDataKey = "HsSqlAgent.SqlCore.Diagnostic"

    let bind sourceDialect parsed =
        let document = Parsed.value parsed
        try
            Transition.bind (bindDocument sourceDialect) parsed
        with
        | :? SqlCompilationException as ex when not (isNull ex.Diagnostic) ->
            reraise()
        | :? InvalidOperationException as ex ->
            let span =
                if document.Span.Start < 0 || document.Span.Length < 0 then null
                else SqlDiagnosticSpan(document.Span.Start, document.Span.Length)
            let diagnostic =
                SqlDiagnostic(
                    "SQL_BINDING_ERROR",
                    SqlDiagnosticStage.Binding,
                    SqlDiagnosticCategory.Binding,
                    ex.Message,
                    span)
            ex.Data[diagnosticDataKey] <- diagnostic
            reraise()
