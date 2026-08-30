namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteRenderer =

    type Provider = PostgreSql | MySql | SqlServer | SQLite | Oracle | Firebird
    type RenderedCommand = { Sql: string; Parameters: (obj | null) list; ReturnsRows: bool }

    type private RenderContext(provider: Provider) =
        let parameters = ResizeArray<obj | null>()
        member _.Provider = provider
        member _.Bind(value: obj | null) =
            let index = parameters.Count
            parameters.Add(value)
            match provider with Oracle -> ":p" + string index | _ -> "@p" + string index
        member _.Parameters = parameters |> Seq.toList

    let private quotePart provider (part: IdentifierPart) =
        let raw =
            if part.WasQuoted then part.Value
            else match provider with Oracle | Firebird -> part.Value.ToUpperInvariant() | _ -> part.Value
        match provider with
        | MySql -> "`" + raw.Replace("`", "``") + "`"
        | SqlServer -> "[" + raw.Replace("]", "]]" ) + "]"
        | _ -> "\"" + raw.Replace("\"", "\"\"") + "\""

    let private renderIdentifier provider identifier = identifier |> Identifier.parts |> List.map (quotePart provider) |> String.concat "."
    let private renderAlias provider alias = quotePart provider alias
    let private tableAliasPrefix = function Oracle -> " " | _ -> " AS "
    let private providerName = function
        | PostgreSql -> "Postgres"
        | MySql -> "MySQL"
        | SqlServer -> "MsSqlServer"
        | SQLite -> "Sqlite"
        | Oracle -> "Oracle"
        | Firebird -> "Firebird"
    let private capabilityError provider capability =
        "SQL capability '" + capability + "' is not supported by provider " + providerName provider + " for this Core plan."

    let private scalarObject value : obj | null =
        match value with
        | ScalarValue.Null -> null
        | ScalarValue.Boolean value -> box value
        | ScalarValue.Integer value when value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue -> box (int value)
        | ScalarValue.Integer value -> box value
        | ScalarValue.Decimal value -> box value
        | ScalarValue.Floating value -> box value
        | ScalarValue.Text value -> box value
        | ScalarValue.Date value -> box (value.ToDateTime(TimeOnly.MinValue))
        | ScalarValue.Time value -> box (value.ToTimeSpan())
        | ScalarValue.LocalDateTime value -> box (DateTime.SpecifyKind(value, DateTimeKind.Unspecified))
        | ScalarValue.OffsetDateTime value -> box value
        | ScalarValue.Duration value -> box value
        | ScalarValue.Bytes value -> box value

    let private renderLiteral (ctx: RenderContext) value =
        let placeholder = ctx.Bind(scalarObject value)
        match ctx.Provider, value with
        | Firebird, ScalarValue.Text text ->
            if text.Length > 8191 then invalidOp "Firebird string literal exceeds the safe UTF8 VARCHAR limit of 8191 characters."
            "CAST(" + placeholder + " AS VARCHAR(" + string (max 1 text.Length) + "))"
        | Firebird, ScalarValue.Boolean _ -> "CAST(" + placeholder + " AS BOOLEAN)"
        | Firebird, ScalarValue.Integer value when value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue -> "CAST(" + placeholder + " AS INTEGER)"
        | Firebird, ScalarValue.Integer _ -> "CAST(" + placeholder + " AS BIGINT)"
        | Firebird, ScalarValue.Decimal _ -> "CAST(" + placeholder + " AS DECIMAL(38,18))"
        | Firebird, ScalarValue.Floating _ -> "CAST(" + placeholder + " AS DOUBLE PRECISION)"
        | Firebird, ScalarValue.Date _ -> "CAST(" + placeholder + " AS DATE)"
        | Firebird, ScalarValue.Time _ -> "CAST(" + placeholder + " AS TIME)"
        | Firebird, ScalarValue.LocalDateTime _ -> "CAST(" + placeholder + " AS TIMESTAMP)"
        | Firebird, ScalarValue.OffsetDateTime _ -> "CAST(" + placeholder + " AS TIMESTAMP WITH TIME ZONE)"
        | _ -> placeholder

    let private renderLikeEscape provider escape =
        let value = LikeEscape.value escape
        let escaped = (string value).Replace("'", "''", StringComparison.Ordinal)
        match value, provider with
        | '\\', PostgreSql -> "E'\\\\'"
        | '\\', MySql -> "CHAR(92)"
        | _ -> "'" + escaped + "'"

    let private binaryText = function
        | BinaryOperator.Add -> "+" | BinaryOperator.Subtract -> "-" | BinaryOperator.Multiply -> "*"
        | BinaryOperator.Divide -> "/" | BinaryOperator.Modulo -> "%" | BinaryOperator.Concat -> "||"
        | BinaryOperator.Equal -> "=" | BinaryOperator.NotEqual -> "<>" | BinaryOperator.GreaterThan -> ">"
        | BinaryOperator.LessThan -> "<" | BinaryOperator.GreaterThanOrEqual -> ">=" | BinaryOperator.LessThanOrEqual -> "<="
        | BinaryOperator.And -> "AND" | BinaryOperator.Or -> "OR"

    let private joinText = function
        | JoinKind.Inner -> "INNER JOIN" | JoinKind.Left -> "LEFT JOIN" | JoinKind.Right -> "RIGHT JOIN"
        | JoinKind.Full -> "FULL OUTER JOIN" | JoinKind.Cross -> "CROSS JOIN"

    let private setText = function
        | SetOperator.Union -> "UNION" | SetOperator.UnionAll -> "UNION ALL"
        | SetOperator.Intersect -> "INTERSECT" | SetOperator.Except -> "EXCEPT"

    let private frameBoundText = function
        | WindowFrameBound.UnboundedPreceding -> "UNBOUNDED PRECEDING"
        | WindowFrameBound.Preceding value -> string (FrameOffset.value value) + " PRECEDING"
        | WindowFrameBound.CurrentRow -> "CURRENT ROW"
        | WindowFrameBound.Following value -> string (FrameOffset.value value) + " FOLLOWING"
        | WindowFrameBound.UnboundedFollowing -> "UNBOUNDED FOLLOWING"

    let private projectionOutputName (item: SelectItem) =
        match item.Alias, item.Expression with
        | Some alias, _ -> alias
        | None, Column identifier -> Identifier.parts identifier |> List.last
        | _ -> invalidOp "Pagination requires every projected output to have a stable name; use explicit aliases for computed expressions."

    let private isSetQuery (query: Query) = not query.SetOperations.IsEmpty
    let private hasTail (query: Query) = not query.OrderBy.IsEmpty || query.Limit.IsSome || query.Offset.IsSome

    let rec private renderExpr (ctx: RenderContext) expression =
        match expression with
        | Expr.Column identifier -> renderIdentifier ctx.Provider identifier
        | Expr.Wildcard None -> "*"
        | Expr.Wildcard(Some identifier) -> renderIdentifier ctx.Provider identifier + ".*"
        | Expr.OrderOrdinal ordinal -> string (PositiveRowCount.value ordinal)
        | Expr.Literal value -> renderLiteral ctx value
        | Expr.Interval literal ->
            match ctx.Provider with
            | PostgreSql -> "CAST(" + ctx.Bind(box (IntervalLiteral.value literal)) + " AS interval)"
            | _ -> invalidOp "INTERVAL lowering is not available for this provider."
        | Expr.Unary(UnaryOperator.Not, operand) -> "NOT (" + renderExpr ctx operand + ")"
        | Expr.Unary(UnaryOperator.Negate, operand) -> "(-" + renderExpr ctx operand + ")"
        | Expr.Unary(UnaryOperator.Positive, operand) -> "(+" + renderExpr ctx operand + ")"
        | Expr.Binary(operator, left, right) ->
            let leftSql = renderExpr ctx left
            let rightSql = renderExpr ctx right
            match operator, ctx.Provider with
            | BinaryOperator.Modulo, Oracle -> "MOD(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, MySql -> "CONCAT(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer -> "(" + leftSql + " + " + rightSql + ")"
            | _ -> "(" + leftSql + " " + binaryText operator + " " + rightSql + ")"
        | Expr.Like(value, pattern, escape, negated, caseInsensitive) ->
            if caseInsensitive && ctx.Provider <> PostgreSql then invalidOp (capabilityError ctx.Provider "operator.ilike")
            let positive =
                "(" + renderExpr ctx value + " " + (if caseInsensitive then "ILIKE" else "LIKE") + " " + renderExpr ctx pattern
                + (escape |> Option.map (fun value -> " ESCAPE " + renderLikeEscape ctx.Provider value) |> Option.defaultValue "") + ")"
            if negated then "NOT (" + positive + ")" else positive
        | Expr.FunctionCall call ->
            let name = FunctionName.value call.Name
            let args = call.Arguments |> List.map (renderExpr ctx) |> String.concat ", "
            name + "(" + (if call.IsDistinct then "DISTINCT " else "") + args + ")"
        | Expr.FilteredAggregate(value, predicate) ->
            match ctx.Provider with
            | PostgreSql | SQLite | Firebird -> renderExpr ctx value + " FILTER (WHERE " + renderExpr ctx predicate + ")"
            | _ -> invalidOp "Aggregate FILTER requires provider-specific lowering before rendering."
        | Expr.Windowed(value, window) -> renderExpr ctx value + " OVER (" + renderWindow ctx window + ")"
        | Expr.Cast(value, targetType) -> "CAST(" + renderExpr ctx value + " AS " + CastType.value targetType + ")"
        | Expr.Extract(field, value) -> "EXTRACT(" + ExtractField.value field + " FROM " + renderExpr ctx value + ")"
        | Expr.SimpleCase(input, branches, fallback) ->
            let cases = branches |> NonEmpty.toList |> List.map (fun branch -> " WHEN " + renderExpr ctx branch.Match + " THEN " + renderExpr ctx branch.Result) |> String.concat ""
            "CASE " + renderExpr ctx input + cases + (fallback |> Option.map (fun value -> " ELSE " + renderExpr ctx value) |> Option.defaultValue "") + " END"
        | Expr.SearchedCase(branches, fallback) ->
            let cases = branches |> NonEmpty.toList |> List.map (fun branch -> " WHEN " + renderExpr ctx branch.Condition + " THEN " + renderExpr ctx branch.Result) |> String.concat ""
            "CASE" + cases + (fallback |> Option.map (fun value -> " ELSE " + renderExpr ctx value) |> Option.defaultValue "") + " END"
        | Expr.InList(value, items, negated) ->
            "(" + renderExpr ctx value + (if negated then " NOT IN " else " IN ") + "(" + (items |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", ") + "))"
        | Expr.InSubquery(value, query, negated) -> "(" + renderExpr ctx value + (if negated then " NOT IN (" else " IN (") + renderQuery ctx query + "))"
        | Expr.Between(value, lower, upper, negated) -> "(" + renderExpr ctx value + (if negated then " NOT BETWEEN " else " BETWEEN ") + renderExpr ctx lower + " AND " + renderExpr ctx upper + ")"
        | Expr.IsNull(value, negated) -> "(" + renderExpr ctx value + (if negated then " IS NOT NULL)" else " IS NULL)")
        | Expr.ScalarSubquery query -> "(" + renderQuery ctx query + ")"
        | Expr.Exists(query, negated) -> (if negated then "NOT EXISTS (" else "EXISTS (") + renderQuery ctx query + ")"

    and private renderWindow (ctx: RenderContext) window =
        let parts = ResizeArray<string>()
        if not window.PartitionBy.IsEmpty then parts.Add("PARTITION BY " + (window.PartitionBy |> List.map (renderExpr ctx) |> String.concat ", "))
        if not window.OrderBy.IsEmpty then parts.Add("ORDER BY " + (window.OrderBy |> List.map (renderOrderBy ctx false) |> List.collect id |> String.concat ", "))
        window.Frame |> Option.iter (fun frame ->
            let unitText = match frame.Unit with WindowFrameUnit.Rows -> "ROWS" | WindowFrameUnit.Range -> "RANGE"
            let frameSql = match frame.Extent with SingleBound start -> unitText + " " + frameBoundText start | BetweenBounds(start, finish) -> unitText + " BETWEEN " + frameBoundText start + " AND " + frameBoundText finish
            parts.Add(frameSql))
        String.concat " " parts

    and private renderOrderBy (ctx: RenderContext) setTail (order: OrderBy) : string list =
        let expression = renderExpr ctx order.Expression
        let direction = if order.Descending then " DESC" else " ASC"
        match order.NullOrdering, ctx.Provider with
        | NullOrdering.Default, _ -> [ expression + direction ]
        | NullOrdering.NullsFirst, (PostgreSql | SQLite | Oracle | Firebird) -> [ expression + direction + " NULLS FIRST" ]
        | NullOrdering.NullsLast, (PostgreSql | SQLite | Oracle | Firebird) -> [ expression + direction + " NULLS LAST" ]
        | explicitNulls, (MySql | SqlServer) ->
            if setTail then invalidOp (capabilityError ctx.Provider "ordering.nulls")
            let targetDefault = (not order.Descending && explicitNulls = NullOrdering.NullsFirst) || (order.Descending && explicitNulls = NullOrdering.NullsLast)
            if targetDefault then [ expression + direction ]
            else
                match order.Expression with
                | Column _ ->
                    let nullRank, nonNullRank = if explicitNulls = NullOrdering.NullsLast then 1, 0 else 0, 1
                    let nullRankSql = ctx.Bind(box nullRank)
                    let nonNullRankSql = ctx.Bind(box nonNullRank)
                    [ "CASE WHEN (" + expression + " IS NULL) THEN " + nullRankSql + " ELSE " + nonNullRankSql + " END ASC"; expression + direction ]
                | _ -> invalidOp (capabilityError ctx.Provider "ordering.nulls")

    and private renderCtes (ctx: RenderContext) ctes =
        if List.isEmpty ctes then ""
        else
            "WITH " +
            (ctes
             |> List.map (fun cte -> renderAlias ctx.Provider cte.Name + " AS (" + renderQuery ctx cte.Query + ")")
             |> String.concat ", ") + " "

    and private renderSource (ctx: RenderContext) source =
        match source with
        | TableSource.NamedTable(identifier, alias) | TableSource.CteTable(identifier, alias) ->
            renderIdentifier ctx.Provider identifier + (alias |> Option.map (fun value -> tableAliasPrefix ctx.Provider + renderAlias ctx.Provider value) |> Option.defaultValue "")
        | TableSource.DerivedTable(query, alias) -> "(" + renderQuery ctx query + ")" + tableAliasPrefix ctx.Provider + renderAlias ctx.Provider alias

    and private renderSelectBody (ctx: RenderContext) (select: Select) =
        let projection = select.Projection |> List.map (fun item -> renderExpr ctx item.Expression + (item.Alias |> Option.map (fun alias -> " AS " + renderAlias ctx.Provider alias) |> Option.defaultValue "")) |> String.concat ", "
        let mutable sql = "SELECT " + (if select.Distinct then "DISTINCT " else "") + projection
        select.From |> Option.iter (fun source -> sql <- sql + " FROM " + renderSource ctx source)
        if select.From.IsNone then
            match ctx.Provider with Oracle -> sql <- sql + " FROM DUAL" | Firebird -> sql <- sql + " FROM RDB$DATABASE" | _ -> ()
        for join in select.Joins do
            sql <- sql + " " + joinText join.Kind + " " + renderSource ctx join.Source
            join.Predicate |> Option.iter (fun predicate -> sql <- sql + " ON " + renderExpr ctx predicate)
        select.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderExpr ctx predicate)
        if not select.GroupBy.IsEmpty then sql <- sql + " GROUP BY " + (select.GroupBy |> List.map (renderExpr ctx) |> String.concat ", ")
        select.Having |> Option.iter (fun predicate -> sql <- sql + " HAVING " + renderExpr ctx predicate)
        sql

    and private renderSetBody (ctx: RenderContext) (query: Query) =
        let headNoCtes = { query.Head with Ctes = [] }
        let mutable sql = renderSelectBody ctx headNoCtes
        for branch in query.SetOperations do
            let branchNoTail = { branch.Query with OrderBy = []; Limit = None; Offset = None }
            sql <- sql + " " + setText branch.Operator + " " + renderQueryCore ctx branchNoTail
        sql

    and private renderOrderClause (ctx: RenderContext) setTail orderBy =
        if List.isEmpty orderBy then ""
        else " ORDER BY " + (orderBy |> List.collect (renderOrderBy ctx setTail) |> String.concat ", ")

    and private renderPaging (ctx: RenderContext) (query: Query) sql =
        let intValue value = NonNegativeRowCount.value value
        match ctx.Provider with
        | PostgreSql ->
            let withLimit = query.Limit |> Option.map (fun value -> sql + " LIMIT " + ctx.Bind(box (intValue value))) |> Option.defaultValue sql
            query.Offset |> Option.map (fun value -> withLimit + " OFFSET " + ctx.Bind(box (intValue value))) |> Option.defaultValue withLimit
        | MySql ->
            match query.Limit, query.Offset with
            | None, None -> sql
            | None, Some offset -> sql + " LIMIT 18446744073709551615 OFFSET " + ctx.Bind(box (intValue offset))
            | Some limit, None -> sql + " LIMIT " + ctx.Bind(box (intValue limit))
            | Some limit, Some offset -> sql + " LIMIT " + ctx.Bind(box (intValue limit)) + " OFFSET " + ctx.Bind(box (intValue offset))
        | SQLite ->
            match query.Limit, query.Offset with
            | None, None -> sql
            | None, Some offset -> sql + " LIMIT -1 OFFSET " + ctx.Bind(box (intValue offset))
            | Some limit, None -> sql + " LIMIT " + ctx.Bind(box (intValue limit))
            | Some limit, Some offset -> sql + " LIMIT " + ctx.Bind(box (intValue limit)) + " OFFSET " + ctx.Bind(box (intValue offset))
        | Oracle ->
            match query.Limit, query.Offset with
            | None, None -> sql
            | None, Some offset -> sql + " OFFSET " + ctx.Bind(box (int64 (intValue offset))) + " ROWS"
            | Some limit, offset -> sql + " OFFSET " + ctx.Bind(box (int64 (offset |> Option.map intValue |> Option.defaultValue 0))) + " ROWS FETCH NEXT " + ctx.Bind(box (intValue limit)) + " ROWS ONLY"
        | Firebird ->
            match query.Limit, query.Offset with
            | Some limit, Some offset when intValue limit > 0 && intValue offset > 0 ->
                let first = int64 (intValue offset) + 1L
                let last = int64 (intValue offset) + int64 (intValue limit)
                sql + " ROWS " + ctx.Bind(box first) + " TO " + ctx.Bind(box last)
            | _ -> sql
        | SqlServer -> sql

    and private renderSqlServerOffset (ctx: RenderContext) (query: Query) =
        let projection = query.Head.Projection
        let outputNames = projection |> List.map projectionOutputName
        let internalNames = outputNames |> List.mapi (fun index _ -> { Value = "_core_page_" + string index; WasQuoted = false; Span = { Start = 0; Length = 0 } })
        let baseAlias = "[_core_page_base]"
        let wrapperAlias = "[results_wrapper]"
        let rowAlias = "[_core_page_row]"
        let baseProjection =
            (projection, internalNames)
            ||> List.map2 (fun item alias -> renderExpr ctx item.Expression + " AS " + renderAlias ctx.Provider alias)
            |> String.concat ", "
        let mutable baseSql = "SELECT " + (if query.Head.Distinct then "DISTINCT " else "") + baseProjection
        query.Head.From |> Option.iter (fun source -> baseSql <- baseSql + " FROM " + renderSource ctx source)
        for join in query.Head.Joins do
            baseSql <- baseSql + " " + joinText join.Kind + " " + renderSource ctx join.Source
            join.Predicate |> Option.iter (fun predicate -> baseSql <- baseSql + " ON " + renderExpr ctx predicate)
        query.Head.Where |> Option.iter (fun predicate -> baseSql <- baseSql + " WHERE " + renderExpr ctx predicate)
        if not query.Head.GroupBy.IsEmpty then baseSql <- baseSql + " GROUP BY " + (query.Head.GroupBy |> List.map (renderExpr ctx) |> String.concat ", ")
        query.Head.Having |> Option.iter (fun predicate -> baseSql <- baseSql + " HAVING " + renderExpr ctx predicate)
        let resolveOrder (order: OrderBy) =
            let index =
                match order.Expression with
                | OrderOrdinal ordinal -> PositiveRowCount.value ordinal - 1
                | Column identifier ->
                    let name = Identifier.parts identifier |> List.last |> fun part -> part.Value
                    outputNames |> List.tryFindIndex (fun output -> StringComparer.OrdinalIgnoreCase.Equals(output.Value, name)) |> Option.defaultValue -1
                | _ -> -1
            if index < 0 || index >= internalNames.Length then invalidOp "SQL Server OFFSET pagination ORDER BY must resolve to a projected output."
            let alias = renderAlias ctx.Provider internalNames[index]
            alias + (if order.Descending then " DESC" else " ASC")
        let windowOrder = if query.OrderBy.IsEmpty then "ORDER BY (SELECT 0)" else "ORDER BY " + (query.OrderBy |> List.map resolveOrder |> String.concat ", ")
        let middleOutputs = internalNames |> List.map (fun alias -> baseAlias + "." + renderAlias ctx.Provider alias) |> String.concat ", "
        let middleSql = "SELECT " + middleOutputs + ", ROW_NUMBER() OVER (" + windowOrder + ") AS " + rowAlias + " FROM (" + baseSql + ") AS " + baseAlias
        let outerOutputs =
            (internalNames, outputNames)
            ||> List.map2 (fun internalName externalName -> wrapperAlias + "." + renderAlias ctx.Provider internalName + " AS " + renderAlias ctx.Provider externalName)
            |> String.concat ", "
        let offset = query.Offset |> Option.map NonNegativeRowCount.value |> Option.defaultValue 0
        let predicate =
            match query.Limit with
            | None -> wrapperAlias + "." + rowAlias + " >= " + ctx.Bind(box (int64 offset + 1L))
            | Some limit -> wrapperAlias + "." + rowAlias + " BETWEEN " + ctx.Bind(box (int64 offset + 1L)) + " AND " + ctx.Bind(box (int64 offset + int64 (NonNegativeRowCount.value limit)))
        renderCtes ctx query.Head.Ctes + "SELECT " + outerOutputs + " FROM (" + middleSql + ") AS " + wrapperAlias + " WHERE " + predicate + " ORDER BY " + wrapperAlias + "." + rowAlias + " ASC"

    and private renderSetTailWrapper (ctx: RenderContext) (query: Query) =
        let prefix = renderCtes ctx query.Head.Ctes
        let body = renderSetBody ctx query
        let alias = renderAlias ctx.Provider { Value = "_set"; WasQuoted = false; Span = { Start = 0; Length = 0 } }
        let wrapper = "SELECT * FROM (" + body + ")" + tableAliasPrefix ctx.Provider + alias
        let ordered = wrapper + renderOrderClause ctx true query.OrderBy
        prefix + renderPaging ctx query ordered

    and private renderQueryCore (ctx: RenderContext) (query: Query) =
        if isSetQuery query && hasTail query then renderSetTailWrapper ctx query
        elif ctx.Provider = SqlServer && query.Offset |> Option.exists (fun value -> NonNegativeRowCount.value value > 0) && not (isSetQuery query) then renderSqlServerOffset ctx query
        else
            match ctx.Provider, query.Offset, query.Limit, isSetQuery query with
            | SqlServer, None, Some limit, false ->
                let top = ctx.Bind(box (NonNegativeRowCount.value limit))
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                ctes + head + "TOP (" + top + ") " + withOrder.Substring(head.Length)
            | Firebird, None, Some limit, false ->
                let first = ctx.Bind(box (NonNegativeRowCount.value limit))
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                let replacement = if query.Head.Distinct then "SELECT FIRST " + first + " DISTINCT " else "SELECT FIRST " + first + " "
                ctes + replacement + withOrder.Substring(head.Length)
            | Firebird, Some offset, None, false when NonNegativeRowCount.value offset > 0 ->
                let skip = ctx.Bind(box (NonNegativeRowCount.value offset))
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                let replacement = if query.Head.Distinct then "SELECT SKIP " + skip + " DISTINCT " else "SELECT SKIP " + skip + " "
                ctes + replacement + withOrder.Substring(head.Length)
            | _ ->
                let ctes = renderCtes ctx query.Head.Ctes
                let body = if isSetQuery query then renderSetBody ctx query else renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx (isSetQuery query) query.OrderBy
                ctes + renderPaging ctx query withOrder

    and private renderQuery (ctx: RenderContext) query = renderQueryCore ctx query

    let private renderReturning (ctx: RenderContext) items =
        if List.isEmpty items then ""
        else
            match ctx.Provider with
            | PostgreSql | SQLite | Firebird ->
                " RETURNING " + (items |> List.map (fun item -> renderExpr ctx item.Expression + (item.Alias |> Option.map (fun alias -> " AS " + renderAlias ctx.Provider alias) |> Option.defaultValue "")) |> String.concat ", ")
            | _ -> invalidOp "RETURNING is not supported by the target provider."

    let private renderConflict (ctx: RenderContext) conflict =
        match conflict with
        | None -> ""
        | Some conflict ->
            let targets = conflict.TargetColumns |> NonEmpty.toList |> List.map (renderIdentifier ctx.Provider) |> String.concat ", "
            match ctx.Provider with
            | PostgreSql | SQLite ->
                match conflict.Action with
                | DoNothing -> " ON CONFLICT (" + targets + ") DO NOTHING"
                | UpdateProposedValues assignments ->
                    let values = assignments |> NonEmpty.toList |> List.map (fun assignment -> renderIdentifier ctx.Provider assignment.Target + " = EXCLUDED." + renderIdentifier ctx.Provider assignment.Proposed) |> String.concat ", "
                    " ON CONFLICT (" + targets + ") DO UPDATE SET " + values
            | _ -> invalidOp "Portable INSERT conflict lowering is not supported by the target provider."

    let private renderInsert (ctx: RenderContext) insert =
        let columns = if insert.Columns.IsEmpty then "" else " (" + (insert.Columns |> List.map (renderAlias ctx.Provider) |> String.concat ", ") + ")"
        match ctx.Provider, insert.Conflict with
        | Firebird, Some conflict ->
            let values =
                match insert.Input with
                | Values rows when NonEmpty.length rows = 1 -> rows |> NonEmpty.toList |> List.head |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", "
                | _ -> invalidOp "Firebird UPDATE OR INSERT requires exactly one VALUES row."
            let targets = conflict.TargetColumns |> NonEmpty.toList |> List.map (renderIdentifier ctx.Provider) |> String.concat ", "
            "UPDATE OR INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns + " VALUES (" + values + ") MATCHING (" + targets + ")" + renderReturning ctx insert.Returning
        | _ ->
            let sourceSql =
                match insert.Input with
                | QuerySource query -> " " + renderQuery ctx query
                | Values rows -> " VALUES " + (rows |> NonEmpty.toList |> List.map (fun row -> "(" + (row |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", ") + ")") |> String.concat ", ")
                | DefaultValues -> " DEFAULT VALUES"
            "INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns + sourceSql + renderConflict ctx insert.Conflict + renderReturning ctx insert.Returning

    let private renderUpdate (ctx: RenderContext) (update: Update) =
        let assignments = update.Assignments |> List.map (fun (assignment: Assignment) -> renderIdentifier ctx.Provider assignment.Target + " = " + renderExpr ctx assignment.Value) |> String.concat ", "
        let mutable sql = "UPDATE " + renderIdentifier ctx.Provider update.Target + " SET " + assignments
        if not update.From.IsEmpty then
            if ctx.Provider <> PostgreSql then invalidOp "UPDATE ... FROM is not supported by the target provider."
            sql <- sql + " FROM " + (update.From |> List.map (renderSource ctx) |> String.concat ", ")
        update.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderExpr ctx predicate)
        sql + renderReturning ctx update.Returning

    let private renderDelete (ctx: RenderContext) (delete: Delete) =
        let mutable sql = "DELETE FROM " + renderIdentifier ctx.Provider delete.Target
        if not delete.Using.IsEmpty then
            if ctx.Provider <> PostgreSql then invalidOp "DELETE ... USING is not supported by the target provider."
            sql <- sql + " USING " + (delete.Using |> List.map (renderSource ctx) |> String.concat ", ")
        delete.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderExpr ctx predicate)
        sql + renderReturning ctx delete.Returning

    let render provider executable : RenderedCommand =
        let ctx = RenderContext(provider)
        let document = Executable.value executable
        let sql, returnsRows =
            match document.Statement with
            | Statement.QueryStatement query -> renderQuery ctx query, false
            | Statement.InsertStatement insert -> renderInsert ctx insert, not insert.Returning.IsEmpty
            | Statement.UpdateStatement update -> renderUpdate ctx update, not update.Returning.IsEmpty
            | Statement.DeleteStatement delete -> renderDelete ctx delete, not delete.Returning.IsEmpty
        { Sql = sql; Parameters = ctx.Parameters; ReturnsRows = returnsRows }
