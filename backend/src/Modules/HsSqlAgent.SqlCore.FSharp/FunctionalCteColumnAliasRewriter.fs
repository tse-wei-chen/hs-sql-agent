namespace HsSqlAgent.SqlCore.Internal

open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation

/// Canonicalizes CTE column-alias lists into explicit projection aliases.
module internal FunctionalCteColumnAliasRewriter =

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    let private identifierText (identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private isWildcard
        (identifier: SqlIdentifier) =

        if identifier.Parts.IsDefaultOrEmpty then
            false
        else
            let tail =
                identifier.Parts[identifier.Parts.Length - 1]

            tail.Value = "*"
            && not tail.WasQuoted

    let private containsWildcard
        (expression: SqlExpr) =

        match expression with
        | :? ColumnExpr as column ->
            isWildcard column.Name
        | :? BoundColumnExpr as column ->
            isWildcard column.Name
        | _ ->
            false

    let rec private rewriteStatement
        (statement: SqlStatement)
        : SqlStatement =

        match statement with
        | :? SelectStatement as select ->
            rewriteSelect select :> SqlStatement

        | :? QueryStatement as query ->
            let head =
                rewriteSelect query.Head

            let operations =
                query.SetOperations
                |> Seq.map (fun operation ->
                    CoreBindingAstClone.SetOperation(
                        operation,
                        rewriteStatement operation.Query))
                |> toImmutableArray

            let orderBy =
                query.OrderBy
                |> Seq.map (fun item ->
                    CoreBindingAstClone.OrderBy(
                        item,
                        rewriteExpression item.Expression))
                |> toImmutableArray

            CoreBindingAstClone.Query(
                query,
                head,
                operations,
                orderBy)
            :> SqlStatement

        | :? UpdateStatement as update ->
            let assignments =
                update.Assignments
                |> Seq.map (fun assignment ->
                    CoreBindingAstClone.Assignment(
                        assignment,
                        rewriteExpression assignment.Value))
                |> toImmutableArray

            let predicate =
                update.Predicate
                |> Option.ofObj
                |> Option.map rewriteExpression
                |> Option.toObj

            CoreBindingAstClone.Update(
                update,
                assignments,
                predicate)
            :> SqlStatement

        | :? DeleteStatement as delete ->
            let predicate =
                delete.Predicate
                |> Option.ofObj
                |> Option.map rewriteExpression
                |> Option.toObj

            CoreBindingAstClone.Delete(
                delete,
                predicate)
            :> SqlStatement

        | :? InsertStatement as insert ->
            CoreBindingAstClone.Insert(
                insert,
                rewriteInsertSource insert.Source)
            :> SqlStatement

        | other ->
            raise (SqlCompilationException(
                $"Unsupported statement while canonicalizing CTE column aliases: {other.GetType().Name}"))

    and private rewriteSelect
        (select: SelectStatement)
        : SelectStatement =

        let ctes =
            select.Ctes
            |> Seq.map rewriteCte
            |> toImmutableArray

        let fromSource =
            select.From
            |> Option.ofObj
            |> Option.map rewriteSource
            |> Option.toObj

        let joins =
            select.Joins
            |> Seq.map (fun join ->
                CoreBindingAstClone.Join(
                    join,
                    rewriteSource join.Source,
                    join.Predicate
                    |> Option.ofObj
                    |> Option.map rewriteExpression
                    |> Option.toObj))
            |> toImmutableArray

        let projection =
            select.Select
            |> Seq.map (fun item ->
                CoreBindingAstClone.SelectItem(
                    item,
                    rewriteExpression item.Expression))
            |> toImmutableArray

        let groupBy =
            select.GroupBy
            |> Seq.map rewriteExpression
            |> toImmutableArray

        let orderBy =
            select.OrderBy
            |> Seq.map (fun item ->
                CoreBindingAstClone.OrderBy(
                    item,
                    rewriteExpression item.Expression))
            |> toImmutableArray

        CoreBindingAstClone.Select(
            select,
            ctes,
            fromSource,
            joins,
            projection,
            select.Where
            |> Option.ofObj
            |> Option.map rewriteExpression
            |> Option.toObj,
            groupBy,
            select.Having
            |> Option.ofObj
            |> Option.map rewriteExpression
            |> Option.toObj,
            orderBy)

    and private rewriteCte
        (cte: CteDefinition)
        : CteDefinition =

        let query =
            rewriteStatement cte.Query

        if cte.ColumnAliases.IsDefaultOrEmpty then
            CoreBindingAstClone.Cte(
                cte,
                query)
        else
            let aliased =
                applyOutputAliases
                    query
                    cte.ColumnAliases
                    cte.Name

            CoreBindingAstClone.CteColumns(
                cte,
                ImmutableArray<SqlIdentifier>.Empty,
                aliased)

    and private applyOutputAliases
        (statement: SqlStatement)
        (aliases: ImmutableArray<SqlIdentifier>)
        (cteName: SqlIdentifier)
        : SqlStatement =

        match statement with
        | :? SelectStatement as select ->
            applyOutputAliasesToSelect
                select
                aliases
                cteName
            :> SqlStatement

        | :? QueryStatement as query ->
            let head =
                applyOutputAliasesToSelect
                    query.Head
                    aliases
                    cteName

            CoreBindingAstClone.Query(
                query,
                head,
                query.SetOperations,
                query.OrderBy)
            :> SqlStatement

        | _ ->
            raise (SqlCompilationException(
                $"CTE '{identifierText cteName}' column aliases require a SELECT query body."))

    and private applyOutputAliasesToSelect
        (select: SelectStatement)
        (aliases: ImmutableArray<SqlIdentifier>)
        (cteName: SqlIdentifier)
        : SelectStatement =

        if aliases
           |> Seq.exists (fun alias ->
               alias.Parts.Length <> 1) then
            raise (SqlCompilationException(
                $"CTE '{identifierText cteName}' column aliases must be unqualified identifiers."))

        if select.Select
           |> Seq.exists (fun item ->
               containsWildcard item.Expression) then
            raise (SqlCompilationException(
                $"CTE '{identifierText cteName}' column aliases cannot be lowered safely when the CTE projection contains a wildcard."))

        if select.Select.Length <> aliases.Length then
            raise (SqlCompilationException(
                $"CTE '{identifierText cteName}' declares {aliases.Length} column alias(es) but its statically modeled projection has {select.Select.Length} column(s)."))

        let projection =
            select.Select
            |> Seq.mapi (fun index item ->
                CoreBindingAstClone.SelectItemAlias(
                    item,
                    aliases[index].Parts[0]))
            |> toImmutableArray

        CoreBindingAstClone.Select(
            select,
            select.Ctes,
            select.From,
            select.Joins,
            projection,
            select.Where,
            select.GroupBy,
            select.Having,
            select.OrderBy)

    and private rewriteSource
        (source: TableSource)
        : TableSource =

        match source with
        | :? NamedTableSource ->
            source

        | :? DerivedTableSource as derived ->
            CoreBindingAstClone.Derived(
                derived,
                rewriteStatement derived.Query)
            :> TableSource

        | other ->
            raise (SqlCompilationException(
                $"Unsupported table source while canonicalizing CTE column aliases: {other.GetType().Name}"))

    and private rewriteInsertSource
        (source: InsertSource)
        : InsertSource =

        match source with
        | :? InsertValuesSource as values ->
            let rows =
                values.Rows
                |> Seq.map (fun row ->
                    row
                    |> Seq.map rewriteExpression
                    |> toImmutableArray)
                |> toImmutableArray

            InsertValuesSource(
                rows,
                values.Span)
            :> InsertSource

        | :? InsertQuerySource as query ->
            CoreBindingAstClone.InsertQuery(
                query,
                rewriteStatement query.Query)
            :> InsertSource

        | other ->
            raise (SqlCompilationException(
                $"Unsupported INSERT source while canonicalizing CTE column aliases: {other.GetType().Name}"))

    and private rewriteExpression
        (expression: SqlExpr)
        : SqlExpr =

        match expression with
        | :? LiteralExpr
        | :? ColumnExpr
        | :? BoundColumnExpr
        | :? IntervalExpr ->
            expression

        | :? UnaryExpr as unary ->
            CoreBindingAstClone.Unary(
                unary,
                rewriteExpression unary.Operand)
            :> SqlExpr

        | :? BinaryExpr as binary ->
            CoreBindingAstClone.Binary(
                binary,
                rewriteExpression binary.Left,
                rewriteExpression binary.Right)
            :> SqlExpr

        | :? FunctionCallExpr as functionCall ->
            let arguments =
                functionCall.Arguments
                |> Seq.map rewriteExpression
                |> toImmutableArray

            let aggregateOrderBy =
                functionCall.AggregateOrderBy
                |> Seq.map (fun item ->
                    CoreBindingAstClone.OrderBy(
                        item,
                        rewriteExpression item.Expression))
                |> toImmutableArray

            CoreBindingAstClone.Function(
                functionCall,
                arguments,
                aggregateOrderBy)
            :> SqlExpr

        | :? FilterExpr as filter ->
            CoreBindingAstClone.Filter(
                filter,
                rewriteExpression filter.Expression,
                rewriteExpression filter.Predicate)
            :> SqlExpr

        | :? WindowedExpr as windowed ->
            let partitionBy =
                windowed.Window.PartitionBy
                |> Seq.map rewriteExpression
                |> toImmutableArray

            let orderBy =
                windowed.Window.OrderBy
                |> Seq.map (fun item ->
                    CoreBindingAstClone.OrderBy(
                        item,
                        rewriteExpression item.Expression))
                |> toImmutableArray

            let window =
                CoreBindingAstClone.Window(
                    windowed.Window,
                    partitionBy,
                    orderBy)

            CoreBindingAstClone.Windowed(
                windowed,
                rewriteExpression windowed.Expression,
                window)
            :> SqlExpr

        | :? CastExpr as cast ->
            CoreBindingAstClone.Cast(
                cast,
                rewriteExpression cast.Expression)
            :> SqlExpr

        | :? CaseExpr as caseExpression ->
            let branches =
                caseExpression.Branches
                |> Seq.map (fun branch ->
                    CaseBranch(
                        rewriteExpression branch.Condition,
                        rewriteExpression branch.Value))
                |> toImmutableArray

            CoreBindingAstClone.Case(
                caseExpression,
                branches,
                caseExpression.ElseExpression
                |> Option.ofObj
                |> Option.map rewriteExpression
                |> Option.toObj)
            :> SqlExpr

        | :? InExpr as inExpression ->
            let items =
                inExpression.Items
                |> Seq.map rewriteExpression
                |> toImmutableArray

            CoreBindingAstClone.In(
                inExpression,
                rewriteExpression inExpression.Value,
                items)
            :> SqlExpr

        | :? BetweenExpr as between ->
            CoreBindingAstClone.Between(
                between,
                rewriteExpression between.Value,
                rewriteExpression between.Lower,
                rewriteExpression between.Upper)
            :> SqlExpr

        | :? IsNullExpr as isNull ->
            CoreBindingAstClone.IsNull(
                isNull,
                rewriteExpression isNull.Value)
            :> SqlExpr

        | :? SubqueryExpr as subquery ->
            CoreBindingAstClone.Subquery(
                subquery,
                rewriteStatement subquery.Query)
            :> SqlExpr

        | :? ExistsExpr as exists ->
            CoreBindingAstClone.Exists(
                exists,
                rewriteStatement exists.Query)
            :> SqlExpr

        | other ->
            raise (SqlCompilationException(
                $"Unsupported expression while canonicalizing CTE column aliases: {other.GetType().Name}"))

    let rewrite
        (statement: SqlStatement)
        : SqlStatement =

        rewriteStatement statement
