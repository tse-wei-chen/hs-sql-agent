namespace HsSqlAgent.SqlCore.Rewrite

open System
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Scope binding over the pure F# compiler model.
/// No compatibility AST or runtime type tests are used here.
module internal RewriteBinder =

    type private SourceBinding =
        { Qualifiers: string list }

    type private Scope =
        { Parent: Scope option
          Sources: SourceBinding list }

    let private identifierParts (identifier: Identifier) = Identifier.parts identifier

    let private identifierText (identifier: Identifier) =
        identifierParts identifier
        |> List.map (fun (part: IdentifierPart) -> part.Value)
        |> String.concat "."

    let private equalsName (left: string) (right: string) =
        StringComparer.OrdinalIgnoreCase.Equals(left, right)

    let private containsQualifier qualifier (source: SourceBinding) =
        source.Qualifiers |> List.exists (equalsName qualifier)

    let rec private qualifierExists qualifier (scope: Scope) =
        if scope.Sources |> List.exists (containsQualifier qualifier) then true
        else
            match scope.Parent with
            | Some parent -> qualifierExists qualifier parent
            | None -> false

    let private sourceBinding (source: TableSource) : SourceBinding =
        match source with
        | NamedTable(name, alias) ->
            let parts = identifierParts name
            let tableTail = parts |> List.last |> fun part -> part.Value
            let fullName = identifierText name
            let qualifiers =
                match alias with
                | Some value -> [ value.Value ]
                | None when equalsName tableTail fullName -> [ tableTail ]
                | None -> [ tableTail; fullName ]
            { Qualifiers = qualifiers }
        | DerivedTable(_, alias) ->
            { Qualifiers = [ alias.Value ] }

    let private ensureDistinctSources (sources: SourceBinding list) =
        let qualifiers = sources |> List.collect (fun source -> source.Qualifiers)
        qualifiers
        |> List.iteri (fun index qualifier ->
            qualifiers
            |> List.skip (index + 1)
            |> List.tryFind (equalsName qualifier)
            |> Option.iter (fun _ -> invalidOp ("Duplicate table qualifier '" + qualifier + "' in SQL scope.")))

    let private bindColumn (scope: Scope) (identifier: Identifier) : Identifier =
        match identifierParts identifier with
        | [] -> invalidOp "Column identifier cannot be empty."
        | [ _ ] -> identifier
        | qualifier :: _ ->
            if not (qualifierExists qualifier.Value scope) then
                invalidOp ("Unknown table qualifier '" + qualifier.Value + "'.")
            identifier

    let rec private bindExpr (scope: Scope) (expression: Expr) : Expr =
        match expression with
        | Column identifier -> Column(bindColumn scope identifier)
        | Literal _
        | Interval _ -> expression
        | Unary(op, operand) -> Unary(op, bindExpr scope operand)
        | Binary(op, left, right) -> Binary(op, bindExpr scope left, bindExpr scope right)
        | FunctionCall call ->
            FunctionCall { call with Arguments = call.Arguments |> List.map (bindExpr scope) }
        | FilteredAggregate(value, predicate) ->
            FilteredAggregate(bindExpr scope value, bindExpr scope predicate)
        | Windowed(value, window) ->
            Windowed(bindExpr scope value, bindWindow scope window)
        | Cast(value, targetType) -> Cast(bindExpr scope value, targetType)
        | SimpleCase(input, branches, fallback) ->
            SimpleCase(
                bindExpr scope input,
                branches
                |> List.map (fun (branch: SimpleCaseBranch) ->
                    { Match = bindExpr scope branch.Match
                      Result = bindExpr scope branch.Result }),
                fallback |> Option.map (bindExpr scope))
        | SearchedCase(branches, fallback) ->
            SearchedCase(
                branches
                |> List.map (fun (branch: SearchedCaseBranch) ->
                    { Condition = bindExpr scope branch.Condition
                      Result = bindExpr scope branch.Result }),
                fallback |> Option.map (bindExpr scope))
        | InList(value, items, negated) ->
            InList(bindExpr scope value, items |> List.map (bindExpr scope), negated)
        | Between(value, lower, upper, negated) ->
            Between(bindExpr scope value, bindExpr scope lower, bindExpr scope upper, negated)
        | IsNull(value, negated) -> IsNull(bindExpr scope value, negated)
        | ScalarSubquery query -> ScalarSubquery(bindQuery (Some scope) query)
        | Exists(query, negated) -> Exists(bindQuery (Some scope) query, negated)

    and private bindOrderBy (scope: Scope) (orderBy: OrderBy) : OrderBy =
        { orderBy with Expression = bindExpr scope orderBy.Expression }

    and private bindWindow (scope: Scope) (window: WindowSpec) : WindowSpec =
        { window with
            PartitionBy = window.PartitionBy |> List.map (bindExpr scope)
            OrderBy = window.OrderBy |> List.map (bindOrderBy scope) }

    and private bindTableSource (parentScope: Scope option) (source: TableSource) : TableSource =
        match source with
        | NamedTable _ -> source
        | DerivedTable(query, alias) -> DerivedTable(bindQuery parentScope query, alias)

    and private bindJoin (scope: Scope) (join: Join) : Join * Scope =
        let source = bindTableSource (Some scope) join.Source
        let binding = sourceBinding source
        let extended = { scope with Sources = scope.Sources @ [ binding ] }
        ensureDistinctSources extended.Sources
        let predicate = join.Predicate |> Option.map (bindExpr extended)
        { join with Source = source; Predicate = predicate }, extended

    and private bindSelect (parentScope: Scope option) (select: Select) : Select * Scope =
        let from = select.From |> Option.map (bindTableSource parentScope)
        let initialSources = from |> Option.map sourceBinding |> Option.toList
        ensureDistinctSources initialSources
        let initialScope = { Parent = parentScope; Sources = initialSources }

        let joins, scope =
            (([], initialScope), select.Joins)
            ||> List.fold (fun (boundJoins: Join list, currentScope: Scope) (join: Join) ->
                let boundJoin, nextScope = bindJoin currentScope join
                boundJoins @ [ boundJoin ], nextScope)

        { select with
            From = from
            Joins = joins
            Projection =
                select.Projection
                |> List.map (fun (item: SelectItem) ->
                    { item with Expression = bindExpr scope item.Expression })
            Where = select.Where |> Option.map (bindExpr scope)
            GroupBy = select.GroupBy |> List.map (bindExpr scope)
            Having = select.Having |> Option.map (bindExpr scope) }, scope

    and private bindQuery (parentScope: Scope option) (query: Query) : Query =
        let head, headScope = bindSelect parentScope query.Head
        let setOperations =
            query.SetOperations
            |> List.map (fun (branch: SetBranch) ->
                { branch with Query = bindQuery parentScope branch.Query })
        { query with
            Head = head
            SetOperations = setOperations
            OrderBy = query.OrderBy |> List.map (bindOrderBy headScope) }

    let private bindAssignment (scope: Scope) (assignment: Assignment) : Assignment =
        { assignment with Value = bindExpr scope assignment.Value }

    let private bindDocument (document: Document) : Document =
        let statement =
            match document.Statement with
            | QueryStatement query -> QueryStatement(bindQuery None query)
            | InsertStatement insert ->
                let emptyScope : Scope = { Parent = None; Sources = [] }
                let rows = insert.Rows |> List.map (List.map (bindExpr emptyScope))
                let source = insert.Source |> Option.map (bindQuery None)
                InsertStatement { insert with Rows = rows; Source = source }
            | UpdateStatement update ->
                let binding = sourceBinding (NamedTable(update.Target, None))
                let scope : Scope = { Parent = None; Sources = [ binding ] }
                UpdateStatement
                    { update with
                        Assignments = update.Assignments |> List.map (bindAssignment scope)
                        Where = update.Where |> Option.map (bindExpr scope) }
            | DeleteStatement delete ->
                let binding = sourceBinding (NamedTable(delete.Target, None))
                let scope : Scope = { Parent = None; Sources = [ binding ] }
                DeleteStatement { delete with Where = delete.Where |> Option.map (bindExpr scope) }
        { document with Statement = statement }

    let bind parsed =
        Transition.bind bindDocument parsed
