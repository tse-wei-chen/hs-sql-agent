namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Text
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# implementation of the query binder.
///
/// Scope and resolution state are explicit immutable values. Traversal is
/// exhaustive at the C# AST boundary, and every recursive operation returns
/// the updated binding state so subquery/CTE facts cannot be hidden in ambient
/// mutable fields.
module internal FunctionalQueryBinder =

    type private ResolvedSource =
        {
            Symbol: TableSymbol
            IsOuterReference: bool
        }

    type private BindingScope =
        {
            Id: int
            Parent: BindingScope option
            Sources: TableSymbol list
            Qualifiers: (string * TableSymbol list) list
            AliasKeys: string list
        }

    type private BindingState =
        {
            SourceDialect: SqlAgentToolType
            IdentifierComparer: StringComparer
            PhysicalTables: string list
            AliasFactsRev: QueryAliasFact list
            NextScopeId: int
            ContainsSubquery: bool
            ContainsCte: bool
        }

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    let private requireExpr context (value: SqlExpr | null) : SqlExpr =
        match value with
        | null ->
            raise (InvalidOperationException(
                $"{context} cannot be null at the F# binder boundary."))
        | expression ->
            expression

    let private identifierName (identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private aliasValue (alias: IdentifierPart | null) =
        match Option.ofObj alias with
        | None -> None
        | Some value when String.IsNullOrWhiteSpace(value.Value) -> None
        | Some value -> Some(value.Value.Trim())

    let private identifierKeyParts
        (state: BindingState)
        (parts: seq<IdentifierPart>) =

        let builder = StringBuilder()
        for part in parts do
            let value =
                SqlIdentifierDialectRules.CanonicalPart(
                    part,
                    state.SourceDialect)

            builder
                .Append(value.Length)
                .Append(':')
                .Append(value)
                .Append(';')
            |> ignore

        builder.ToString()

    let private identifierKey
        (state: BindingState)
        (identifier: SqlIdentifier) =
        identifierKeyParts state identifier.Parts

    let private keyEquals
        (state: BindingState)
        left
        right =
        state.IdentifierComparer.Equals(left, right)

    let private containsKey
        (state: BindingState)
        key
        keys =
        keys |> List.exists (fun candidate -> keyEquals state candidate key)

    let private addPhysicalTable tableName state =
        let exists =
            state.PhysicalTables
            |> List.exists (fun existing ->
                StringComparer.OrdinalIgnoreCase.Equals(existing, tableName))

        if exists then
            state
        else
            { state with PhysicalTables = tableName :: state.PhysicalTables }

    let private addAliasFact alias target scopeId state =
        {
            state with
                AliasFactsRev =
                    QueryAliasFact(alias, target, scopeId)
                    :: state.AliasFactsRev
        }

    let private allocateScope parent state =
        let scope =
            {
                Id = state.NextScopeId
                Parent = parent
                Sources = []
                Qualifiers = []
                AliasKeys = []
            }

        scope,
        { state with NextScopeId = state.NextScopeId + 1 }

    let private tryFindQualifier state key scope =
        scope.Qualifiers
        |> List.tryPick (fun (candidate, symbols) ->
            if keyEquals state candidate key then Some symbols else None)

    let private addQualifier state key symbol scope =
        let rec loop remaining acc =
            match remaining with
            | [] ->
                List.rev ((key, [ symbol ]) :: acc)

            | (candidate, symbols) :: tail
                when keyEquals state candidate key ->

                let updatedSymbols =
                    if symbols |> List.contains symbol then
                        symbols
                    else
                        symbol :: symbols

                List.rev acc
                @ ((candidate, updatedSymbols) :: tail)

            | head :: tail ->
                loop tail (head :: acc)

        { scope with Qualifiers = loop scope.Qualifiers [] }

    let private registerAlias
        (state: BindingState)
        (alias: IdentifierPart option)
        (symbol: TableSymbol)
        (scope: BindingScope) =

        match alias with
        | None ->
            scope

        | Some aliasPart ->
            let key = identifierKeyParts state [ aliasPart ]
            if containsKey state key scope.AliasKeys then
                raise (InvalidOperationException(
                    $"Duplicate table alias '{symbol.Alias}' in SQL scope {scope.Id}."))

            { scope with AliasKeys = key :: scope.AliasKeys }

    let private addNamedSource
        state
        (symbol: TableSymbol)
        (name: SqlIdentifier)
        (alias: IdentifierPart option)
        scope =

        let registered = registerAlias state alias symbol scope

        let withSource =
            { registered with Sources = symbol :: registered.Sources }

        let withFullName =
            addQualifier state (identifierKey state name) symbol withSource

        let withTailName =
            if name.Parts.IsDefaultOrEmpty then
                withFullName
            else
                addQualifier
                    state
                    (identifierKeyParts state [ name.Parts[name.Parts.Length - 1] ])
                    symbol
                    withFullName

        match alias with
        | None ->
            withTailName
        | Some aliasPart ->
            addQualifier
                state
                (identifierKeyParts state [ aliasPart ])
                symbol
                withTailName

    let private addDerivedSource
        state
        (symbol: TableSymbol)
        (alias: IdentifierPart)
        scope =

        let registered = registerAlias state (Some alias) symbol scope
        let withSource =
            { registered with Sources = symbol :: registered.Sources }

        addQualifier
            state
            (identifierKeyParts state [ alias ])
            symbol
            withSource

    let rec private resolveQualifier
        state
        (qualifierParts: IdentifierPart array)
        isOuterReference
        scope =

        let key = identifierKeyParts state qualifierParts

        match tryFindQualifier state key scope with
        | Some [ symbol ] ->
            Some
                {
                    Symbol = symbol
                    IsOuterReference = isOuterReference
                }

        | Some matches ->
            let qualifier =
                qualifierParts
                |> Seq.map (fun part -> part.Value)
                |> String.concat "."

            raise (InvalidOperationException(
                $"Ambiguous table/alias qualifier '{qualifier}' in SQL scope {scope.Id}."))

        | None ->
            match scope.Parent with
            | Some parent ->
                resolveQualifier state qualifierParts true parent
            | None ->
                None

    let rec private tryResolveSingleVisibleSource
        isOuterReference
        scope =

        match scope.Sources with
        | [ symbol ] ->
            Some
                {
                    Symbol = symbol
                    IsOuterReference = isOuterReference
                }

        | _ :: _ ->
            // Multiple local sources deliberately suppress outer fallback.
            None

        | [] ->
            match scope.Parent with
            | Some parent ->
                tryResolveSingleVisibleSource true parent
            | None ->
                None

    let private createFacts state =
        let tableBuilder =
            ImmutableHashSet.CreateBuilder<string>(
                StringComparer.OrdinalIgnoreCase)

        state.PhysicalTables
        |> List.iter (fun table -> tableBuilder.Add(table) |> ignore)

        QueryFacts(
            tableBuilder.ToImmutable(),
            state.AliasFactsRev
            |> List.rev
            |> toImmutableArray,
            state.ContainsSubquery,
            state.ContainsCte)

    let rec private bindStatement
        (statement: SqlStatement)
        (parentScope: BindingScope option)
        visibleCtes
        state =

        match statement with
        | :? SelectStatement as select ->
            bindSelect select parentScope visibleCtes state
            |> fun (bound, nextState) -> bound :> SqlStatement, nextState

        | :? QueryStatement as query ->
            bindQueryStatement query parentScope visibleCtes state
            |> fun (bound, nextState) -> bound :> SqlStatement, nextState

        | other ->
            raise (InvalidOperationException(
                $"Unsupported SQL statement while binding: {other.GetType().Name}"))

    and private bindQueryStatement
        (query: QueryStatement)
        parentScope
        inheritedCtes
        state =

        let head, stateAfterHead =
            bindSelect query.Head parentScope inheritedCtes state

        let visibleCtes =
            query.Head.Ctes
            |> Seq.fold
                (fun keys cte ->
                    let key = identifierKey stateAfterHead cte.Name
                    if containsKey stateAfterHead key keys then keys
                    else key :: keys)
                inheritedCtes

        let operations, stateAfterOperations =
            (([], stateAfterHead), query.SetOperations)
            ||> Seq.fold (fun (acc, currentState) operation ->
                let boundQuery, nextState =
                    bindStatement
                        operation.Query
                        parentScope
                        visibleCtes
                        currentState

                CoreBindingAstClone.SetOperation(operation, boundQuery)
                :: acc,
                nextState)

        let orderBy, finalState =
            bindOrderByItems
                query.OrderBy
                None
                visibleCtes
                stateAfterOperations

        CoreBindingAstClone.Query(
            query,
            head,
            operations |> List.rev |> toImmutableArray,
            orderBy),
        finalState

    and private bindSelect
        (select: SelectStatement)
        parentScope
        inheritedCtes
        state =

        let boundCtesRev, localCtes, stateAfterCtes =
            (([], inheritedCtes, state), select.Ctes)
            ||> Seq.fold (fun (boundRev, visible, currentState) cte ->
                let boundQuery, nextState =
                    bindStatement cte.Query None visible currentState

                let key = identifierKey nextState cte.Name
                let nextVisible =
                    if containsKey nextState key visible then visible
                    else key :: visible

                CoreBindingAstClone.Cte(cte, boundQuery) :: boundRev,
                nextVisible,
                { nextState with ContainsCte = true })

        let initialScope, stateWithScope =
            allocateScope parentScope stateAfterCtes

        let boundFrom, scopeAfterFrom, stateAfterFrom =
            match Option.ofObj select.From with
            | None ->
                None, initialScope, stateWithScope

            | Some source ->
                let boundSource, nextScope, nextState =
                    bindSource
                        source
                        initialScope
                        localCtes
                        stateWithScope

                Some boundSource, nextScope, nextState

        let boundJoinsRev, finalScope, stateAfterJoins =
            (([], scopeAfterFrom, stateAfterFrom), select.Joins)
            ||> Seq.fold (fun (boundRev, currentScope, currentState) join ->
                let boundSource, scopeWithSource, stateWithSource =
                    bindSource
                        join.Source
                        currentScope
                        localCtes
                        currentState

                let predicate, nextState =
                    match Option.ofObj join.Predicate with
                    | None ->
                        None, stateWithSource
                    | Some value ->
                        let bound, afterPredicate =
                            bindExpr
                                value
                                (Some scopeWithSource)
                                localCtes
                                stateWithSource

                        Some bound, afterPredicate

                CoreBindingAstClone.Join(
                    join,
                    boundSource,
                    Option.toObj predicate)
                :: boundRev,
                scopeWithSource,
                nextState)

        let boundSelect, stateAfterSelect =
            bindSelectItems
                select.Select
                (Some finalScope)
                localCtes
                stateAfterJoins

        let boundWhere, stateAfterWhere =
            match Option.ofObj select.Where with
            | None ->
                None, stateAfterSelect
            | Some value ->
                let bound, nextState =
                    bindExpr
                        value
                        (Some finalScope)
                        localCtes
                        stateAfterSelect

                Some bound, nextState

        let boundGroupBy, stateAfterGroupBy =
            bindExprItems
                select.GroupBy
                (Some finalScope)
                localCtes
                stateAfterWhere

        let boundHaving, stateAfterHaving =
            match Option.ofObj select.Having with
            | None ->
                None, stateAfterGroupBy
            | Some value ->
                let bound, nextState =
                    bindExpr
                        value
                        (Some finalScope)
                        localCtes
                        stateAfterGroupBy

                Some bound, nextState

        let boundOrderBy, finalState =
            bindOrderByItems
                select.OrderBy
                (Some finalScope)
                localCtes
                stateAfterHaving

        CoreBindingAstClone.Select(
            select,
            boundCtesRev |> List.rev |> toImmutableArray,
            Option.toObj boundFrom,
            boundJoinsRev |> List.rev |> toImmutableArray,
            boundSelect,
            Option.toObj boundWhere,
            boundGroupBy,
            Option.toObj boundHaving,
            boundOrderBy),
        finalState

    and private bindSource
        (source: TableSource)
        scope
        visibleCtes
        state =

        match source with
        | :? NamedTableSource as named ->
            let tableName = identifierName named.Name
            let tableKey = identifierKey state named.Name
            let isCte = containsKey state tableKey visibleCtes

            let stateWithTable =
                if isCte then state
                else addPhysicalTable tableName state

            let alias = aliasValue named.Alias
            let aliasPart = Option.ofObj named.Alias
            let symbol =
                TableSymbol(
                    tableName,
                    Option.toObj alias,
                    false,
                    isCte,
                    named.Span)

            let nextScope =
                addNamedSource
                    stateWithTable
                    symbol
                    named.Name
                    aliasPart
                    scope

            let nextState =
                match alias with
                | None ->
                    stateWithTable
                | Some value ->
                    addAliasFact
                        value
                        symbol.Name
                        nextScope.Id
                        stateWithTable

            named :> TableSource, nextScope, nextState

        | :? DerivedTableSource as derived ->
            if String.IsNullOrWhiteSpace(derived.Alias.Value) then
                raise (InvalidOperationException(
                    "Derived table must have an alias before binding."))

            let stateWithSubquery =
                { state with ContainsSubquery = true }

            let boundQuery, stateAfterQuery =
                bindStatement
                    derived.Query
                    None
                    visibleCtes
                    stateWithSubquery

            let alias = derived.Alias.Value.Trim()
            let symbol =
                TableSymbol(
                    "<subquery>",
                    alias,
                    true,
                    false,
                    derived.Span)

            let nextScope =
                addDerivedSource
                    stateAfterQuery
                    symbol
                    derived.Alias
                    scope

            let nextState =
                addAliasFact
                    alias
                    symbol.Name
                    nextScope.Id
                    stateAfterQuery

            CoreBindingAstClone.Derived(derived, boundQuery) :> TableSource,
            nextScope,
            nextState

        | other ->
            raise (InvalidOperationException(
                $"Unsupported table source while binding: {other.GetType().Name}"))

    and private bindSelectItems
        (items: ImmutableArray<SelectItem>)
        scope
        visibleCtes
        state =

        let reversed, finalState =
            (([], state), items)
            ||> Seq.fold (fun (acc, currentState) (item: SelectItem) ->
                let sourceExpression =
                    requireExpr "SELECT item expression" item.Expression

                let expression, nextState =
                    bindExpr
                        sourceExpression
                        scope
                        visibleCtes
                        currentState

                CoreBindingAstClone.SelectItem(item, expression) :: acc,
                nextState)

        reversed |> List.rev |> toImmutableArray, finalState

    and private bindOrderByItems
        (items: ImmutableArray<OrderByItem>)
        scope
        visibleCtes
        state =

        let reversed, finalState =
            (([], state), items)
            ||> Seq.fold (fun (acc, currentState) (item: OrderByItem) ->
                let sourceExpression =
                    requireExpr "ORDER BY expression" item.Expression

                let expression, nextState =
                    bindExpr
                        sourceExpression
                        scope
                        visibleCtes
                        currentState

                CoreBindingAstClone.OrderBy(item, expression) :: acc,
                nextState)

        reversed |> List.rev |> toImmutableArray, finalState

    and private bindExprItems
        (items: ImmutableArray<SqlExpr>)
        scope
        visibleCtes
        state =

        let reversed, finalState =
            (([], state), items)
            ||> Seq.fold (fun (acc, currentState) expression ->
                let sourceExpression =
                    requireExpr "SQL expression collection item" expression

                let bound, nextState =
                    bindExpr
                        sourceExpression
                        scope
                        visibleCtes
                        currentState

                bound :: acc, nextState)

        reversed |> List.rev |> toImmutableArray, finalState

    and private bindExpr
        (expression: SqlExpr)
        scope
        visibleCtes
        state =

        match expression with
        | :? BoundColumnExpr ->
            expression, state

        | :? ColumnExpr as column ->
            bindColumn column scope state :> SqlExpr, state

        | :? LiteralExpr
        | :? IntervalExpr ->
            expression, state

        | :? UnaryExpr as unary ->
            let operand, nextState =
                bindExpr unary.Operand scope visibleCtes state

            CoreBindingAstClone.Unary(unary, operand) :> SqlExpr,
            nextState

        | :? BinaryExpr as binary ->
            let left, stateAfterLeft =
                bindExpr binary.Left scope visibleCtes state

            let right, finalState =
                bindExpr binary.Right scope visibleCtes stateAfterLeft

            CoreBindingAstClone.Binary(binary, left, right) :> SqlExpr,
            finalState

        | :? FunctionCallExpr as functionCall ->
            if functionCall.Name.Parts.Length <> 1
               || functionCall.Name.Parts[0].WasQuoted then
                raise (InvalidOperationException(
                    $"Quoted or qualified function identifier '{identifierName functionCall.Name}' is not supported by the portable Core function registry."))

            let arguments, stateAfterArguments =
                bindExprItems
                    functionCall.Arguments
                    scope
                    visibleCtes
                    state

            let aggregateOrderBy, finalState =
                bindOrderByItems
                    functionCall.AggregateOrderBy
                    scope
                    visibleCtes
                    stateAfterArguments

            CoreBindingAstClone.Function(
                functionCall,
                arguments,
                aggregateOrderBy) :> SqlExpr,
            finalState

        | :? FilterExpr as filter ->
            let inner, stateAfterInner =
                bindExpr filter.Expression scope visibleCtes state

            let predicate, finalState =
                bindExpr
                    filter.Predicate
                    scope
                    visibleCtes
                    stateAfterInner

            CoreBindingAstClone.Filter(filter, inner, predicate) :> SqlExpr,
            finalState

        | :? WindowedExpr as windowed ->
            let inner, stateAfterInner =
                bindExpr windowed.Expression scope visibleCtes state

            let window, finalState =
                bindWindow
                    windowed.Window
                    scope
                    visibleCtes
                    stateAfterInner

            CoreBindingAstClone.Windowed(windowed, inner, window) :> SqlExpr,
            finalState

        | :? CastExpr as cast ->
            let inner, nextState =
                bindExpr cast.Expression scope visibleCtes state

            CoreBindingAstClone.Cast(cast, inner) :> SqlExpr,
            nextState

        | :? CaseExpr as caseExpression ->
            let branchesRev, stateAfterBranches =
                (([], state), caseExpression.Branches)
                ||> Seq.fold (fun (acc, currentState) branch ->
                    let condition, stateAfterCondition =
                        bindExpr
                            branch.Condition
                            scope
                            visibleCtes
                            currentState

                    let value, stateAfterValue =
                        bindExpr
                            branch.Value
                            scope
                            visibleCtes
                            stateAfterCondition

                    CaseBranch(condition, value) :: acc,
                    stateAfterValue)

            let elseExpression, finalState =
                match Option.ofObj caseExpression.ElseExpression with
                | None ->
                    None, stateAfterBranches
                | Some value ->
                    let bound, nextState =
                        bindExpr
                            value
                            scope
                            visibleCtes
                            stateAfterBranches

                    Some bound, nextState

            CoreBindingAstClone.Case(
                caseExpression,
                branchesRev |> List.rev |> toImmutableArray,
                Option.toObj elseExpression) :> SqlExpr,
            finalState

        | :? InExpr as inExpression ->
            let value, stateAfterValue =
                bindExpr
                    inExpression.Value
                    scope
                    visibleCtes
                    state

            let items, finalState =
                bindExprItems
                    inExpression.Items
                    scope
                    visibleCtes
                    stateAfterValue

            CoreBindingAstClone.In(inExpression, value, items) :> SqlExpr,
            finalState

        | :? BetweenExpr as between ->
            let value, stateAfterValue =
                bindExpr
                    between.Value
                    scope
                    visibleCtes
                    state

            let lower, stateAfterLower =
                bindExpr
                    between.Lower
                    scope
                    visibleCtes
                    stateAfterValue

            let upper, finalState =
                bindExpr
                    between.Upper
                    scope
                    visibleCtes
                    stateAfterLower

            CoreBindingAstClone.Between(
                between,
                value,
                lower,
                upper) :> SqlExpr,
            finalState

        | :? IsNullExpr as isNull ->
            let value, nextState =
                bindExpr
                    isNull.Value
                    scope
                    visibleCtes
                    state

            CoreBindingAstClone.IsNull(isNull, value) :> SqlExpr,
            nextState

        | :? SubqueryExpr as subquery ->
            let stateWithSubquery =
                { state with ContainsSubquery = true }

            let boundQuery, nextState =
                bindStatement
                    subquery.Query
                    scope
                    visibleCtes
                    stateWithSubquery

            CoreBindingAstClone.Subquery(subquery, boundQuery) :> SqlExpr,
            nextState

        | :? ExistsExpr as exists ->
            let stateWithSubquery =
                { state with ContainsSubquery = true }

            let boundQuery, nextState =
                bindStatement
                    exists.Query
                    scope
                    visibleCtes
                    stateWithSubquery

            CoreBindingAstClone.Exists(exists, boundQuery) :> SqlExpr,
            nextState

        | other ->
            raise (InvalidOperationException(
                $"Unsupported SQL expression while binding: {other.GetType().Name}"))

    and private bindWindow
        (window: WindowSpec)
        scope
        visibleCtes
        state =

        let partitionBy, stateAfterPartition =
            bindExprItems
                window.PartitionBy
                scope
                visibleCtes
                state

        let orderBy, finalState =
            bindOrderByItems
                window.OrderBy
                scope
                visibleCtes
                stateAfterPartition

        CoreBindingAstClone.Window(
            window,
            partitionBy,
            orderBy),
        finalState

    and private bindColumn
        (column: ColumnExpr)
        scope
        state =

        match scope with
        | None ->
            CoreBindingAstClone.BoundColumn(
                column,
                null,
                false)

        | Some currentScope ->
            let parts = column.Name.Parts
            if parts.IsDefaultOrEmpty then
                raise (InvalidOperationException(
                    "Column identifier has no parts."))

            if parts.Length = 1 then
                match tryResolveSingleVisibleSource false currentScope with
                | Some resolved ->
                    CoreBindingAstClone.BoundColumn(
                        column,
                        resolved.Symbol,
                        resolved.IsOuterReference)

                | None ->
                    CoreBindingAstClone.BoundColumn(
                        column,
                        null,
                        false)

            else
                let qualifierParts =
                    parts
                    |> Seq.take (parts.Length - 1)
                    |> Seq.toArray

                let qualifier =
                    qualifierParts
                    |> Seq.map (fun part -> part.Value)
                    |> String.concat "."

                match resolveQualifier
                        state
                        qualifierParts
                        false
                        currentScope with
                | Some resolved ->
                    CoreBindingAstClone.BoundColumn(
                        column,
                        resolved.Symbol,
                        resolved.IsOuterReference)

                | None ->
                    raise (InvalidOperationException(
                        $"Column '{identifierName column.Name}' references unknown table/alias qualifier '{qualifier}'."))

    /// Bind a SELECT/query-set graph using the F# scope engine.
    let bind (statement: ParsedStatement) : BoundStatement =
        let initialState =
            {
                SourceDialect = statement.SourceDialect
                IdentifierComparer =
                    SqlIdentifierDialectRules.Comparer(
                        statement.SourceDialect)
                PhysicalTables = []
                AliasFactsRev = []
                NextScopeId = 0
                ContainsSubquery = false
                ContainsCte = false
            }

        let bound, finalState =
            bindStatement
                statement.Statement
                None
                []
                initialState

        BoundStatement(
            bound,
            createFacts finalState,
            statement.SourceDialect)
