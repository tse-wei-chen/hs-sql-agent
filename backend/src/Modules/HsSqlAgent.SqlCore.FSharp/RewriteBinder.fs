namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Scope binding over the pure F# compiler model.
/// No compatibility AST or runtime type tests are used here.
module internal RewriteBinder =

    type private SourceBinding = { Qualifiers: string list }
    type private Scope = { Parent: Scope option; Sources: SourceBinding list }

    let private identifierParts (identifier: Identifier) = Identifier.parts identifier
    let private identifierText identifier = identifierParts identifier |> List.map (fun part -> part.Value) |> String.concat "."
    let private equalsName left right = StringComparer.OrdinalIgnoreCase.Equals(left, right)
    let private containsQualifier qualifier source = source.Qualifiers |> List.exists (equalsName qualifier)

    let rec private qualifierExists qualifier scope =
        if scope.Sources |> List.exists (containsQualifier qualifier) then true
        else match scope.Parent with | Some parent -> qualifierExists qualifier parent | None -> false

    let private sourceBinding source =
        match source with
        | NamedTable(name, alias) ->
            let parts = identifierParts name
            let tableTail = parts |> List.last |> fun part -> part.Value
            let fullName = identifierText name
            let qualifiers = match alias with | Some value -> [ value.Value ] | None when equalsName tableTail fullName -> [ tableTail ] | None -> [ tableTail; fullName ]
            { Qualifiers = qualifiers }
        | DerivedTable(_, alias) -> { Qualifiers = [ alias.Value ] }

    let private ensureDistinctSources sources =
        let qualifiers = sources |> List.collect (fun source -> source.Qualifiers)
        qualifiers |> List.iteri (fun index qualifier -> qualifiers |> List.skip (index + 1) |> List.tryFind (equalsName qualifier) |> Option.iter (fun _ -> invalidOp ("Duplicate table qualifier '" + qualifier + "' in SQL scope.")))

    let private bindColumn scope identifier =
        match identifierParts identifier with
        | [] -> invalidOp "Column identifier cannot be empty."
        | [ _ ] -> identifier
        | qualifier :: _ -> if not (qualifierExists qualifier.Value scope) then invalidOp ("Unknown table qualifier '" + qualifier.Value + "'."); identifier

    let rec private bindExpr scope expression =
        match expression with
        | Column identifier -> Column(bindColumn scope identifier)
        | Literal _ | Interval _ -> expression
        | Unary(op, operand) -> Unary(op, bindExpr scope operand)
        | Binary(op, left, right) -> Binary(op, bindExpr scope left, bindExpr scope right)
        | FunctionCall call -> FunctionCall { call with Arguments = call.Arguments |> List.map (bindExpr scope) }
        | FilteredAggregate(value, predicate) -> FilteredAggregate(bindExpr scope value, bindExpr scope predicate)
        | Windowed(value, window) -> Windowed(bindExpr scope value, bindWindow scope window)
        | Cast(value, targetType) -> Cast(bindExpr scope value, targetType)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(bindExpr scope input, branches |> List.map (fun branch -> { Match = bindExpr scope branch.Match; Result = bindExpr scope branch.Result }), fallback |> Option.map (bindExpr scope))
        | SearchedCase(branches, fallback) ->
            SearchedCase(branches |> List.map (fun branch -> { Condition = bindExpr scope branch.Condition; Result = bindExpr scope branch.Result }), fallback |> Option.map (bindExpr scope))
        | InList(value, items, negated) -> InList(bindExpr scope value, items |> List.map (bindExpr scope), negated)
        | Between(value, lower, upper, negated) -> Between(bindExpr scope value, bindExpr scope lower, bindExpr scope upper, negated)
        | IsNull(value, negated) -> IsNull(bindExpr scope value, negated)
        | ScalarSubquery query -> ScalarSubquery(bindQuery (Some scope) query)
        | Exists(query, negated) -> Exists(bindQuery (Some scope) query, negated)

    and private bindOrderBy scope orderBy = { orderBy with Expression = bindExpr scope orderBy.Expression }
    and private bindWindow scope window = { window with PartitionBy = window.PartitionBy |> List.map (bindExpr scope); OrderBy = window.OrderBy |> List.map (bindOrderBy scope) }
    and private bindTableSource parentScope source = match source with | NamedTable _ -> source | DerivedTable(query, alias) -> DerivedTable(bindQuery parentScope query, alias)

    and private bindJoin scope join =
        let source = bindTableSource (Some scope) join.Source
        let binding = sourceBinding source
        let extended = { scope with Sources = scope.Sources @ [ binding ] }
        ensureDistinctSources extended.Sources
        let boundJoin = match join with | CrossJoin _ -> CrossJoin source | OnJoin(kind, _, predicate) -> OnJoin(kind, source, bindExpr extended predicate)
        boundJoin, extended

    and private bindSelect parentScope select =
        let from = select.From |> Option.map (bindTableSource parentScope)
        let initialSources = from |> Option.map sourceBinding |> Option.toList
        ensureDistinctSources initialSources
        let initialScope = { Parent = parentScope; Sources = initialSources }
        let joins, scope =
            (([], initialScope), select.Joins)
            ||> List.fold (fun (boundJoins, currentScope) join -> let boundJoin, nextScope = bindJoin currentScope join in boundJoins @ [ boundJoin ], nextScope)
        { select with
            From = from
            Joins = joins
            ProjectionItems = select.ProjectionItems |> NonEmpty.map (fun item -> { item with Expression = bindExpr scope item.Expression })
            Where = select.Where |> Option.map (bindExpr scope)
            GroupBy = select.GroupBy |> List.map (bindExpr scope)
            Having = select.Having |> Option.map (bindExpr scope) }, scope

    and private bindQuery parentScope query =
        let head, headScope = bindSelect parentScope query.Head
        { query with
            Head = head
            SetOperations = query.SetOperations |> List.map (fun branch -> { branch with Query = bindQuery parentScope branch.Query })
            OrderBy = query.OrderBy |> List.map (bindOrderBy headScope) }

    let private bindAssignment scope assignment = { assignment with Value = bindExpr scope assignment.Value }

    let private bindDocument document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(bindQuery None query)
            | InsertStatement insert ->
                let emptyScope = { Parent = None; Sources = [] }
                let input =
                    match insert.Input with
                    | Values rows -> Values(rows |> NonEmpty.map (NonEmpty.map (bindExpr emptyScope)))
                    | QuerySource query -> QuerySource(bindQuery None query)
                    | DefaultValues -> DefaultValues
                InsertStatement { insert with Input = input }
            | UpdateStatement update ->
                let binding = sourceBinding (NamedTable(update.Target, None))
                let scope = { Parent = None; Sources = [ binding ] }
                UpdateStatement { update with AssignmentItems = update.AssignmentItems |> NonEmpty.map (bindAssignment scope); Where = update.Where |> Option.map (bindExpr scope) }
            | DeleteStatement delete ->
                let binding = sourceBinding (NamedTable(delete.Target, None))
                let scope = { Parent = None; Sources = [ binding ] }
                DeleteStatement { delete with Where = delete.Where |> Option.map (bindExpr scope) }
        { document with Statement = statement }

    let bind parsed = Transition.bind bindDocument parsed
