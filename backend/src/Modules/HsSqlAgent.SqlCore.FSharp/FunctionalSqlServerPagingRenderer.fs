namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open System.Text
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums

/// F# ownership boundary for SQL Server ROW_NUMBER-based OFFSET compatibility lowering.
module internal FunctionalSqlServerPagingRenderer =

    type SelectPagePlan =
        { BaseProjection: ImmutableArray<SelectItem>
          OutputInternalAliases: ImmutableArray<IdentifierPart>
          ExternalAliases: ImmutableArray<IdentifierPart>
          WindowOrderBy: ImmutableArray<OrderByItem> }

    let private internalPageAlias index =
        IdentifierPart("_core_page_" + string index, false, SourceSpan.Unknown)

    let private expressionIdentifier (expression: SqlExpr) =
        match expression with
        | :? ColumnExpr as column -> Some column.Name
        | :? BoundColumnExpr as column -> Some column.Name
        | _ -> None

    let private projectionOutputNames
        (projection: ImmutableArray<SelectItem>)
        context =

        if projection.IsDefaultOrEmpty then
            raise (SqlCompilationException(
                context + " cannot preserve an implicit wildcard projection through the legacy ROW_NUMBER wrapper."))

        let result = ImmutableArray.CreateBuilder<IdentifierPart>(projection.Length)
        for item in projection do
            match item.Alias with
            | null ->
                match expressionIdentifier item.Expression with
                | Some identifier
                    when not identifier.Parts.IsDefaultOrEmpty
                         && not (identifier.Parts[identifier.Parts.Length - 1].Value = "*"
                                 && not identifier.Parts[identifier.Parts.Length - 1].WasQuoted) ->
                    result.Add(identifier.Parts[identifier.Parts.Length - 1])
                | _ ->
                    raise (SqlCompilationException(
                        context + " requires every projected output to have a stable name; " +
                        "use explicit aliases for wildcard or computed expressions."))
            | alias -> result.Add(alias)
        result.ToImmutable()

    let private ensureUniqueOutputNames
        (names: ImmutableArray<IdentifierPart>)
        context =

        let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for name in names do
            if not (seen.Add(name.Value)) then
                raise (SqlCompilationException(
                    context + " requires unique set-result output names before the legacy ROW_NUMBER wrapper."))

    let private equivalentFragment (left: NativeSqlFragment) (right: NativeSqlFragment) =
        left.Sql = right.Sql
        && left.Bindings.Length = right.Bindings.Length
        && Seq.forall2 (fun leftBinding rightBinding -> Object.Equals(leftBinding, rightBinding)) left.Bindings right.Bindings

    let private tryResolveProjectionOrderIndex
        (renderExpression: SqlExpr -> NativeSqlFragment)
        (expression: SqlExpr)
        (projection: ImmutableArray<SelectItem>) =

        match expression with
        | :? LiteralExpr as literal ->
            match literal.Value with
            | :? OrderByOrdinalValue as ordinal when ordinal.Position > 0 && ordinal.Position <= projection.Length ->
                ordinal.Position - 1
            | :? OrderByOrdinalValue -> -1
            | _ ->
                match expressionIdentifier expression with
                | Some identifier when identifier.Parts.Length = 1 ->
                    let reference = identifier.Parts[0].Value
                    let matches =
                        projection
                        |> Seq.mapi (fun index item -> index, item.Alias)
                        |> Seq.filter (fun (_, alias) ->
                            not (isNull alias)
                            && String.Equals(alias.Value, reference, StringComparison.OrdinalIgnoreCase))
                        |> Seq.truncate 2
                        |> Seq.toArray
                    if matches.Length > 1 then
                        raise (SqlCompilationException(
                            "SQL Server OFFSET pagination ORDER BY alias '" + reference + "' is ambiguous."))
                    elif matches.Length = 1 then
                        fst matches[0]
                    else
                        let ordered = renderExpression expression
                        projection
                        |> Seq.tryFindIndex (fun item -> equivalentFragment (renderExpression item.Expression) ordered)
                        |> Option.defaultValue -1
                | _ ->
                    let ordered = renderExpression expression
                    projection
                    |> Seq.tryFindIndex (fun item -> equivalentFragment (renderExpression item.Expression) ordered)
                    |> Option.defaultValue -1
        | _ ->
            match expressionIdentifier expression with
            | Some identifier when identifier.Parts.Length = 1 ->
                let reference = identifier.Parts[0].Value
                let matches =
                    projection
                    |> Seq.mapi (fun index item -> index, item.Alias)
                    |> Seq.filter (fun (_, alias) ->
                        not (isNull alias)
                        && String.Equals(alias.Value, reference, StringComparison.OrdinalIgnoreCase))
                    |> Seq.truncate 2
                    |> Seq.toArray
                if matches.Length > 1 then
                    raise (SqlCompilationException(
                        "SQL Server OFFSET pagination ORDER BY alias '" + reference + "' is ambiguous."))
                elif matches.Length = 1 then fst matches[0]
                else
                    let ordered = renderExpression expression
                    projection
                    |> Seq.tryFindIndex (fun item -> equivalentFragment (renderExpression item.Expression) ordered)
                    |> Option.defaultValue -1
            | _ ->
                let ordered = renderExpression expression
                projection
                |> Seq.tryFindIndex (fun item -> equivalentFragment (renderExpression item.Expression) ordered)
                |> Option.defaultValue -1

    let buildSelectPagePlan
        (renderExpression: SqlExpr -> NativeSqlFragment)
        (projection: ImmutableArray<SelectItem>)
        (orderBy: ImmutableArray<OrderByItem>)
        distinct =

        let externalAliases = projectionOutputNames projection "SQL Server OFFSET pagination"
        let outputInternalAliases =
            externalAliases
            |> Seq.mapi (fun index _ -> internalPageAlias index)
            |> ImmutableArray.CreateRange

        let baseProjection = ImmutableArray.CreateBuilder<SelectItem>(projection.Length + orderBy.Length)
        for index in 0 .. projection.Length - 1 do
            baseProjection.Add(
                SelectItem(projection[index].Expression, outputInternalAliases[index], projection[index].Span))

        let windowOrder = ImmutableArray.CreateBuilder<OrderByItem>(orderBy.Length)
        for index in 0 .. orderBy.Length - 1 do
            let item = orderBy[index]
            if item.NullOrdering <> NullOrderingKind.Default then
                raise (SqlCompilationException(
                    "SQL Server OFFSET pagination requires NULL ordering to be canonicalized before native lowering."))

            let projectionIndex = tryResolveProjectionOrderIndex renderExpression item.Expression projection
            let orderAlias =
                if projectionIndex >= 0 then
                    outputInternalAliases[projectionIndex]
                else
                    if distinct then
                        raise (SqlCompilationException(
                            "SQL Server DISTINCT OFFSET pagination requires every ORDER BY expression to resolve to a projected output."))
                    let alias = IdentifierPart("_core_page_order_" + string index, false, SourceSpan.Unknown)
                    baseProjection.Add(SelectItem(item.Expression, alias, item.Span))
                    alias

            windowOrder.Add(
                OrderByItem(
                    ColumnExpr(SqlIdentifier.Unquoted(orderAlias.Value, item.Span), item.Span),
                    item.Descending,
                    NullOrderingKind.Default,
                    item.Span))

        { BaseProjection = baseProjection.ToImmutable()
          OutputInternalAliases = outputInternalAliases
          ExternalAliases = externalAliases
          WindowOrderBy = windowOrder.ToImmutable() }

    let private rewriteSetPageOrderBy
        (orderBy: ImmutableArray<OrderByItem>)
        (externalAliases: ImmutableArray<IdentifierPart>)
        (internalAliases: ImmutableArray<IdentifierPart>) =

        if orderBy.IsDefaultOrEmpty then ImmutableArray<OrderByItem>.Empty
        else
            let result = ImmutableArray.CreateBuilder<OrderByItem>(orderBy.Length)
            for item in orderBy do
                if item.NullOrdering <> NullOrderingKind.Default then
                    raise (SqlCompilationException(
                        "SQL Server set-operation OFFSET pagination requires NULL ordering to be canonicalized before native lowering."))

                let index =
                    match item.Expression with
                    | :? LiteralExpr as literal ->
                        match literal.Value with
                        | :? OrderByOrdinalValue as ordinal -> ordinal.Position - 1
                        | _ -> -1
                    | _ ->
                        match expressionIdentifier item.Expression with
                        | Some identifier when identifier.Parts.Length = 1 ->
                            let reference = identifier.Parts[0].Value
                            let matches =
                                externalAliases
                                |> Seq.mapi (fun aliasIndex alias -> aliasIndex, alias)
                                |> Seq.filter (fun (_, alias) ->
                                    String.Equals(alias.Value, reference, StringComparison.OrdinalIgnoreCase))
                                |> Seq.truncate 2
                                |> Seq.toArray
                            if matches.Length <> 1 then
                                raise (SqlCompilationException(
                                    "SQL Server set-operation OFFSET pagination ORDER BY reference '" +
                                    reference + "' is not a unique combined output name."))
                            fst matches[0]
                        | _ ->
                            raise (SqlCompilationException(
                                "SQL Server set-operation OFFSET pagination supports ORDER BY output names or ordinals only."))

                if index < 0 || index >= internalAliases.Length then
                    raise (SqlCompilationException(
                        "SQL Server set-operation OFFSET pagination ORDER BY position is outside the projected output width."))

                result.Add(
                    OrderByItem(
                        ColumnExpr(SqlIdentifier.Unquoted(internalAliases[index].Value, item.Span), item.Span),
                        item.Descending,
                        NullOrderingKind.Default,
                        item.Span))
            result.ToImmutable()

    let renderPageWrapper
        (renderOrderBy: ImmutableArray<OrderByItem> -> NativeSqlFragment)
        (pageSource: NativeSqlFragment)
        (outputInternalAliases: ImmutableArray<IdentifierPart>)
        (externalAliases: ImmutableArray<IdentifierPart>)
        (windowOrderBy: ImmutableArray<OrderByItem>)
        (limit: Nullable<int>)
        offset =

        let renderAlias name =
            CoreIdentifierSqlRenderer.RenderAlias(IdentifierPart(name, false, SourceSpan.Unknown), SqlAgentToolType.MsSqlServer)
        let baseAlias = renderAlias "_core_page_base"
        let wrapperAlias = renderAlias "results_wrapper"
        let rowAlias = renderAlias "_core_page_row"
        let order =
            if windowOrderBy.IsDefaultOrEmpty then NativeSqlFragment("ORDER BY (SELECT 0)", ImmutableArray<obj | null>.Empty)
            else renderOrderBy windowOrderBy

        let middleOutputs =
            outputInternalAliases
            |> Seq.map (fun alias -> baseAlias + "." + CoreIdentifierSqlRenderer.RenderAlias(alias, SqlAgentToolType.MsSqlServer))
            |> Seq.toArray
        let middleSql =
            "SELECT " + String.Join(", ", middleOutputs) +
            ", ROW_NUMBER() OVER (" + order.Sql + ") AS " + rowAlias +
            " FROM (" + pageSource.Sql + ") AS " + baseAlias

        let outerOutputs =
            Array.init outputInternalAliases.Length (fun index ->
                wrapperAlias + "." + CoreIdentifierSqlRenderer.RenderAlias(outputInternalAliases[index], SqlAgentToolType.MsSqlServer) +
                " AS " + CoreIdentifierSqlRenderer.RenderAlias(externalAliases[index], SqlAgentToolType.MsSqlServer))

        let sql =
            StringBuilder()
                .Append("SELECT ").Append(String.Join(", ", outerOutputs))
                .Append(" FROM (").Append(middleSql).Append(") AS ").Append(wrapperAlias)
                .Append(" WHERE ").Append(wrapperAlias).Append('.').Append(rowAlias).Append(' ')
        let mutable bindings = pageSource.Bindings.AddRange(order.Bindings)
        if not limit.HasValue then
            sql.Append(">= ").Append(NativeSqlParameterizer.Placeholder) |> ignore
            bindings <- bindings.Add(box (int64 offset + 1L))
        else
            sql.Append("BETWEEN ").Append(NativeSqlParameterizer.Placeholder)
                .Append(" AND ").Append(NativeSqlParameterizer.Placeholder) |> ignore
            bindings <- bindings.Add(box (int64 offset + 1L)).Add(box (int64 offset + int64 limit.Value))
        sql.Append(" ORDER BY ").Append(wrapperAlias).Append('.').Append(rowAlias).Append(" ASC") |> ignore
        NativeSqlFragment(sql.ToString(), bindings)

    let renderSetOffsetWrapper
        (renderOrderBy: ImmutableArray<OrderByItem> -> NativeSqlFragment)
        (inner: NativeSqlFragment)
        (orderBy: ImmutableArray<OrderByItem>)
        (limit: Nullable<int>)
        offset
        (projection: ImmutableArray<SelectItem>) =

        let externalAliases = projectionOutputNames projection "SQL Server set-operation OFFSET pagination"
        ensureUniqueOutputNames externalAliases "SQL Server set-operation OFFSET pagination"
        let internalAliases =
            externalAliases |> Seq.mapi (fun index _ -> internalPageAlias index) |> ImmutableArray.CreateRange
        let setAlias = CoreIdentifierSqlRenderer.RenderAlias(IdentifierPart("_set", false, SourceSpan.Unknown), SqlAgentToolType.MsSqlServer)
        let selectParts =
            Array.init externalAliases.Length (fun index ->
                setAlias + "." + CoreIdentifierSqlRenderer.RenderAlias(externalAliases[index], SqlAgentToolType.MsSqlServer) +
                " AS " + CoreIdentifierSqlRenderer.RenderAlias(internalAliases[index], SqlAgentToolType.MsSqlServer))
        let pageSource = NativeSqlFragment(
            "SELECT " + String.Join(", ", selectParts) + " FROM (" + inner.Sql + ") AS " + setAlias,
            inner.Bindings)
        let windowOrder = rewriteSetPageOrderBy orderBy externalAliases internalAliases
        renderPageWrapper renderOrderBy pageSource internalAliases externalAliases windowOrder limit offset
