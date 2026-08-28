namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation

/// Fail-closed structural Core AST rewrite implemented in F#.
///
/// Child nodes are rewritten first, then the supplied expression hook is
/// applied to the rebuilt expression node. This mirrors the legacy
/// CoreSqlAstRewriter traversal contract while making the recursive shape
/// explicit in F#.
module internal FunctionalAstRewriter =

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    let rewrite
        (context: string)
        (rewriteExpressionNode: SqlExpr -> SqlExpr)
        (statement: SqlStatement)
        : SqlStatement =

        if String.IsNullOrWhiteSpace(context) then
            raise (ArgumentException(
                "AST rewrite context cannot be empty.",
                "context"))

        let context = context.Trim()

        let rec rewriteStatement
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
                    rewriteOrderBy query.OrderBy

                CoreBindingAstClone.Query(
                    query,
                    head,
                    operations,
                    orderBy)
                :> SqlStatement

            | :? InsertStatement as insert ->
                CoreBindingAstClone.Insert(
                    insert,
                    rewriteInsertSource insert.Source)
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
                    match Option.ofObj update.Predicate with
                    | Some value ->
                        Some(rewriteExpression value)
                    | None ->
                        None

                CoreBindingAstClone.Update(
                    update,
                    assignments,
                    Option.toObj predicate)
                :> SqlStatement

            | :? DeleteStatement as delete ->
                let predicate =
                    match Option.ofObj delete.Predicate with
                    | Some value ->
                        Some(rewriteExpression value)
                    | None ->
                        None

                CoreBindingAstClone.Delete(
                    delete,
                    Option.toObj predicate)
                :> SqlStatement

            | other ->
                raise (SqlCompilationException(
                    $"Unsupported statement during {context} AST rewrite: {other.GetType().Name}"))

        and rewriteInsertSource
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

            | :? InsertQuerySource as querySource ->
                CoreBindingAstClone.InsertQuery(
                    querySource,
                    rewriteStatement querySource.Query)
                :> InsertSource

            | other ->
                raise (SqlCompilationException(
                    $"Unsupported INSERT source during {context} AST rewrite: {other.GetType().Name}"))

        and rewriteSelect
            (select: SelectStatement)
            : SelectStatement =

            let ctes =
                select.Ctes
                |> Seq.map (fun cte ->
                    CoreBindingAstClone.Cte(
                        cte,
                        rewriteStatement cte.Query))
                |> toImmutableArray

            let selectItems =
                select.Select
                |> Seq.map (fun item ->
                    CoreBindingAstClone.SelectItem(
                        item,
                        rewriteExpression item.Expression))
                |> toImmutableArray

            let fromSource =
                match Option.ofObj select.From with
                | Some source ->
                    Some(rewriteSource source)
                | None ->
                    None

            let joins =
                select.Joins
                |> Seq.map (fun join ->
                    let predicate =
                        match Option.ofObj join.Predicate with
                        | Some value ->
                            Some(rewriteExpression value)
                        | None ->
                            None

                    CoreBindingAstClone.Join(
                        join,
                        rewriteSource join.Source,
                        Option.toObj predicate))
                |> toImmutableArray

            let whereExpression =
                match Option.ofObj select.Where with
                | Some value ->
                    Some(rewriteExpression value)
                | None ->
                    None

            let groupBy =
                select.GroupBy
                |> Seq.map rewriteExpression
                |> toImmutableArray

            let having =
                match Option.ofObj select.Having with
                | Some value ->
                    Some(rewriteExpression value)
                | None ->
                    None

            let orderBy =
                rewriteOrderBy select.OrderBy

            CoreBindingAstClone.Select(
                select,
                ctes,
                Option.toObj fromSource,
                joins,
                selectItems,
                Option.toObj whereExpression,
                groupBy,
                Option.toObj having,
                orderBy)

        and rewriteSource
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
                    $"Unsupported table source during {context} AST rewrite: {other.GetType().Name}"))

        and rewriteOrderBy
            (orderBy: ImmutableArray<OrderByItem>) =

            orderBy
            |> Seq.map (fun item ->
                CoreBindingAstClone.OrderBy(
                    item,
                    rewriteExpression item.Expression))
            |> toImmutableArray

        and rewriteBranches
            (branches: ImmutableArray<CaseBranch>) =

            branches
            |> Seq.map (fun branch ->
                CaseBranch(
                    rewriteExpression branch.Condition,
                    rewriteExpression branch.Value))
            |> toImmutableArray

        and rewriteExpression
            (expression: SqlExpr)
            : SqlExpr =

            let rewritten =
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
                        rewriteOrderBy
                            functionCall.AggregateOrderBy

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
                        rewriteOrderBy
                            windowed.Window.OrderBy

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

                | :? SimpleCaseExpr as simpleCase ->
                    let branches =
                        rewriteBranches simpleCase.Branches

                    let elseExpression =
                        match Option.ofObj simpleCase.ElseExpression with
                        | Some value ->
                            Some(rewriteExpression value)
                        | None ->
                            None

                    SimpleCaseExpr(
                        branches,
                        Option.toObj elseExpression,
                        simpleCase.Span)
                    :> SqlExpr

                | :? CaseExpr as caseExpression ->
                    let branches =
                        rewriteBranches caseExpression.Branches

                    let elseExpression =
                        match Option.ofObj caseExpression.ElseExpression with
                        | Some value ->
                            Some(rewriteExpression value)
                        | None ->
                            None

                    CoreBindingAstClone.Case(
                        caseExpression,
                        branches,
                        Option.toObj elseExpression)
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
                        $"Unsupported expression during {context} AST rewrite: {other.GetType().Name}"))

            rewriteExpressionNode rewritten

        rewriteStatement statement
