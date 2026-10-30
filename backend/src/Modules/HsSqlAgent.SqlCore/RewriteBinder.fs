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
    let private containsName nameKey values =
        values |> List.exists (fun value ->
            RewriteResourceBudget.checkpoint ()
            value = nameKey)
    let private containsQualifier qualifierKey (source: SourceBinding) =
        RewriteResourceBudget.checkpoint ()
        containsName qualifierKey source.QualifierKeys

    let private localQualifierExists qualifier (scope: Scope) =
        scope.Sources |> List.exists (containsQualifier qualifier)

    // Parent scopes can be as deep as a nested subquery chain. Qualifier
    // lookup must never spend one CLR call frame per ancestor.
    let private ancestorQualifierExists qualifier (scope: Scope) =
        let mutable current = scope.Parent
        let mutable found = false
        while current.IsSome && not found do
            RewriteResourceBudget.checkpoint ()
            match current with
            | Some parent ->
                found <- localQualifierExists qualifier parent
                current <- parent.Parent
            | None -> ()
        found

    let private ancestorHasSources (scope: Scope) =
        let mutable current = scope.Parent
        let mutable found = false
        while current.IsSome && not found do
            RewriteResourceBudget.checkpoint ()
            match current with
            | Some parent ->
                found <- not parent.Sources.IsEmpty
                current <- parent.Parent
            | None -> ()
        found

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

    // Recursive CTE proofs traverse expressions AND nested queries. The
    // former mutually-recursive walkers consumed one CLR frame per AST level,
    // including nested scalar queries inside recursive UNION members.
    // Explicit work frames retain the original proof boundary: inspect the
    // SELECT body and set branches but NOT separately scoped CTE definitions
    // or DISTINCT ON expressions when counting self-references.
    [<Struct>]
    type private CteProofFrame =
        | CteProofExpr of expression: Expr
        | CteProofQuery of queryBody: Query
        | CteProofSelect of selectBody: Select
        | CteProofSource of tableSource: TableSource

    let private walkCteProof visit (root: CteProofFrame) =
        let pending = ResizeArray<CteProofFrame>()
        let inline push frame = pending.Add frame
        let pushExprs (expressions: Expr list) =
            let entries = List.toArray expressions
            for index = entries.Length - 1 downto 0 do
                push (CteProofExpr entries.[index])
        let pushOrders (orders: OrderBy list) =
            let entries = List.toArray orders
            for index = entries.Length - 1 downto 0 do
                push (CteProofExpr entries.[index].Expression)
        push root
        let mutable stopped = false
        while pending.Count > 0 && not stopped do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            if visit frame then stopped <- true
            else
                match frame with
                | CteProofQuery query ->
                    pushOrders query.OrderBy
                    let branches = List.toArray query.SetOperations
                    for index = branches.Length - 1 downto 0 do
                        push (CteProofQuery branches.[index].Query)
                    push (CteProofSelect query.Head)
                | CteProofSelect select ->
                    select.Having |> Option.iter (fun value -> push (CteProofExpr value))
                    pushExprs select.GroupBy
                    select.Where |> Option.iter (fun value -> push (CteProofExpr value))
                    NonEmpty.iterReverse (fun item ->
                        push (CteProofExpr item.Expression)) select.ProjectionItems
                    let joins = List.toArray select.Joins
                    for index = joins.Length - 1 downto 0 do
                        match joins.[index].Predicate with
                        | Some predicate -> push (CteProofExpr predicate)
                        | None -> ()
                        push (CteProofSource joins.[index].Source)
                    select.From |> Option.iter (fun source -> push (CteProofSource source))
                | CteProofSource source ->
                    match source with
                    | NamedTable _ | CteTable _ -> ()
                    | DerivedTable(query, _) | LateralDerivedTable(query, _) ->
                        push (CteProofQuery query)
                | CteProofExpr expression ->
                    match expression with
                    | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                    | Literal _ | Interval _ -> ()
                    | Spanned(_, inner) -> push (CteProofExpr inner)
                    | Unary(_, operand) | Cast(operand, _)
                    | Extract(_, operand) | PostgresJsonAccess(operand, _, _)
                    | IsNull(operand, _) -> push (CteProofExpr operand)
                    | Binary(_, left, right) | RegexMatch(left, right)
                    | FilteredAggregate(left, right)
                    | DateAdd(_, left, right) | DateDiff(_, left, right)
                    | Like(left, right, _, _, _) ->
                        push (CteProofExpr right)
                        push (CteProofExpr left)
                    | RawRegexCall(arguments, _) -> pushExprs arguments
                    | FunctionCall call ->
                        pushOrders call.AggregateOrderBy
                        pushExprs call.Arguments
                    | Windowed(value, window) ->
                        pushOrders window.OrderBy
                        pushExprs window.PartitionBy
                        push (CteProofExpr value)
                    | SimpleCase(input, branches, fallback) ->
                        fallback |> Option.iter (fun value -> push (CteProofExpr value))
                        NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                            push (CteProofExpr branch.Result)
                            push (CteProofExpr branch.Match)) branches
                        push (CteProofExpr input)
                    | SearchedCase(branches, fallback) ->
                        fallback |> Option.iter (fun value -> push (CteProofExpr value))
                        NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                            push (CteProofExpr branch.Result)
                            push (CteProofExpr branch.Condition)) branches
                    | InList(value, items, _) ->
                        NonEmpty.iterReverse (fun item -> push (CteProofExpr item)) items
                        push (CteProofExpr value)
                    | InSubquery(value, query, _) ->
                        push (CteProofQuery query)
                        push (CteProofExpr value)
                    | Between(value, lower, upper, _) ->
                        push (CteProofExpr upper)
                        push (CteProofExpr lower)
                        push (CteProofExpr value)
                    | ScalarSubquery query | Exists(query, _) ->
                        push (CteProofQuery query)
        stopped

    let private countCteSelfReferences dialect cteKey frame =
        let mutable count = 0
        walkCteProof (function
            | CteProofSource(NamedTable(name, _) | CteTable(name, _))
                when StringComparer.Ordinal.Equals(identifierKey dialect name, cteKey) ->
                count <- count + 1
                false
            | _ -> false) frame |> ignore
        count

    let private containsRestrictedRecursiveMember query =
        walkCteProof (function
            | CteProofQuery q ->
                not q.OrderBy.IsEmpty || q.Limit.IsSome || q.Offset.IsSome || q.FetchWithTies
            | CteProofSelect select ->
                select.DistinctMode <> SelectDistinct.AllRows
                || not select.GroupBy.IsEmpty || select.Having.IsSome
            | CteProofExpr(FilteredAggregate _ | Windowed _) -> true
            | CteProofExpr(FunctionCall call) ->
                let name = FunctionName.value call.Name
                not (FunctionName.hasQuotedParts call.Name)
                && (SqlCanonicalFunctionRegistry.IsAggregate(name)
                    || SqlCanonicalFunctionRegistry.IsWindow(name))
            | _ -> false) (CteProofQuery query)

    [<Struct>]
    type private BindPrefixFrame =
        | BindSpan of span: Span
        | BindUnary of operation: UnaryOperator

    // The query frame carries its lexical CTE visibility and the caller's
    // correlation scope. These cannot be inferred from an already-bound
    // branch without changing alias resolution.
    [<Struct>]
    type private BindQueryFrame =
        | BindQueryEnter of query: Query * parentScope: Scope option * inheritedCtes: string list
        | BindQueryComplete of original: Query * head: Select * headScope: Scope * visibleCtes: string list

    // The mixed Binder graph owns its own scope state. Query callbacks
    // cannot be reconstructed with a scope-free Expr/Query mapper because
    // LATERAL, recursive CTEs, and projection aliases depend on when
    // the parent row sources become visible.
    type private BindGraphQueryContext =
        { Original: Query
          Parent: Scope option
          Inherited: string list
          SetBranches: SetBranch array
          TailOrders: OrderBy array
          BoundBranches: ResizeArray<Query>
          BoundOrders: ResizeArray<OrderBy>
          mutable BoundHead: Select option
          mutable HeadScope: Scope option
          mutable HeadCtes: string list
          mutable OrderAliases: IdentifierPart list }

    type private BindGraphSelectContext =
        { Original: Select
          Parent: Scope option
          CteItems: Cte array
          JoinItems: Join array
          BoundCtes: ResizeArray<Cte>
          BoundJoins: ResizeArray<Join>
          AliasDisplays: System.Collections.Generic.Dictionary<string, string>
          mutable VisibleCtes: string list
          mutable BoundFrom: TableSource option
          mutable Scope: Scope option
          mutable Slots: Expr array
          mutable BoundSlots: Expr array }

    type private BindGraphFrame =
        | GraphQueryEnter of query: BindGraphQueryContext
        | GraphQueryHeadDone of query: BindGraphQueryContext
        | GraphQueryBranchNext of query: BindGraphQueryContext * index: int
        | GraphQueryBranchDone of query: BindGraphQueryContext * index: int
        | GraphQueryOrderNext of query: BindGraphQueryContext * index: int
        | GraphQueryOrderDone of query: BindGraphQueryContext * index: int
        | GraphQueryFinish of query: BindGraphQueryContext
        | GraphSelectCteNext of selection: BindGraphSelectContext * index: int
        | GraphSelectCteDone of selection: BindGraphSelectContext * index: int
        | GraphSelectFrom of selection: BindGraphSelectContext
        | GraphSelectFromDone of selection: BindGraphSelectContext
        | GraphSelectJoinNext of selection: BindGraphSelectContext * index: int
        | GraphSelectJoinSourceDone of selection: BindGraphSelectContext * index: int
        | GraphSelectJoinPredicateDone of selection: BindGraphSelectContext * index: int * source: TableSource * extendedScope: Scope
        | GraphSelectSlotNext of selection: BindGraphSelectContext * index: int
        | GraphSelectSlotDone of selection: BindGraphSelectContext * index: int
        | GraphSelectFinish of selection: BindGraphSelectContext
        | GraphSourceEnter of source: TableSource * parent: Scope option * visibleCtes: string list
        | GraphSourceDone of source: TableSource
        | GraphExprEnter of expression: Expr * scope: Scope
        | GraphExprScan of expression: Expr * scope: Scope
        | GraphExprDone of expression: Expr * scope: Scope * queryCount: int

    let private queryRequiresMixedBinding (query: Query) =
        // Query-free SQL continues through the original fast binder. The
        // mixed machine is needed only when a SELECT crosses a Query boundary.
        let containsQuery expr =
            RewriteAstTraversal.exists true
                (function
                 | ScalarSubquery _ | Exists _ | InSubquery _ -> true
                 | _ -> false) expr
        let isDerived = function
            | DerivedTable _ | LateralDerivedTable _ -> true
            | NamedTable _ | CteTable _ -> false
        let select = query.Head
        not select.Ctes.IsEmpty
        || not query.SetOperations.IsEmpty
        || (select.From |> Option.map isDerived |> Option.defaultValue false)
        || (select.Joins |> List.exists (fun join ->
            isDerived join.Source
            || (join.Predicate |> Option.map containsQuery |> Option.defaultValue false)))
        || (select.Projection |> List.exists (fun item -> containsQuery item.Expression))
        || (match select.DistinctMode with
            | SelectDistinct.DistinctOn items -> NonEmpty.toList items |> List.exists containsQuery
            | _ -> false)
        || (select.Where |> Option.map containsQuery |> Option.defaultValue false)
        || (select.GroupBy |> List.exists containsQuery)
        || (select.Having |> Option.map containsQuery |> Option.defaultValue false)
        || (query.OrderBy |> List.exists (fun order -> containsQuery order.Expression))

    let private bindScalarNode (scope: Scope) (value: Expr) : Expr =
        match value with
        | Column identifier -> bindColumn scope identifier
        | Wildcard(Some identifier) ->
            let qualifier = identifierText identifier
            ensureQualifier
                scope
                (identifierKey scope.Dialect identifier)
                qualifier
                ("Wildcard '" + qualifier + ".*'")
            value
        | _ -> value

    let rec private bindExpr (scope: Scope) (expression: Expr) : Expr =
        // One explicit enter/exit frame per scalar Expr, regardless of
        // branching, CASE, window arguments or binary-tree depth. Query
        // callbacks keep their existing nested correlation/CTE scopes.
        RewriteAstTraversal.mapPostOrder
            (fun _ -> RewriteStackSafety.compile SqlDiagnosticStage.Binding)
            (bindScalarNode scope)
            (bindQuery scope.Dialect (Some scope) scope.VisibleCtes)
            expression

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

    and private countRecursiveReferencesSelect dialect cteKey (select: Select) =
        countCteSelfReferences dialect cteKey (CteProofSelect select)

    and private countRecursiveReferencesQuery dialect cteKey (query: Query) =
        countCteSelfReferences dialect cteKey (CteProofQuery query)

    and private countDirectRecursiveSources dialect cteKey (select: Select) =
        let countDirect source =
            match source with
            | NamedTable(name, _) | CteTable(name, _)
                when StringComparer.Ordinal.Equals(identifierKey dialect name, cteKey) -> 1
            | _ -> 0
        (select.From |> Option.map countDirect |> Option.defaultValue 0)
        + (select.Joins |> List.sumBy (fun join -> countDirect join.Source))

    and private hasRestrictedRecursiveQuery (query: Query) =
        containsRestrictedRecursiveMember query

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

    and private bindJoin
        (scope: Scope)
        (aliasDisplays: System.Collections.Generic.Dictionary<string, string>)
        (join: Join) : Join * Scope =
        let correlationScope =
            match join.Source with
            | LateralDerivedTable _ -> Some scope
            | _ -> scope.Parent
        let source = bindTableSource scope.Dialect correlationScope scope.VisibleCtes join.Source
        let binding = sourceBinding scope.Dialect source
        match binding.AliasKey with
        | Some key ->
            match aliasDisplays.TryGetValue key with
            | true, firstDisplay ->
                // Match the former first-alias diagnostic, including casing.
                invalidOp ("Duplicate table alias '" + firstDisplay + "' in SQL scope " + string scope.Id + ".")
            | false, _ ->
                aliasDisplays.Add(key, binding.Alias |> Option.defaultValue key)
        | None -> ()
        // Sources are only queried for existence/qualified lookups. Prepend
        // to the persistent list rather than copying every earlier join.
        // The accumulating dictionary enforces original source-order alias
        // uniqueness without rescanning the entire prefix at every join.
        let extended = { scope with Sources = binding :: scope.Sources }
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
        let aliases = System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        for binding in initialSources do
            match binding.AliasKey with
            | Some key -> aliases.Add(key, binding.Alias |> Option.defaultValue key)
            | None -> ()
        // Every join receives a scope containing all preceding row sources,
        // including lateral correlation, without rebuilding that prefix.
        let reverseJoins, scope =
            (([], initialScope), select.Joins)
            ||> List.fold (fun (boundJoins: Join list, currentScope: Scope) join ->
                let boundJoin, nextScope = bindJoin currentScope aliases join
                boundJoin :: boundJoins, nextScope)
        let joins = List.rev reverseJoins
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
        // Flatten only inert no-FROM scalar SELECT wrappers. Build each
        // intervening lexical scope explicitly, including the full parent
        // chain needed for correlated column references.
        if RewriteAstTraversal.trySimpleScalarChain query |> Option.isSome then
            let frames = ResizeArray<struct (Query * SelectItem)>()
            let mutable current = query
            let mutable parent = parentScope
            let mutable peeling = true
            while peeling do
                match RewriteAstTraversal.trySimpleScalarChain current with
                | Some(item, child) ->
                    RewriteStackSafety.compile SqlDiagnosticStage.Binding
                    frames.Add(struct (current, item))
                    let nestedScope : Scope =
                        { Id = scopeId parent
                          Parent = parent
                          Sources = []
                          VisibleCtes = inheritedCtes
                          Dialect = dialect }
                    parent <- Some nestedScope
                    current <- child
                | None -> peeling <- false
            let mutable result = bindQuery dialect parent inheritedCtes current
            for index = frames.Count - 1 downto 0 do
                let struct (original, item) = frames.[index]
                result <- RewriteAstTraversal.rebuildSimpleScalarChain original item result
            result
        elif queryRequiresMixedBinding query then
            bindMixedQuery dialect parentScope inheritedCtes query
        // Most scalar and DML subqueries have no set branches. Avoid two
        // heap-backed work stacks for the overwhelmingly common case,
        // while using the same lexical scope and alias-binding rules.
        elif query.SetOperations.IsEmpty then
            RewriteStackSafety.compile SqlDiagnosticStage.Binding
            let head, headScope, _ =
                bindSelect dialect parentScope inheritedCtes query.Head
            let explicitAliases =
                head.Projection |> List.choose (fun item -> item.Alias)
            { query with
                Head = head
                OrderBy =
                    query.OrderBy |> List.map (bindOrderBy headScope explicitAliases) }
        else
            // Set-operation branches form their own Query tree. Bind each head
            // before its branches, then rebuild branches and ORDER BY on exit:
            // equivalent to the recursive order, but without a CLR frame per
            // nested set Query.
            let pending = ResizeArray<BindQueryFrame>()
            let results = ResizeArray<Query>()
            pending.Add(BindQueryEnter(query, parentScope, inheritedCtes))
            while pending.Count > 0 do
                let last = pending.Count - 1
                let frame = pending.[last]
                pending.RemoveAt(last)
                match frame with
                | BindQueryEnter(current, parent, visible) ->
                    RewriteStackSafety.compile SqlDiagnosticStage.Binding
                    let head, headScope, headCtes =
                        bindSelect dialect parent visible current.Head
                    pending.Add(BindQueryComplete(current, head, headScope, headCtes))
                    // A UNION/INTERSECT/EXCEPT branch inherits the original
                    // parent's correlation scope and the head's visible CTEs,
                    // never the head's local row-source scope.
                    let branches = List.toArray current.SetOperations
                    for index = branches.Length - 1 downto 0 do
                        pending.Add(BindQueryEnter(branches.[index].Query, parent, headCtes))
                | BindQueryComplete(current, head, headScope, _) ->
                    let branches = List.toArray current.SetOperations
                    let rebuilt = Array.zeroCreate<SetBranch> branches.Length
                    for index = branches.Length - 1 downto 0 do
                        let resultIndex = results.Count - 1
                        if resultIndex < 0 then
                            invalidOp "Binder query continuation results underflow."
                        let child = results.[resultIndex]
                        results.RemoveAt(resultIndex)
                        rebuilt.[index] <- { branches.[index] with Query = child }
                    let explicitAliases =
                        head.Projection |> List.choose (fun item -> item.Alias)
                    let setOutputNames =
                        head.Projection
                        |> List.choose (fun item ->
                            match item.Alias, Expr.unspan item.Expression with
                            | Some alias, _ -> Some alias
                            | None, Column identifier
                            | None, BoundColumn(identifier, _) ->
                                Identifier.parts identifier |> List.tryLast
                            | _ -> None)
                    let orderAliases =
                        if branches.Length = 0 then explicitAliases else setOutputNames
                    results.Add
                        { current with
                            Head = head
                            SetOperations = List.ofArray rebuilt
                            OrderBy = current.OrderBy |> List.map (bindOrderBy headScope orderAliases) }
            if results.Count <> 1 then
                invalidOp "Binder query continuation frames did not reconstruct one query root."
            results.[0]

    and private bindMixedQuery dialect parentScope inheritedCtes (root: Query) : Query =
        // All mixed Query/Select/Source/Expr work is scheduled on ONE
        // explicit stack. No onQuery callback ever enters bindQuery while
        // the machine is executing. Parent Scope and VisibleCtes travel
        // with each Query and Expr frame, not in ambient mutable state.
        let pending = ResizeArray<BindGraphFrame>()
        let queryResults = ResizeArray<Query>()
        let sourceResults = ResizeArray<TableSource>()
        let expressionResults = ResizeArray<Expr>()
        let selectResults = ResizeArray<struct (Select * Scope * string list)>()
        let inline push frame = pending.Add frame
        let pop (values: ResizeArray<'T>) =
            let last = values.Count - 1
            if last < 0 then invalidOp "Binder mixed-graph result stack underflow."
            let value = values.[last]
            values.RemoveAt last
            value
        let popQuery () = pop queryResults
        let popSource () = pop sourceResults
        let popExpr () = pop expressionResults
        let popQueries count =
            let mutable mapped = []
            for _ in 1 .. count do
                mapped <- popQuery () :: mapped
            mapped
        let makeQuery (query: Query) (parent: Scope option) (visible: string list) =
            { Original = query
              Parent = parent
              Inherited = visible
              SetBranches = List.toArray query.SetOperations
              TailOrders = List.toArray query.OrderBy
              BoundBranches = ResizeArray<Query>()
              BoundOrders = ResizeArray<OrderBy>()
              BoundHead = None
              HeadScope = None
              HeadCtes = visible
              OrderAliases = [] }
        let makeSelect (select: Select) (parent: Scope option) visible =
            { Original = select
              Parent = parent
              CteItems = List.toArray select.Ctes
              JoinItems = List.toArray select.Joins
              BoundCtes = ResizeArray<Cte>()
              BoundJoins = ResizeArray<Join>()
              AliasDisplays =
                System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
              VisibleCtes = visible
              BoundFrom = None
              Scope = None
              Slots = [||]
              BoundSlots = [||] }
        let scopeOf (select: BindGraphSelectContext) =
            match select.Scope with
            | Some scope -> scope
            | None -> invalidOp "Binder mixed graph reached SELECT without row-source scope."
        let headScopeOf (query: BindGraphQueryContext) =
            match query.HeadScope with
            | Some scope -> scope
            | None -> invalidOp "Binder mixed graph reached ORDER BY without head scope."
        let pushScan scope value = push (GraphExprScan(value, scope))
        let scanReverse scope (values: Expr list) =
            let values = List.toArray values
            for index = values.Length - 1 downto 0 do
                pushScan scope values.[index]
        let scanOrders scope (orders: OrderBy list) =
            let orders = List.toArray orders
            for index = orders.Length - 1 downto 0 do
                pushScan scope orders.[index].Expression
        push (GraphQueryEnter(makeQuery root parentScope inheritedCtes))
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last
            match frame with
            | GraphQueryEnter query ->
                RewriteStackSafety.compile SqlDiagnosticStage.Binding
                push (GraphQueryHeadDone query)
                push (GraphSelectCteNext(
                    makeSelect query.Original.Head query.Parent query.Inherited, 0))
            | GraphQueryHeadDone query ->
                let struct (head, scope, visible) = pop selectResults
                query.BoundHead <- Some head
                query.HeadScope <- Some scope
                query.HeadCtes <- visible
                let explicitAliases =
                    head.Projection |> List.choose (fun item -> item.Alias)
                let outputAliases =
                    head.Projection
                    |> List.choose (fun item ->
                        match item.Alias, Expr.unspan item.Expression with
                        | Some alias, _ -> Some alias
                        | None, Column identifier
                        | None, BoundColumn(identifier, _) ->
                            Identifier.parts identifier |> List.tryLast
                        | _ -> None)
                query.OrderAliases <-
                    if query.SetBranches.Length = 0 then explicitAliases
                    else outputAliases
                push (GraphQueryBranchNext(query, 0))
            | GraphQueryBranchNext(query, index) ->
                if index < query.SetBranches.Length then
                    push (GraphQueryBranchDone(query, index))
                    push (GraphQueryEnter(
                        makeQuery query.SetBranches.[index].Query
                            query.Parent query.HeadCtes))
                else
                    push (GraphQueryOrderNext(query, 0))
            | GraphQueryBranchDone(query, index) ->
                query.BoundBranches.Add(popQuery ())
                push (GraphQueryBranchNext(query, index + 1))
            | GraphQueryOrderNext(query, index) ->
                if index < query.TailOrders.Length then
                    let original = query.TailOrders.[index]
                    let scope = headScopeOf query
                    if RewriteAstTraversal.exists true
                           (function
                            | ScalarSubquery _ | Exists _ | InSubquery _ -> true
                            | _ -> false) original.Expression then
                        push (GraphQueryOrderDone(query, index))
                        push (GraphExprEnter(original.Expression, scope))
                    else
                        query.BoundOrders.Add(
                            bindOrderBy scope query.OrderAliases original)
                        push (GraphQueryOrderNext(query, index + 1))
                else
                    push (GraphQueryFinish query)
            | GraphQueryOrderDone(query, index) ->
                let original = query.TailOrders.[index]
                let scope = headScopeOf query
                let sourceSpan = Expr.span original.Expression
                let bound = popExpr ()
                let wrapped =
                    if Span.isKnown sourceSpan then Expr.withSpan sourceSpan bound
                    else bound
                query.BoundOrders.Add({ original with Expression = wrapped })
                push (GraphQueryOrderNext(query, index + 1))
            | GraphQueryFinish query ->
                let head =
                    match query.BoundHead with
                    | Some value -> value
                    | None -> invalidOp "Binder mixed graph Query head was not rebuilt."
                let branchQueries = query.BoundBranches.ToArray()
                if branchQueries.Length <> query.SetBranches.Length then
                    invalidOp "Binder mixed graph Query branch results are unbalanced."
                let branches =
                    Array.mapi (fun index (branch: SetBranch) ->
                        { branch with Query = branchQueries.[index] }) query.SetBranches
                    |> Array.toList
                queryResults.Add
                    { query.Original with
                        Head = head
                        SetOperations = branches
                        OrderBy = query.BoundOrders |> Seq.toList }
            | GraphSelectCteNext(select, index) ->
                if index < select.CteItems.Length then
                    let cte = select.CteItems.[index]
                    validateRecursiveCteShape dialect cte
                    let cteKey = partKey dialect cte.Name
                    let visible =
                        if cte.RecursiveScope then select.VisibleCtes @ [ cteKey ]
                        else select.VisibleCtes
                    push (GraphSelectCteDone(select, index))
                    // CTEs bind in isolation; they must not inherit the
                    // SELECT's Parent Scope.
                    push (GraphQueryEnter(makeQuery cte.Query None visible))
                else
                    push (GraphSelectFrom select)
            | GraphSelectCteDone(select, index) ->
                let cte = select.CteItems.[index]
                select.BoundCtes.Add({ cte with Query = popQuery () })
                select.VisibleCtes <- select.VisibleCtes @ [ partKey dialect cte.Name ]
                push (GraphSelectCteNext(select, index + 1))
            | GraphSelectFrom select ->
                push (GraphSelectFromDone select)
                select.Original.From
                |> Option.iter (fun source ->
                    push (GraphSourceEnter(source, select.Parent, select.VisibleCtes)))
            | GraphSelectFromDone select ->
                select.BoundFrom <-
                    select.Original.From
                    |> Option.map (fun _ -> popSource ())
                let sourceBindings =
                    select.BoundFrom |> Option.map (sourceBinding dialect) |> Option.toList
                let scope =
                    { Id = scopeId select.Parent
                      Parent = select.Parent
                      Sources = sourceBindings
                      VisibleCtes = select.VisibleCtes
                      Dialect = dialect }
                ensureDistinctAliases scope
                for binding in sourceBindings do
                    match binding.AliasKey with
                    | Some key ->
                        select.AliasDisplays.Add(
                            key, binding.Alias |> Option.defaultValue key)
                    | None -> ()
                select.Scope <- Some scope
                push (GraphSelectJoinNext(select, 0))
            | GraphSelectJoinNext(select, index) ->
                if index < select.JoinItems.Length then
                    let join = select.JoinItems.[index]
                    let current = scopeOf select
                    let correlationScope =
                        match join.Source with
                        | LateralDerivedTable _ -> Some current
                        | _ -> current.Parent
                    push (GraphSelectJoinSourceDone(select, index))
                    push (GraphSourceEnter(
                        join.Source, correlationScope, select.VisibleCtes))
                else
                    let original = select.Original
                    let projection = original.Projection |> List.map (fun item -> item.Expression)
                    let distinct =
                        match original.DistinctMode with
                        | SelectDistinct.DistinctOn values -> NonEmpty.toList values
                        | _ -> []
                    let where =
                        match original.Where with
                        | Some expr -> [ expr ]
                        | None -> []
                    let having =
                        match original.Having with
                        | Some expr -> [ expr ]
                        | None -> []
                    select.Slots <-
                        List.toArray (projection @ distinct @ where @ original.GroupBy @ having)
                    select.BoundSlots <- Array.zeroCreate<Expr> select.Slots.Length
                    push (GraphSelectSlotNext(select, 0))
            | GraphSelectJoinSourceDone(select, index) ->
                let original = select.JoinItems.[index]
                let source = popSource ()
                let binding = sourceBinding dialect source
                let scope = scopeOf select
                match binding.AliasKey with
                | Some key ->
                    match select.AliasDisplays.TryGetValue key with
                    | true, firstDisplay ->
                        invalidOp (
                            "Duplicate table alias '" + firstDisplay
                            + "' in SQL scope " + string scope.Id + ".")
                    | false, _ ->
                        select.AliasDisplays.Add(
                            key, binding.Alias |> Option.defaultValue key)
                | None -> ()
                let extended = { scope with Sources = binding :: scope.Sources }
                match original with
                | OnJoin(_, _, predicate) ->
                    push (GraphSelectJoinPredicateDone(select, index, source, extended))
                    push (GraphExprEnter(predicate, extended))
                | CrossJoin _ ->
                    select.BoundJoins.Add(CrossJoin source)
                    select.Scope <- Some extended
                    push (GraphSelectJoinNext(select, index + 1))
                | NaturalJoin(kind, _) ->
                    select.BoundJoins.Add(NaturalJoin(kind, source))
                    select.Scope <- Some extended
                    push (GraphSelectJoinNext(select, index + 1))
                | UsingJoin(kind, _, columns) ->
                    select.BoundJoins.Add(UsingJoin(kind, source, columns))
                    select.Scope <- Some extended
                    push (GraphSelectJoinNext(select, index + 1))
            | GraphSelectJoinPredicateDone(select, index, source, extended) ->
                let predicate = popExpr ()
                match select.JoinItems.[index] with
                | OnJoin(kind, _, _) ->
                    select.BoundJoins.Add(OnJoin(kind, source, predicate))
                | _ -> invalidOp "Binder ON JOIN continuation lost its predicate."
                select.Scope <- Some extended
                push (GraphSelectJoinNext(select, index + 1))
            | GraphSelectSlotNext(select, index) ->
                if index < select.Slots.Length then
                    push (GraphSelectSlotDone(select, index))
                    push (GraphExprEnter(select.Slots.[index], scopeOf select))
                else
                    push (GraphSelectFinish select)
            | GraphSelectSlotDone(select, index) ->
                select.BoundSlots.[index] <- popExpr ()
                push (GraphSelectSlotNext(select, index + 1))
            | GraphSelectFinish select ->
                let original = select.Original
                let mutable current = 0
                let take () =
                    let value = select.BoundSlots.[current]
                    current <- current + 1
                    value
                let projections =
                    original.ProjectionItems
                    |> NonEmpty.map (fun item ->
                        { item with Expression = take () })
                let distinct =
                    match original.DistinctMode with
                    | SelectDistinct.DistinctOn values ->
                        values
                        |> NonEmpty.map (fun _ -> take ())
                        |> SelectDistinct.DistinctOn
                    | mode -> mode
                let where = original.Where |> Option.map (fun _ -> take ())
                let groupBy = original.GroupBy |> List.map (fun _ -> take ())
                let having = original.Having |> Option.map (fun _ -> take ())
                if current <> select.BoundSlots.Length then
                    invalidOp "Binder SELECT mixed-graph expression slots are unbalanced."
                selectResults.Add
                    (struct (
                        { original with
                            Ctes = select.BoundCtes |> Seq.toList
                            From = select.BoundFrom
                            Joins = select.BoundJoins |> Seq.toList
                            ProjectionItems = projections
                            DistinctMode = distinct
                            Where = where
                            GroupBy = groupBy
                            Having = having },
                        scopeOf select,
                        select.VisibleCtes))
            | GraphSourceEnter(source, parent, visible) ->
                match source with
                | NamedTable(name, alias)
                    when containsName (identifierKey dialect name) visible ->
                    sourceResults.Add(CteTable(name, alias))
                | NamedTable _ | CteTable _ -> sourceResults.Add source
                | DerivedTable(query, _) | LateralDerivedTable(query, _) ->
                    push (GraphSourceDone source)
                    push (GraphQueryEnter(makeQuery query parent visible))
            | GraphSourceDone source ->
                let query = popQuery ()
                match source with
                | DerivedTable(_, alias) ->
                    sourceResults.Add(DerivedTable(query, alias))
                | LateralDerivedTable(_, alias) ->
                    sourceResults.Add(LateralDerivedTable(query, alias))
                | _ -> invalidOp "Binder source continuation has no derived Query."
            | GraphExprEnter(expression, scope) ->
                let mutable queryCount = 0
                RewriteAstTraversal.iter (function
                    | ScalarSubquery _ | Exists _ | InSubquery _ ->
                        queryCount <- queryCount + 1
                    | _ -> ()) expression
                if queryCount = 0 then
                    expressionResults.Add(bindExpr scope expression)
                else
                    push (GraphExprDone(expression, scope, queryCount))
                    pushScan scope expression
            | GraphExprScan(expression, scope) ->
                RewriteStackSafety.compile SqlDiagnosticStage.Binding
                // Check each scalar leaf before entering its following
                // subquery. The exit mapper reconstructs these bindings
                // without changing their first-diagnostic precedence.
                match expression with
                | Column _ | Wildcard(Some _) ->
                    bindScalarNode scope expression |> ignore
                | _ -> ()
                match expression with
                | Spanned(_, inner) | Unary(_, inner) | Cast(inner, _)
                | Extract(_, inner) | PostgresJsonAccess(inner, _, _)
                | IsNull(inner, _) -> pushScan scope inner
                | DateAdd(_, left, right) | DateDiff(_, left, right)
                | Binary(_, left, right) | Like(left, right, _, _, _)
                | RegexMatch(left, right) | FilteredAggregate(left, right) ->
                    pushScan scope right
                    pushScan scope left
                | RawRegexCall(arguments, _) -> scanReverse scope arguments
                | FunctionCall call ->
                    scanOrders scope call.AggregateOrderBy
                    scanReverse scope call.Arguments
                | Windowed(value, window) ->
                    scanOrders scope window.OrderBy
                    scanReverse scope window.PartitionBy
                    pushScan scope value
                | SimpleCase(input, branches, fallback) ->
                    fallback |> Option.iter (pushScan scope)
                    NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                        pushScan scope branch.Result
                        pushScan scope branch.Match) branches
                    pushScan scope input
                | SearchedCase(branches, fallback) ->
                    fallback |> Option.iter (pushScan scope)
                    NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                        pushScan scope branch.Result
                        pushScan scope branch.Condition) branches
                | InList(value, items, _) ->
                    NonEmpty.iterReverse (pushScan scope) items
                    pushScan scope value
                | InSubquery(value, query, _) ->
                    push (GraphQueryEnter(
                        makeQuery query (Some scope) scope.VisibleCtes))
                    pushScan scope value
                | Between(value, lower, upper, _) ->
                    pushScan scope upper
                    pushScan scope lower
                    pushScan scope value
                | ScalarSubquery query | Exists(query, _) ->
                    push (GraphQueryEnter(
                        makeQuery query (Some scope) scope.VisibleCtes))
                | Column _ | BoundColumn _ | Wildcard _ | OrderOrdinal _
                | Literal _ | Interval _ -> ()
            | GraphExprDone(expression, scope, queryCount) ->
                let queries =
                    System.Collections.Generic.Queue<Query>(
                        popQueries queryCount)
                let rebuilt =
                    RewriteAstTraversal.mapPostOrder
                        (fun _ -> RewriteStackSafety.compile SqlDiagnosticStage.Binding)
                        (bindScalarNode scope)
                        (fun _ ->
                            if queries.Count = 0 then
                                invalidOp "Binder mixed Expression Query results underflow."
                            queries.Dequeue())
                        expression
                if queries.Count <> 0 then
                    invalidOp "Binder mixed Expression left unused Query results."
                expressionResults.Add rebuilt
        if queryResults.Count <> 1
           || sourceResults.Count <> 0
           || expressionResults.Count <> 0
           || selectResults.Count <> 0 then
            invalidOp "Binder mixed AST continuation frames are unbalanced."
        queryResults.[0]

    let private extendScopeWithSources (scope: Scope) sources =
        // DML FROM/USING entries are bound without the accumulated scope.
        // Preserve their left-to-right binding and first duplicate-alias
        // diagnostic, but avoid repeatedly appending immutable lists.
        let seenAliases = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        for existing in scope.Sources do
            RewriteResourceBudget.checkpoint ()
            match existing.AliasKey with
            | Some key -> seenAliases.Add key |> ignore
            | None -> ()
        let mutable reversedSources = []
        let mutable reversedBindings = []
        for source in sources do
            RewriteResourceBudget.checkpoint ()
            let value = bindTableSource scope.Dialect None scope.VisibleCtes source
            let binding = sourceBinding scope.Dialect value
            match binding.AliasKey with
            | Some key when not (seenAliases.Add key) ->
                invalidOp (
                    "Duplicate table alias '"
                    + (binding.Alias |> Option.defaultValue key)
                    + "' in SQL scope " + string scope.Id + ".")
            | _ -> ()
            reversedSources <- value :: reversedSources
            reversedBindings <- binding :: reversedBindings
        let bound = List.rev reversedSources
        let next =
            { scope with Sources = scope.Sources @ List.rev reversedBindings }
        bound, next

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
                let ctes, visibleCtes = bindCtes dialect [] update.Ctes
                let baseScope : Scope =
                    { Id = 0
                      Parent = None
                      Sources = [ sourceBinding dialect (NamedTable(update.Target, update.TargetAlias)) ]
                      VisibleCtes = visibleCtes
                      Dialect = dialect }
                let from, scope = extendScopeWithSources baseScope update.From
                UpdateStatement
                    { update with
                        Ctes = ctes
                        From = from
                        AssignmentItems = update.AssignmentItems |> NonEmpty.map (bindAssignment scope)
                        Where = update.Where |> Option.map (bindExpr scope)
                        Returning =
                            if dialect = SqlAgentToolType.Sqlite || dialect = SqlAgentToolType.MsSqlServer then
                                bindTargetOnlyReturning dialect baseScope update.Returning
                            else
                                bindReturning scope update.Returning }
            | MergeStatement merge ->
                let emptyScope : Scope =
                    { Id = 0; Parent = None; Sources = []; VisibleCtes = []; Dialect = dialect }
                let targetSource = NamedTable(merge.Target, Some merge.TargetAlias)
                let sourceIdentifier = Identifier.create [ merge.Source.Alias ]
                let syntheticSource = CteTable(sourceIdentifier, Some merge.Source.Alias)
                let fullScope =
                    { emptyScope with
                        Sources =
                            [ sourceBinding dialect targetSource
                              sourceBinding dialect syntheticSource ] }
                ensureDistinctAliases fullScope
                let sourceOnlyScope =
                    { emptyScope with Sources = [ sourceBinding dialect syntheticSource ] }
                let sourceValues = merge.Source.SourceValues |> NonEmpty.map (bindExpr emptyScope)
                let matched =
                    merge.Matched
                    |> Option.map (function
                        | MergeDelete -> MergeDelete
                        | MergeUpdate assignments ->
                            assignments
                            |> NonEmpty.map (bindAssignment fullScope)
                            |> MergeUpdate)
                let notMatched =
                    merge.NotMatched
                    |> Option.map (fun mergeInsert ->
                        { mergeInsert with
                            InsertValues =
                                mergeInsert.InsertValues |> NonEmpty.map (bindExpr sourceOnlyScope) })
                MergeStatement
                    { merge with
                        Source = { merge.Source with SourceValues = sourceValues }
                        MatchPredicate = bindExpr fullScope merge.MatchPredicate
                        Matched = matched
                        NotMatched = notMatched }
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

    // Parse-time lexical budgets protect source length and token count.
    // This extra check executes at the Binder boundary so direct ParsedSql
    // entry points and inspection cannot bypass the same resource policy.
    // Count closed AST nodes iteratively across all statement families.
    [<Literal>]
    let private MaxAstNodes = 65536

    let private validateAstResourceBudget (parsed: ParsedSql) =
        let document = Parsed.value parsed
        let mutable visited = 0
        let addNode () =
            visited <- visited + 1
            if visited > MaxAstNodes then
                let detail =
                    "SQL AST exceeds the compiler's maximum "
                    + string MaxAstNodes
                    + " structural nodes; reduce query complexity."
                let location =
                    if document.Span.Start >= 0 && document.Span.Length >= 0 then
                        SqlDiagnosticSpan(document.Span.Start, document.Span.Length)
                    else
                        Unchecked.defaultof<SqlDiagnosticSpan>
                raise (
                    SqlCompilationException(
                        detail,
                        SqlDiagnostic(
                            "SQL_COMPILER_AST_NODE_LIMIT",
                            SqlDiagnosticStage.Binding,
                            SqlDiagnosticCategory.Resource,
                            detail,
                            location)))
        let traverse frame =
            RewriteAstTraversal.iterIncludingQueries
                (function
                | RewriteAstTraversal.ExprNode _
                | RewriteAstTraversal.QueryNode _
                | RewriteAstTraversal.SelectNode _
                | RewriteAstTraversal.SourceNode _ -> addNode ()
                | RewriteAstTraversal.AfterCtes _
                | RewriteAstTraversal.AfterQueryHead _ -> ())
                frame
        let expr value = traverse (RewriteAstTraversal.ExprNode value)
        let query value = traverse (RewriteAstTraversal.QueryNode value)
        let source value = traverse (RewriteAstTraversal.SourceNode value)
        let returning (items: ReturningItem list) =
            for item in items do expr item.Expression
        let assignments (values: Assignment list) =
            for value in values do expr value.Value
        let conflict (value: ConflictValue) =
            let pending = ResizeArray<ConflictValue>()
            pending.Add value
            while pending.Count > 0 do
                let last = pending.Count - 1
                let item = pending.[last]
                pending.RemoveAt last
                addNode ()
                match item with
                | ConflictNegate inner -> pending.Add inner
                | ConflictBinary(_, left, right) ->
                    pending.Add right
                    pending.Add left
                | ConflictProposedColumn _
                | ConflictTargetColumn _
                | ConflictLiteral _ -> ()
        addNode () // Statement itself.
        match document.Statement with
        | QueryStatement value -> query value
        | InsertStatement value ->
            match value.Input with
            | Values rows ->
                NonEmpty.iter (fun row -> NonEmpty.iter expr row) rows
            | QuerySource selected -> query selected
            | DefaultValues -> ()
            match value.Conflict with
            | Some conflictClause ->
                match conflictClause.Action with
                | DoNothing -> ()
                | UpdateProposedValues entries ->
                    NonEmpty.iter (fun (entry: ConflictAssignment) -> conflict entry.Value) entries
            | None -> ()
            returning value.Returning
        | UpdateStatement value ->
            for cte in value.Ctes do query cte.Query
            assignments value.Assignments
            for item in value.From do source item
            value.Where |> Option.iter expr
            returning value.Returning
        | DeleteStatement value ->
            for item in value.Using do source item
            value.Where |> Option.iter expr
            returning value.Returning
        | MergeStatement value ->
            NonEmpty.iter expr value.Source.SourceValues
            expr value.MatchPredicate
            match value.Matched with
            | Some(MergeUpdate fields) ->
                NonEmpty.iter (fun (field: Assignment) -> expr field.Value) fields
            | Some MergeDelete | None -> ()
            value.NotMatched
            |> Option.iter (fun action ->
                NonEmpty.iter expr action.InsertValues)

    let bind sourceDialect parsed =
        let document = Parsed.value parsed
        try
            validateAstResourceBudget parsed
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
