namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Generic
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Canonical NULL-ordering rewrite implemented in F#.
///
/// Default-equivalent NULLS modifiers are erased for targets that need a
/// rewrite. Inverse explicit ordering is lowered only for a stable bound
/// row-source column, matching the legacy fail-closed contract.
module internal FunctionalNullOrderingRewriter =

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    let private isTargetDefault
        (item: OrderByItem) =

        (not item.Descending
         && item.NullOrdering = NullOrderingKind.First)
        || (item.Descending
            && item.NullOrdering = NullOrderingKind.Last)

    let private isInverseExplicitOrdering
        (item: OrderByItem) =

        (not item.Descending
         && item.NullOrdering = NullOrderingKind.Last)
        || (item.Descending
            && item.NullOrdering = NullOrderingKind.First)

    let private isStableRowSourceColumn
        (expression: SqlExpr)
        (blockedAliases: HashSet<string> option) =

        match expression with
        | :? BoundColumnExpr as column
            when Option.isSome (Option.ofObj column.Source)
                 && not column.Name.Parts.IsDefaultOrEmpty ->

            if column.Name.Parts.Length > 1 then
                true
            else
                match blockedAliases with
                | None ->
                    true
                | Some aliases ->
                    not (
                        aliases.Contains(
                            column.Name.Parts[0].Value))

        | _ ->
            false

    let private createNullRankOrder
        (item: OrderByItem)
        (expression: SqlExpr) =

        let nullRank =
            if item.NullOrdering
               = NullOrderingKind.Last then
                1
            else
                0

        let nonNullRank =
            if item.NullOrdering
               = NullOrderingKind.Last then
                0
            else
                1

        let rankExpression =
            CaseExpr(
                ImmutableArray.Create(
                    CaseBranch(
                        IsNullExpr(
                            expression,
                            false,
                            item.Span),
                        LiteralExpr(
                            nullRank,
                            item.Span))),
                LiteralExpr(
                    nonNullRank,
                    item.Span),
                item.Span)

        OrderByItem(
            rankExpression,
            false,
            NullOrderingKind.Default,
            item.Span)

    let rec private rewriteStatement
        (statement: SqlStatement)
        targetProvider =

        match statement with
        | :? SelectStatement as select ->
            rewriteSelect
                select
                targetProvider
            :> SqlStatement

        | :? QueryStatement as query ->
            let head =
                rewriteSelect
                    query.Head
                    targetProvider

            let setOperations =
                query.SetOperations
                |> Seq.map (fun operation ->
                    CoreBindingAstClone.SetOperation(
                        operation,
                        rewriteStatement
                            operation.Query
                            targetProvider))
                |> toImmutableArray

            let orderBy =
                rewriteOrderBy
                    query.OrderBy
                    targetProvider
                    false
                    None

            CoreBindingAstClone.Query(
                query,
                head,
                setOperations,
                orderBy)
            :> SqlStatement

        | :? UpdateStatement as update ->
            let assignments =
                update.Assignments
                |> Seq.map (fun assignment ->
                    CoreBindingAstClone.Assignment(
                        assignment,
                        rewriteExpression
                            assignment.Value
                            targetProvider))
                |> toImmutableArray

            let predicate =
                match Option.ofObj update.Predicate with
                | Some value ->
                    Some(
                        rewriteExpression
                            value
                            targetProvider)
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
                    Some(
                        rewriteExpression
                            value
                            targetProvider)
                | None ->
                    None

            CoreBindingAstClone.Delete(
                delete,
                Option.toObj predicate)
            :> SqlStatement

        | :? InsertStatement as insert ->
            CoreBindingAstClone.Insert(
                insert,
                rewriteInsertSource
                    insert.Source
                    targetProvider)
            :> SqlStatement

        | other ->
            raise (SqlCompilationException(
                $"Unsupported statement while canonicalizing NULL ordering: {other.GetType().Name}"))

    and private rewriteSelect
        (select: SelectStatement)
        targetProvider =

        let blockedAliases =
            let aliases =
                HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)

            for item in select.Select do
                match Option.ofObj item.Alias with
                | Some alias ->
                    aliases.Add(alias.Value)
                    |> ignore
                | None ->
                    ()

            aliases

        let ctes =
            select.Ctes
            |> Seq.map (fun cte ->
                CoreBindingAstClone.Cte(
                    cte,
                    rewriteStatement
                        cte.Query
                        targetProvider))
            |> toImmutableArray

        let selectItems =
            select.Select
            |> Seq.map (fun item ->
                CoreBindingAstClone.SelectItem(
                    item,
                    rewriteExpression
                        item.Expression
                        targetProvider))
            |> toImmutableArray

        let fromSource =
            match Option.ofObj select.From with
            | Some source ->
                Some(
                    rewriteSource
                        source
                        targetProvider)
            | None ->
                None

        let joins =
            select.Joins
            |> Seq.map (fun join ->
                let predicate =
                    match Option.ofObj join.Predicate with
                    | Some value ->
                        Some(
                            rewriteExpression
                                value
                                targetProvider)
                    | None ->
                        None

                CoreBindingAstClone.Join(
                    join,
                    rewriteSource
                        join.Source
                        targetProvider,
                    Option.toObj predicate))
            |> toImmutableArray

        let whereExpression =
            match Option.ofObj select.Where with
            | Some value ->
                Some(
                    rewriteExpression
                        value
                        targetProvider)
            | None ->
                None

        let groupBy =
            select.GroupBy
            |> Seq.map (fun expression ->
                rewriteExpression
                    expression
                    targetProvider)
            |> toImmutableArray

        let having =
            match Option.ofObj select.Having with
            | Some value ->
                Some(
                    rewriteExpression
                        value
                        targetProvider)
            | None ->
                None

        let orderBy =
            rewriteOrderBy
                select.OrderBy
                targetProvider
                (not select.Distinct)
                (Some blockedAliases)

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

    and private rewriteSource
        (source: TableSource)
        targetProvider =

        match source with
        | :? NamedTableSource ->
            source

        | :? DerivedTableSource as derived ->
            CoreBindingAstClone.Derived(
                derived,
                rewriteStatement
                    derived.Query
                    targetProvider)
            :> TableSource

        | other ->
            raise (SqlCompilationException(
                $"Unsupported table source while canonicalizing NULL ordering: {other.GetType().Name}"))

    and private rewriteInsertSource
        (source: InsertSource)
        targetProvider =

        match source with
        | :? InsertValuesSource as values ->
            let rows =
                values.Rows
                |> Seq.map (fun row ->
                    row
                    |> Seq.map (fun value ->
                        rewriteExpression
                            value
                            targetProvider)
                    |> toImmutableArray)
                |> toImmutableArray

            InsertValuesSource(
                rows,
                values.Span)
            :> InsertSource

        | :? InsertQuerySource as query ->
            CoreBindingAstClone.InsertQuery(
                query,
                rewriteStatement
                    query.Query
                    targetProvider)
            :> InsertSource

        | other ->
            raise (SqlCompilationException(
                $"Unsupported INSERT source while canonicalizing NULL ordering: {other.GetType().Name}"))

    and private rewriteOrderBy
        (orderBy: ImmutableArray<OrderByItem>)
        targetProvider
        allowInverseColumnRewrite
        blockedAliases =

        let result =
            ResizeArray<OrderByItem>(
                orderBy.Length * 2)

        for item in orderBy do
            let expression =
                rewriteExpression
                    item.Expression
                    targetProvider

            if isTargetDefault item then
                result.Add(
                    OrderByItem(
                        expression,
                        item.Descending,
                        NullOrderingKind.Default,
                        item.Span))

            elif allowInverseColumnRewrite
                 && isInverseExplicitOrdering item
                 && isStableRowSourceColumn
                        expression
                        blockedAliases then

                result.Add(
                    createNullRankOrder
                        item
                        expression)

                result.Add(
                    OrderByItem(
                        expression,
                        item.Descending,
                        NullOrderingKind.Default,
                        item.Span))

            else
                result.Add(
                    OrderByItem(
                        expression,
                        item.Descending,
                        item.NullOrdering,
                        item.Span))

        result |> toImmutableArray

    and private rewriteExpression
        (expression: SqlExpr)
        targetProvider
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
                rewriteExpression
                    unary.Operand
                    targetProvider)
            :> SqlExpr

        | :? BinaryExpr as binary ->
            CoreBindingAstClone.Binary(
                binary,
                rewriteExpression
                    binary.Left
                    targetProvider,
                rewriteExpression
                    binary.Right
                    targetProvider)
            :> SqlExpr

        | :? FunctionCallExpr as functionCall ->
            let arguments =
                functionCall.Arguments
                |> Seq.map (fun argument ->
                    rewriteExpression
                        argument
                        targetProvider)
                |> toImmutableArray

            let aggregateOrderBy =
                rewriteOrderBy
                    functionCall.AggregateOrderBy
                    targetProvider
                    true
                    None

            CoreBindingAstClone.Function(
                functionCall,
                arguments,
                aggregateOrderBy)
            :> SqlExpr

        | :? FilterExpr as filter ->
            CoreBindingAstClone.Filter(
                filter,
                rewriteExpression
                    filter.Expression
                    targetProvider,
                rewriteExpression
                    filter.Predicate
                    targetProvider)
            :> SqlExpr

        | :? WindowedExpr as windowed ->
            let partitionBy =
                windowed.Window.PartitionBy
                |> Seq.map (fun partition ->
                    rewriteExpression
                        partition
                        targetProvider)
                |> toImmutableArray

            let orderBy =
                rewriteOrderBy
                    windowed.Window.OrderBy
                    targetProvider
                    true
                    None

            let window =
                CoreBindingAstClone.Window(
                    windowed.Window,
                    partitionBy,
                    orderBy)

            CoreBindingAstClone.Windowed(
                windowed,
                rewriteExpression
                    windowed.Expression
                    targetProvider,
                window)
            :> SqlExpr

        | :? CastExpr as cast ->
            CoreBindingAstClone.Cast(
                cast,
                rewriteExpression
                    cast.Expression
                    targetProvider)
            :> SqlExpr

        | :? CaseExpr as caseExpression ->
            let branches =
                caseExpression.Branches
                |> Seq.map (fun branch ->
                    CaseBranch(
                        rewriteExpression
                            branch.Condition
                            targetProvider,
                        rewriteExpression
                            branch.Value
                            targetProvider))
                |> toImmutableArray

            let elseExpression =
                match Option.ofObj caseExpression.ElseExpression with
                | Some value ->
                    Some(
                        rewriteExpression
                            value
                            targetProvider)
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
                |> Seq.map (fun item ->
                    rewriteExpression
                        item
                        targetProvider)
                |> toImmutableArray

            CoreBindingAstClone.In(
                inExpression,
                rewriteExpression
                    inExpression.Value
                    targetProvider,
                items)
            :> SqlExpr

        | :? BetweenExpr as between ->
            CoreBindingAstClone.Between(
                between,
                rewriteExpression
                    between.Value
                    targetProvider,
                rewriteExpression
                    between.Lower
                    targetProvider,
                rewriteExpression
                    between.Upper
                    targetProvider)
            :> SqlExpr

        | :? IsNullExpr as isNull ->
            CoreBindingAstClone.IsNull(
                isNull,
                rewriteExpression
                    isNull.Value
                    targetProvider)
            :> SqlExpr

        | :? SubqueryExpr as subquery ->
            CoreBindingAstClone.Subquery(
                subquery,
                rewriteStatement
                    subquery.Query
                    targetProvider)
            :> SqlExpr

        | :? ExistsExpr as exists ->
            CoreBindingAstClone.Exists(
                exists,
                rewriteStatement
                    exists.Query
                    targetProvider)
            :> SqlExpr

        | other ->
            raise (SqlCompilationException(
                $"Unsupported expression while canonicalizing NULL ordering: {other.GetType().Name}"))

    /// Rewrite explicit NULL ordering for target providers that require it.
    let rewrite
        (statement: SqlStatement)
        targetProvider
        : SqlStatement =

        if not (
            SqlNullOrderingCapabilityRules
                .RequiresTargetRewrite(
                    targetProvider)) then
            statement
        else
            rewriteStatement
                statement
                targetProvider
