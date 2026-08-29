namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open System.Globalization
open System.Text
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Core.Execution
open HsSqlAgent.SqlCore.Core.Pipeline
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

module internal FunctionalNativeQueryRenderer =

    let private emptyBindings = ImmutableArray<obj | null>.Empty

    let private appendFragments (left: NativeSqlFragment) (right: NativeSqlFragment) separator =
        if right.Sql.Length = 0 then left
        elif left.Sql.Length = 0 then right
        else NativeSqlFragment(left.Sql + separator + right.Sql, left.Bindings.AddRange(right.Bindings))

    let private rootCtes (statement: SqlStatement) =
        match statement with
        | :? SelectStatement as select -> select.Ctes
        | :? QueryStatement as query -> query.Head.Ctes
        | _ -> ImmutableArray<CteDefinition>.Empty

    let private requiresTail (statement: QueryStatement) =
        not statement.OrderBy.IsDefaultOrEmpty
        || statement.Limit.HasValue
        || (statement.Offset.HasValue && statement.Offset.Value > 0)

    let private hasPositiveOffset (offset: Nullable<int>) =
        offset.HasValue && offset.Value > 0

    let private withoutTail (statement: QueryStatement) =
        QueryStatement(
            statement.Head,
            statement.SetOperations,
            ImmutableArray<OrderByItem>.Empty,
            Nullable<int>(),
            Nullable<int>(),
            statement.Span)

    let private selectWithoutTail (statement: SelectStatement) projection =
        SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            statement.Distinct,
            projection,
            statement.From,
            statement.Joins,
            statement.Where,
            statement.GroupBy,
            statement.Having,
            ImmutableArray<OrderByItem>.Empty,
            Nullable<int>(),
            Nullable<int>(),
            statement.Span)

    let private setOperationKeyword kind =
        match kind with
        | SetOperationKind.Union -> "UNION"
        | SetOperationKind.UnionAll -> "UNION ALL"
        | SetOperationKind.Intersect -> "INTERSECT"
        | SetOperationKind.Except -> "EXCEPT"
        | other -> raise (SqlCompilationException($"Unsupported set operation '{other}'."))

    let private renderSelectHead
        (renderer: NativeSqlRenderer)
        (limit: Nullable<int>)
        (offset: Nullable<int>)
        distinct =

        let provider = renderer.Provider
        let positiveOffset = hasPositiveOffset offset
        let sql = StringBuilder("SELECT ")
        let mutable bindings = emptyBindings

        if provider = SqlAgentToolType.MsSqlServer
           && limit.HasValue
           && limit.Value >= 0
           && (not positiveOffset || limit.Value = 0) then
            if distinct then sql.Append("DISTINCT ") |> ignore
            sql.Append("TOP (").Append(NativeSqlParameterizer.Placeholder).Append(") ") |> ignore
            bindings <- bindings.Add(box limit.Value)
            NativeSqlFragment(sql.ToString(), bindings)
        elif provider = SqlAgentToolType.Firebird then
            if limit.HasValue
               && limit.Value >= 0
               && (not positiveOffset || limit.Value = 0) then
                sql.Append("FIRST ").Append(NativeSqlParameterizer.Placeholder).Append(' ') |> ignore
                bindings <- bindings.Add(box limit.Value)

            if positiveOffset
               && (not limit.HasValue || limit.Value = 0) then
                sql.Append("SKIP ").Append(NativeSqlParameterizer.Placeholder).Append(' ') |> ignore
                bindings <- bindings.Add(box offset.Value)

            if distinct then sql.Append("DISTINCT ") |> ignore
            NativeSqlFragment(sql.ToString(), bindings)
        else
            if distinct then sql.Append("DISTINCT ") |> ignore
            NativeSqlFragment(sql.ToString(), bindings)

    let private renderPagination
        (renderer: NativeSqlRenderer)
        (limit: Nullable<int>)
        (offset: Nullable<int>) =

        let positiveOffset = hasPositiveOffset offset
        if not limit.HasValue && not positiveOffset then
            NativeSqlFragment.Empty
        else
            match renderer.Provider with
            | SqlAgentToolType.Postgres ->
                let sql = StringBuilder()
                let mutable bindings = emptyBindings
                if limit.HasValue then
                    sql.Append("LIMIT ").Append(NativeSqlParameterizer.Placeholder) |> ignore
                    bindings <- bindings.Add(box limit.Value)
                if positiveOffset then
                    if sql.Length > 0 then sql.Append(' ') |> ignore
                    sql.Append("OFFSET ").Append(NativeSqlParameterizer.Placeholder) |> ignore
                    bindings <- bindings.Add(box offset.Value)
                NativeSqlFragment(sql.ToString(), bindings)
            | SqlAgentToolType.MySQL ->
                if not limit.HasValue then
                    NativeSqlFragment(
                        "LIMIT 18446744073709551615 OFFSET " + NativeSqlParameterizer.Placeholder,
                        emptyBindings.Add(box offset.Value))
                elif positiveOffset then
                    NativeSqlFragment(
                        "LIMIT " + NativeSqlParameterizer.Placeholder + " OFFSET " + NativeSqlParameterizer.Placeholder,
                        emptyBindings.Add(box limit.Value).Add(box offset.Value))
                else
                    NativeSqlFragment(
                        "LIMIT " + NativeSqlParameterizer.Placeholder,
                        emptyBindings.Add(box limit.Value))
            | SqlAgentToolType.Sqlite ->
                if not limit.HasValue then
                    NativeSqlFragment(
                        "LIMIT -1 OFFSET " + NativeSqlParameterizer.Placeholder,
                        emptyBindings.Add(box offset.Value))
                elif positiveOffset then
                    NativeSqlFragment(
                        "LIMIT " + NativeSqlParameterizer.Placeholder + " OFFSET " + NativeSqlParameterizer.Placeholder,
                        emptyBindings.Add(box limit.Value).Add(box offset.Value))
                else
                    NativeSqlFragment(
                        "LIMIT " + NativeSqlParameterizer.Placeholder,
                        emptyBindings.Add(box limit.Value))
            | SqlAgentToolType.Oracle ->
                if not limit.HasValue then
                    NativeSqlFragment(
                        "OFFSET " + NativeSqlParameterizer.Placeholder + " ROWS",
                        emptyBindings.Add(box offset.Value))
                else
                    let offsetBinding =
                        if offset.HasValue then box (int64 offset.Value)
                        else box 0L
                    NativeSqlFragment(
                        "OFFSET " + NativeSqlParameterizer.Placeholder +
                        " ROWS FETCH NEXT " + NativeSqlParameterizer.Placeholder + " ROWS ONLY",
                        emptyBindings.Add(offsetBinding).Add(box limit.Value))
            | SqlAgentToolType.Firebird ->
                if limit.HasValue && limit.Value > 0 && positiveOffset then
                    NativeSqlFragment(
                        "ROWS " + NativeSqlParameterizer.Placeholder + " TO " + NativeSqlParameterizer.Placeholder,
                        emptyBindings
                            .Add(box (int64 offset.Value + 1L))
                            .Add(box (int64 offset.Value + int64 limit.Value)))
                else
                    NativeSqlFragment.Empty
            | SqlAgentToolType.MsSqlServer -> NativeSqlFragment.Empty
            | other -> raise (SqlCompilationException($"Unsupported target provider '{other}'."))

    let private tryRenderPreservedProjectionAlias
        (renderer: NativeSqlRenderer)
        (expression: SqlExpr)
        (aliases: IdentifierPart array) =

        let identifier =
            match expression with
            | :? BoundColumnExpr as bound -> Some bound.Name
            | :? ColumnExpr as column -> Some column.Name
            | _ -> None

        match identifier with
        | Some name when name.Parts.Length = 1 && not name.Parts[0].WasQuoted ->
            let reference = name.Parts[0]
            let matches =
                aliases
                |> Array.filter (fun alias ->
                    String.Equals(alias.Value, reference.Value, StringComparison.OrdinalIgnoreCase))
                |> Array.truncate 2
            if matches.Length > 1 then
                raise (SqlCompilationException(
                    $"ORDER BY alias '{reference.Value}' is ambiguous among preserved projection aliases."))
            elif matches.Length = 1 then
                Some (NativeSqlFragment(
                    CoreIdentifierSqlRenderer.RenderAlias(matches[0], renderer.Provider),
                    emptyBindings))
            else None
        | _ -> None

    let private sharedBindingValue binding =
        match binding with
        | :? NativeSharedSqlBinding as shared -> shared.Value
        | value -> value

    let private equivalentParameterizedExpression (left: NativeSqlFragment) (right: NativeSqlFragment) =
        left.Sql = right.Sql
        && left.Bindings.Length = right.Bindings.Length
        && Seq.forall2
            (fun leftBinding rightBinding ->
                Object.Equals(sharedBindingValue leftBinding, sharedBindingValue rightBinding))
            left.Bindings
            right.Bindings

    let private shareGroupingBindings (bindings: ImmutableArray<obj | null>) keyPrefix =
        bindings
        |> Seq.mapi (fun index binding ->
            match binding with
            | :? NativeSharedSqlBinding -> binding
            | _ -> box (NativeSharedSqlBinding(keyPrefix + string index, binding)))
        |> ImmutableArray.CreateRange

    let rec private renderStatement
        (renderer: NativeSqlRenderer)
        (statement: SqlStatement)
        position =

        match statement with
        | :? SelectStatement as select -> renderSelect renderer select position true
        | :? QueryStatement as query -> renderQuery renderer query position
        | other -> raise (SqlCompilationException(
            $"Native query renderer requires SELECT/query-set AST, not {other.GetType().Name}."))

    and private renderExpression (renderer: NativeSqlRenderer) (expression: SqlExpr) =
        let renderSubquery =
            Func<SqlStatement, NativeSqlFragment>(fun statement ->
                renderStatement renderer statement FunctionalQueryPosition.ScalarSubquery)
        FunctionalNativeExpressionRenderer.render renderer.Provider renderSubquery expression

    and private renderPredicate (renderer: NativeSqlRenderer) (expression: SqlExpr) =
        let renderSubquery =
            Func<SqlStatement, NativeSqlFragment>(fun statement ->
                renderStatement renderer statement FunctionalQueryPosition.ScalarSubquery)
        FunctionalNativeExpressionRenderer.renderPredicate renderer.Provider renderSubquery expression

    and private sharePostgresGroupingBindings
        (renderer: NativeSqlRenderer)
        (statement: SelectStatement)
        (projections: ResizeArray<NativeSqlFragment>)
        (groupItems: NativeSqlFragment array) =

        for groupIndex in 0 .. groupItems.Length - 1 do
            let groupItem = groupItems[groupIndex]
            if groupItem.Bindings |> Seq.exists (fun binding -> not (binding :? NativeSharedSqlBinding)) then
                let mutable found = false
                let mutable projectionIndex = 0
                while not found && projectionIndex < statement.Select.Length do
                    let projectedExpression = renderExpression renderer statement.Select[projectionIndex].Expression
                    if equivalentParameterizedExpression groupItem projectedExpression then
                        let keyPrefix =
                            "postgres-group:" + string statement.Span.Start + ":" +
                            string statement.Span.End + ":" + string projectionIndex + ":"
                        let projection = projections[projectionIndex]
                        projections[projectionIndex] <- NativeSqlFragment(
                            projection.Sql,
                            shareGroupingBindings projection.Bindings keyPrefix)
                        groupItems[groupIndex] <- NativeSqlFragment(
                            groupItem.Sql,
                            shareGroupingBindings groupItem.Bindings keyPrefix)
                        found <- true
                    projectionIndex <- projectionIndex + 1

    and private renderSelectItem (renderer: NativeSqlRenderer) (item: SelectItem) =
        let expression = renderExpression renderer item.Expression
        match item.Alias with
        | null -> expression
        | alias -> NativeSqlFragment(
            expression.Sql + " AS " + CoreIdentifierSqlRenderer.RenderAlias(alias, renderer.Provider),
            expression.Bindings)

    and private renderNamedTableSource (renderer: NativeSqlRenderer) (source: NamedTableSource) =
        let table = CoreIdentifierSqlRenderer.Render(source.Name, renderer.Provider, allowWildcard = false)
        match source.Alias with
        | null -> NativeSqlFragment(table, emptyBindings)
        | alias ->
            let separator = if renderer.Provider = SqlAgentToolType.Oracle then " " else " AS "
            NativeSqlFragment(table + separator + CoreIdentifierSqlRenderer.RenderAlias(alias, renderer.Provider), emptyBindings)

    and private renderDerivedTableSource (renderer: NativeSqlRenderer) (source: DerivedTableSource) =
        let query = renderStatement renderer source.Query FunctionalQueryPosition.DerivedTable
        let separator = if renderer.Provider = SqlAgentToolType.Oracle then " " else " AS "
        NativeSqlFragment(
            "(" + query.Sql + ")" + separator + CoreIdentifierSqlRenderer.RenderAlias(source.Alias, renderer.Provider),
            query.Bindings)

    and private renderTableSource (renderer: NativeSqlRenderer) (source: TableSource) =
        match source with
        | :? NamedTableSource as named -> renderNamedTableSource renderer named
        | :? DerivedTableSource as derived -> renderDerivedTableSource renderer derived
        | other -> raise (SqlCompilationException(
            $"Unsupported FROM source during native lowering: {other.GetType().Name}"))

    and private renderJoin (renderer: NativeSqlRenderer) (join: JoinSource) =
        let keyword =
            match join.Kind with
            | "INNER" -> "INNER JOIN"
            | "LEFT" -> "LEFT JOIN"
            | "RIGHT" -> "RIGHT JOIN"
            | "FULL" -> "FULL OUTER JOIN"
            | "CROSS" -> "CROSS JOIN"
            | other -> raise (SqlCompilationException($"Unsupported JOIN kind '{other}'."))

        if join.Kind = "CROSS" && not (isNull join.Predicate) then
            raise (SqlCompilationException("CROSS JOIN cannot have an ON predicate."))
        if join.Kind <> "CROSS" && isNull join.Predicate then
            raise (SqlCompilationException(join.Kind + " JOIN requires an ON predicate."))

        renderer.ValidateJoinCapabilityForFunctional(join)
        let source = renderTableSource renderer join.Source
        match join.Predicate with
        | null -> NativeSqlFragment(keyword + " " + source.Sql, source.Bindings)
        | predicate ->
            let renderedPredicate = renderPredicate renderer predicate
            NativeSqlFragment(
                keyword + " " + source.Sql + " ON " + renderedPredicate.Sql,
                source.Bindings.AddRange(renderedPredicate.Bindings))

    and private renderOrderBy
        (renderer: NativeSqlRenderer)
        (orderBy: ImmutableArray<OrderByItem>)
        (projection: ImmutableArray<SelectItem>) =

        if orderBy.IsDefaultOrEmpty then NativeSqlFragment.Empty
        else
            let preservedAliases =
                projection
                |> Seq.choose (fun item ->
                    match item.Alias with
                    | null -> None
                    | alias when alias.PreserveSpelling -> Some alias
                    | _ -> None)
                |> Seq.toArray
            let parts = ResizeArray<string>()
            let mutable bindings = emptyBindings

            for item in orderBy do
                let rendered =
                    match item.Expression with
                    | :? LiteralExpr as literal ->
                        match literal.Value with
                        | :? OrderByOrdinalValue as ordinal ->
                            NativeSqlFragment(ordinal.Position.ToString(CultureInfo.InvariantCulture), emptyBindings)
                        | _ ->
                            match tryRenderPreservedProjectionAlias renderer item.Expression preservedAliases with
                            | Some alias -> alias
                            | None -> renderExpression renderer item.Expression
                    | _ ->
                        match tryRenderPreservedProjectionAlias renderer item.Expression preservedAliases with
                        | Some alias -> alias
                        | None -> renderExpression renderer item.Expression

                let nullOrdering =
                    match item.NullOrdering with
                    | NullOrderingKind.Default -> String.Empty
                    | NullOrderingKind.First -> " NULLS FIRST"
                    | NullOrderingKind.Last -> " NULLS LAST"
                    | other -> raise (SqlCompilationException($"Unsupported NULL ordering '{other}'."))
                parts.Add(rendered.Sql + (if item.Descending then " DESC" else " ASC") + nullOrdering)
                bindings <- bindings.AddRange(rendered.Bindings)

            NativeSqlFragment("ORDER BY " + String.Join(", ", parts), bindings)

    and private renderSelectBody
        (renderer: NativeSqlRenderer)
        (statement: SelectStatement)
        includeTail =

        let limitForHead = if includeTail then statement.Limit else Nullable<int>()
        let offsetForHead = if includeTail then statement.Offset else Nullable<int>()
        let head = renderSelectHead renderer limitForHead offsetForHead statement.Distinct
        let sql = StringBuilder(head.Sql)
        let mutable bindings = head.Bindings

        let projections = ResizeArray<NativeSqlFragment>()
        if statement.Select.IsDefaultOrEmpty then projections.Add(NativeSqlFragment("*", emptyBindings))
        else for item in statement.Select do projections.Add(renderSelectItem renderer item)

        let groupItems =
            if statement.GroupBy.IsDefaultOrEmpty then Array.empty<NativeSqlFragment>
            else statement.GroupBy |> Seq.map (renderExpression renderer) |> Seq.toArray

        if renderer.Provider = SqlAgentToolType.Postgres
           && groupItems.Length > 0
           && not statement.Select.IsDefaultOrEmpty
           && not (statement.Span.Equals(SourceSpan.Unknown)) then
            sharePostgresGroupingBindings renderer statement projections groupItems

        for index in 0 .. projections.Count - 1 do
            if index > 0 then sql.Append(", ") |> ignore
            sql.Append(projections[index].Sql) |> ignore
            bindings <- bindings.AddRange(projections[index].Bindings)

        match statement.From with
        | null ->
            if renderer.Provider = SqlAgentToolType.Oracle then sql.Append(" FROM DUAL") |> ignore
            elif renderer.Provider = SqlAgentToolType.Firebird then sql.Append(" FROM RDB$DATABASE") |> ignore
        | source ->
            let renderedFrom = renderTableSource renderer source
            sql.Append(" FROM ").Append(renderedFrom.Sql) |> ignore
            bindings <- bindings.AddRange(renderedFrom.Bindings)

        for join in statement.Joins do
            let renderedJoin = renderJoin renderer join
            sql.Append(' ').Append(renderedJoin.Sql) |> ignore
            bindings <- bindings.AddRange(renderedJoin.Bindings)

        match statement.Where with
        | null -> ()
        | predicate ->
            let renderedWhere = renderPredicate renderer predicate
            sql.Append(" WHERE ").Append(renderedWhere.Sql) |> ignore
            bindings <- bindings.AddRange(renderedWhere.Bindings)

        if groupItems.Length > 0 then
            sql.Append(" GROUP BY ").Append(String.Join(", ", groupItems |> Array.map (fun item -> item.Sql))) |> ignore
            for item in groupItems do bindings <- bindings.AddRange(item.Bindings)

        match statement.Having with
        | null -> ()
        | predicate ->
            let renderedHaving = renderPredicate renderer predicate
            sql.Append(" HAVING ").Append(renderedHaving.Sql) |> ignore
            bindings <- bindings.AddRange(renderedHaving.Bindings)

        if includeTail then
            let order = renderOrderBy renderer statement.OrderBy statement.Select
            if order.Sql.Length > 0 then
                sql.Append(' ').Append(order.Sql) |> ignore
                bindings <- bindings.AddRange(order.Bindings)
            let pagination = renderPagination renderer statement.Limit statement.Offset
            if pagination.Sql.Length > 0 then
                sql.Append(' ').Append(pagination.Sql) |> ignore
                bindings <- bindings.AddRange(pagination.Bindings)

        NativeSqlFragment(sql.ToString(), bindings)

    and private renderSqlServerOffsetSelect (renderer: NativeSqlRenderer) (statement: SelectStatement) =
        let plan =
            FunctionalSqlServerPagingRenderer.buildSelectPagePlan
                (renderExpression renderer)
                statement.Select
                statement.OrderBy
                statement.Distinct
        let pageSource = renderSelectBody renderer (selectWithoutTail statement plan.BaseProjection) false
        FunctionalSqlServerPagingRenderer.renderPageWrapper
            (fun orderBy -> renderOrderBy renderer orderBy ImmutableArray<SelectItem>.Empty)
            pageSource
            plan.OutputInternalAliases
            plan.ExternalAliases
            plan.WindowOrderBy
            statement.Limit
            statement.Offset.Value

    and private renderCtes (renderer: NativeSqlRenderer) (ctes: ImmutableArray<CteDefinition>) =
        if ctes.IsDefaultOrEmpty then NativeSqlFragment.Empty
        else
            let sqlParts = ResizeArray<string>()
            let mutable bindings = emptyBindings
            for cte in ctes do
                if not cte.ColumnAliases.IsDefaultOrEmpty then
                    raise (SqlCompilationException(
                        "CTE column aliases must be canonicalized to projection aliases before native lowering."))
                if cte.Name.Parts.Length <> 1 then raise (SqlCompilationException("CTE name must be unqualified."))
                let query = renderStatement renderer cte.Query FunctionalQueryPosition.CteDefinition
                let name = CoreIdentifierSqlRenderer.Render(cte.Name, renderer.Provider, allowWildcard = false)
                sqlParts.Add(name + " AS (" + query.Sql + ")")
                bindings <- bindings.AddRange(query.Bindings)
            NativeSqlFragment("WITH " + String.Join(", ", sqlParts), bindings)

    and private renderSelect
        (renderer: NativeSqlRenderer)
        (statement: SelectStatement)
        position
        includeTail =

        let ctes = renderCtes renderer statement.Ctes
        let body =
            if includeTail
               && renderer.Provider = SqlAgentToolType.MsSqlServer
               && hasPositiveOffset statement.Offset
               && (not statement.Limit.HasValue || statement.Limit.Value <> 0) then
                renderSqlServerOffsetSelect renderer statement
            else renderSelectBody renderer statement includeTail
        appendFragments ctes body " "

    and private renderSetBranch (renderer: NativeSqlRenderer) (statement: SqlStatement) =
        let branch = renderStatement renderer statement FunctionalQueryPosition.SetBranch
        if (rootCtes statement).IsDefaultOrEmpty then branch
        else
            let alias = CoreIdentifierSqlRenderer.RenderAlias(
                IdentifierPart("_set_branch", false, SourceSpan.Unknown), renderer.Provider)
            NativeSqlFragment("SELECT * FROM (" + branch.Sql + ") AS " + alias, branch.Bindings)

    and private renderSetBody
        (renderer: NativeSqlRenderer)
        (statement: QueryStatement)
        position =

        let head = renderSelect renderer statement.Head position false
        let mutable sql = head.Sql
        let mutable bindings = head.Bindings
        for operation in statement.SetOperations do
            let branch = renderSetBranch renderer operation.Query
            sql <- sql + " " + setOperationKeyword operation.Kind + " " + branch.Sql
            bindings <- bindings.AddRange(branch.Bindings)
        NativeSqlFragment(sql, bindings)

    and private renderQuery
        (renderer: NativeSqlRenderer)
        (statement: QueryStatement)
        position =

        if not (requiresTail statement) then renderSetBody renderer statement position
        elif position = FunctionalQueryPosition.ScalarSubquery
             && CoreNativeSetTailScope.CanRenderDirectTail(statement) then
            let body = renderSetBody renderer (withoutTail statement) position
            let order = renderOrderBy renderer statement.OrderBy statement.Head.Select
            let pagination = renderPagination renderer statement.Limit statement.Offset
            let tail = appendFragments order pagination " "
            appendFragments body tail " "
        else
            let inner = renderSetBody renderer (withoutTail statement) position
            if renderer.Provider = SqlAgentToolType.MsSqlServer
               && hasPositiveOffset statement.Offset
               && (not statement.Limit.HasValue || statement.Limit.Value <> 0) then
                FunctionalSqlServerPagingRenderer.renderSetOffsetWrapper
                    (fun orderBy -> renderOrderBy renderer orderBy ImmutableArray<SelectItem>.Empty)
                    inner
                    statement.OrderBy
                    statement.Limit
                    statement.Offset.Value
                    statement.Head.Select
            else
                let head = renderSelectHead renderer statement.Limit statement.Offset false
                let alias = CoreIdentifierSqlRenderer.RenderAlias(
                    IdentifierPart("_set", false, SourceSpan.Unknown), renderer.Provider)
                let asKeyword = if renderer.Provider = SqlAgentToolType.Oracle then " " else " AS "
                let sql = StringBuilder(head.Sql)
                sql.Append("* FROM (").Append(inner.Sql).Append(')') |> ignore
                sql.Append(asKeyword).Append(alias) |> ignore
                let mutable bindings = head.Bindings.AddRange(inner.Bindings)
                let order = renderOrderBy renderer statement.OrderBy statement.Head.Select
                if order.Sql.Length > 0 then
                    sql.Append(' ').Append(order.Sql) |> ignore
                    bindings <- bindings.AddRange(order.Bindings)
                let pagination = renderPagination renderer statement.Limit statement.Offset
                if pagination.Sql.Length > 0 then
                    sql.Append(' ').Append(pagination.Sql) |> ignore
                    bindings <- bindings.AddRange(pagination.Bindings)
                NativeSqlFragment(sql.ToString(), bindings)

    let lower
        (plan: ExecutableSqlPlan)
        (provider: SqlAgentToolType)
        (targetProfile: SqlProviderCapabilityProfile | null) =

        ArgumentNullException.ThrowIfNull(plan)
        if plan.TargetProvider <> provider then
            raise (SqlCompilationException(
                $"Plan targets {plan.TargetProvider}, but this native renderer targets {provider}."))

        let renderer = NativeSqlRenderer(provider, targetProfile)
        let fragment = renderStatement renderer plan.Statement FunctionalQueryPosition.Root
        let struct (finalizedSql, finalizedParameters) = NativeSqlParameterizer.Finalize(fragment, provider)
        let command = CompiledSqlCommand(
            finalizedSql, finalizedParameters, SqlStatementKind.Select, String.Empty, provider)
        let fingerprint = DmlFingerprintService.ComputePlanFingerprint(command, plan.PolicyVersion)
        CompiledSqlCommand(
            command.Sql, command.Parameters, command.Kind, fingerprint, command.TargetProvider)
