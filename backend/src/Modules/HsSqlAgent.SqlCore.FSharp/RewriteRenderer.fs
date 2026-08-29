namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Globalization
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Pure F# native renderer for the rewrite model. It consumes ExecutableSql only.
module internal RewriteRenderer =

    type Provider =
        | PostgreSql
        | MySql
        | SqlServer
        | SQLite
        | Oracle
        | Firebird

    type RenderedCommand =
        { Sql: string
          Parameters: obj list }

    type private RenderContext(provider: Provider) =
        let parameters = ResizeArray<obj>()
        member _.Provider = provider
        member _.Bind(value: obj) =
            parameters.Add(value)
            let index = parameters.Count
            match provider with
            | PostgreSql -> "$" + string index
            | SqlServer -> "@p" + string index
            | Oracle -> ":p" + string index
            | MySql
            | SQLite
            | Firebird -> "?"
        member _.Parameters = parameters |> Seq.toList

    let private quotePart provider (value: string) =
        match provider with
        | MySql -> "`" + value.Replace("`", "``") + "`"
        | _ -> "\"" + value.Replace("\"", "\"\"") + "\""

    let private renderIdentifier provider identifier =
        identifier
        |> Identifier.parts
        |> List.map (fun part -> quotePart provider part.Value)
        |> String.concat "."

    let private renderAlias provider alias = quotePart provider alias.Value

    let private scalarObject value : obj =
        match value with
        | ScalarValue.Null -> null
        | ScalarValue.Boolean value -> box value
        | ScalarValue.Integer value -> box value
        | ScalarValue.Decimal value -> box value
        | ScalarValue.Floating value -> box value
        | ScalarValue.Text value -> box value
        | ScalarValue.Date value -> box value
        | ScalarValue.Time value -> box value
        | ScalarValue.LocalDateTime value -> box value
        | ScalarValue.OffsetDateTime value -> box value
        | ScalarValue.Duration value -> box value
        | ScalarValue.Bytes value -> box value

    let private unaryText = function
        | UnaryOperator.Not -> "NOT"
        | UnaryOperator.Negate -> "-"
        | UnaryOperator.Positive -> "+"

    let private binaryText = function
        | BinaryOperator.Add -> "+"
        | BinaryOperator.Subtract -> "-"
        | BinaryOperator.Multiply -> "*"
        | BinaryOperator.Divide -> "/"
        | BinaryOperator.Modulo -> "%"
        | BinaryOperator.Concat -> "||"
        | BinaryOperator.Equal -> "="
        | BinaryOperator.NotEqual -> "<>"
        | BinaryOperator.GreaterThan -> ">"
        | BinaryOperator.LessThan -> "<"
        | BinaryOperator.GreaterThanOrEqual -> ">="
        | BinaryOperator.LessThanOrEqual -> "<="
        | BinaryOperator.Like -> "LIKE"
        | BinaryOperator.ILike -> "ILIKE"
        | BinaryOperator.And -> "AND"
        | BinaryOperator.Or -> "OR"

    let private joinText = function
        | JoinKind.Inner -> "INNER JOIN"
        | JoinKind.Left -> "LEFT JOIN"
        | JoinKind.Right -> "RIGHT JOIN"
        | JoinKind.Full -> "FULL JOIN"
        | JoinKind.Cross -> "CROSS JOIN"

    let private setText = function
        | SetOperator.Union -> "UNION"
        | SetOperator.UnionAll -> "UNION ALL"
        | SetOperator.Intersect -> "INTERSECT"
        | SetOperator.Except -> "EXCEPT"

    let private validateFunctionName value =
        if not (Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_$.]*$", RegexOptions.CultureInvariant)) then
            invalidOp ("Unsafe function name '" + value + "'.")
        value

    let private validateCastType value =
        if not (Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_ ]*(\\([0-9]+(,[0-9]+)?\\))?$", RegexOptions.CultureInvariant)) then
            invalidOp ("Unsafe CAST type '" + value + "'.")
        value

    let private frameBoundText = function
        | WindowFrameBound.UnboundedPreceding -> "UNBOUNDED PRECEDING"
        | WindowFrameBound.Preceding value -> string value + " PRECEDING"
        | WindowFrameBound.CurrentRow -> "CURRENT ROW"
        | WindowFrameBound.Following value -> string value + " FOLLOWING"
        | WindowFrameBound.UnboundedFollowing -> "UNBOUNDED FOLLOWING"

    let rec private renderExpr (ctx: RenderContext) expression =
        match expression with
        | Expr.Column identifier -> renderIdentifier ctx.Provider identifier
        | Expr.Literal value -> ctx.Bind(scalarObject value)
        | Expr.Interval literal ->
            match ctx.Provider with
            | PostgreSql -> "CAST(" + ctx.Bind(box literal) + " AS interval)"
            | _ -> invalidOp "INTERVAL lowering is not available for this rewrite provider yet."
        | Expr.Unary(operator, operand) ->
            match operator with
            | UnaryOperator.Not -> "NOT (" + renderExpr ctx operand + ")"
            | _ -> "(" + unaryText operator + renderExpr ctx operand + ")"
        | Expr.Binary(operator, left, right) ->
            let leftSql = renderExpr ctx left
            let rightSql = renderExpr ctx right
            match operator, ctx.Provider with
            | BinaryOperator.Modulo, Oracle -> "MOD(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, MySql -> "CONCAT(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.ILike, provider when provider <> PostgreSql ->
                invalidOp "ILIKE requires provider-specific lowering before rendering."
            | _ -> "(" + leftSql + " " + binaryText operator + " " + rightSql + ")"
        | Expr.FunctionCall call ->
            let name = call.Name |> FunctionName.value |> validateFunctionName
            let args = call.Arguments |> List.map (renderExpr ctx) |> String.concat ", "
            let distinct = if call.IsDistinct then "DISTINCT " else ""
            name + "(" + distinct + args + ")"
        | Expr.FilteredAggregate(value, predicate) ->
            match ctx.Provider with
            | PostgreSql
            | SQLite
            | Firebird -> renderExpr ctx value + " FILTER (WHERE " + renderExpr ctx predicate + ")"
            | _ -> invalidOp "Aggregate FILTER requires provider lowering before rendering."
        | Expr.Windowed(value, window) -> renderExpr ctx value + " OVER (" + renderWindow ctx window + ")"
        | Expr.Cast(value, targetType) ->
            let castType = targetType |> CastType.value |> validateCastType
            "CAST(" + renderExpr ctx value + " AS " + castType + ")"
        | Expr.SimpleCase(input, branches, fallback) ->
            let cases =
                branches
                |> List.map (fun branch -> " WHEN " + renderExpr ctx branch.Match + " THEN " + renderExpr ctx branch.Result)
                |> String.concat ""
            let elseSql = fallback |> Option.map (fun value -> " ELSE " + renderExpr ctx value) |> Option.defaultValue ""
            "CASE " + renderExpr ctx input + cases + elseSql + " END"
        | Expr.SearchedCase(branches, fallback) ->
            let cases =
                branches
                |> List.map (fun branch -> " WHEN " + renderExpr ctx branch.Condition + " THEN " + renderExpr ctx branch.Result)
                |> String.concat ""
            let elseSql = fallback |> Option.map (fun value -> " ELSE " + renderExpr ctx value) |> Option.defaultValue ""
            "CASE" + cases + elseSql + " END"
        | Expr.InList(value, items, negated) ->
            let keyword = if negated then " NOT IN " else " IN "
            renderExpr ctx value + keyword + "(" + (items |> List.map (renderExpr ctx) |> String.concat ", ") + ")"
        | Expr.Between(value, lower, upper, negated) ->
            let keyword = if negated then " NOT BETWEEN " else " BETWEEN "
            renderExpr ctx value + keyword + renderExpr ctx lower + " AND " + renderExpr ctx upper
        | Expr.IsNull(value, negated) -> renderExpr ctx value + (if negated then " IS NOT NULL" else " IS NULL")
        | Expr.ScalarSubquery query -> "(" + renderQuery ctx query + ")"
        | Expr.Exists(query, negated) -> (if negated then "NOT EXISTS (" else "EXISTS (") + renderQuery ctx query + ")"

    and private renderWindow ctx window =
        let parts = ResizeArray<string>()
        if not window.PartitionBy.IsEmpty then
            parts.Add("PARTITION BY " + (window.PartitionBy |> List.map (renderExpr ctx) |> String.concat ", "))
        if not window.OrderBy.IsEmpty then
            parts.Add("ORDER BY " + (window.OrderBy |> List.map (renderOrderBy ctx) |> String.concat ", "))
        match window.Frame with
        | None -> ()
        | Some frame ->
            let unitText = match frame.Unit with | WindowFrameUnit.Rows -> "ROWS" | WindowFrameUnit.Range -> "RANGE"
            let frameSql =
                match frame.End with
                | None -> unitText + " " + frameBoundText frame.Start
                | Some finish -> unitText + " BETWEEN " + frameBoundText frame.Start + " AND " + frameBoundText finish
            parts.Add(frameSql)
        String.concat " " parts

    and private renderOrderBy ctx order =
        let direction = if order.Descending then " DESC" else " ASC"
        let nulls =
            match order.NullOrdering with
            | NullOrdering.Default -> ""
            | NullOrdering.NullsFirst ->
                match ctx.Provider with
                | SqlServer
                | MySql -> invalidOp "NULLS FIRST requires lowering for this provider."
                | _ -> " NULLS FIRST"
            | NullOrdering.NullsLast ->
                match ctx.Provider with
                | SqlServer
                | MySql -> invalidOp "NULLS LAST requires lowering for this provider."
                | _ -> " NULLS LAST"
        renderExpr ctx order.Expression + direction + nulls

    and private renderSource ctx source =
        match source with
        | TableSource.NamedTable(identifier, alias) ->
            renderIdentifier ctx.Provider identifier
            + (alias |> Option.map (fun value -> " AS " + renderAlias ctx.Provider value) |> Option.defaultValue "")
        | TableSource.DerivedTable(query, alias) ->
            "(" + renderQuery ctx query + ") AS " + renderAlias ctx.Provider alias

    and private renderSelect ctx select =
        let projection =
            select.Projection
            |> List.map (fun item ->
                renderExpr ctx item.Expression
                + (item.Alias |> Option.map (fun alias -> " AS " + renderAlias ctx.Provider alias) |> Option.defaultValue ""))
            |> String.concat ", "
        let mutable sql = "SELECT " + (if select.Distinct then "DISTINCT " else "") + projection
        select.From |> Option.iter (fun source -> sql <- sql + " FROM " + renderSource ctx source)
        for join in select.Joins do
            sql <- sql + " " + joinText join.Kind + " " + renderSource ctx join.Source
            join.Predicate |> Option.iter (fun predicate -> sql <- sql + " ON " + renderExpr ctx predicate)
        select.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderExpr ctx predicate)
        if not select.GroupBy.IsEmpty then sql <- sql + " GROUP BY " + (select.GroupBy |> List.map (renderExpr ctx) |> String.concat ", ")
        select.Having |> Option.iter (fun predicate -> sql <- sql + " HAVING " + renderExpr ctx predicate)
        sql

    and private renderPaging ctx query sql =
        match ctx.Provider with
        | PostgreSql
        | SQLite
        | MySql ->
            let withLimit = query.Limit |> Option.map (fun value -> sql + " LIMIT " + string value) |> Option.defaultValue sql
            match query.Offset, ctx.Provider, query.Limit with
            | Some value, MySql, None -> withLimit + " LIMIT 18446744073709551615 OFFSET " + string value
            | Some value, _, _ -> withLimit + " OFFSET " + string value
            | None, _, _ -> withLimit
        | SqlServer ->
            match query.Offset, query.Limit with
            | None, None -> sql
            | None, Some limit ->
                if sql.StartsWith("SELECT DISTINCT ", StringComparison.Ordinal) then
                    "SELECT DISTINCT TOP (" + string limit + ") " + sql.Substring("SELECT DISTINCT ".Length)
                else
                    "SELECT TOP (" + string limit + ") " + sql.Substring("SELECT ".Length)
            | Some offset, limit ->
                if query.OrderBy.IsEmpty then invalidOp "SQL Server OFFSET requires ORDER BY."
                sql + " OFFSET " + string offset + " ROWS"
                + (limit |> Option.map (fun value -> " FETCH NEXT " + string value + " ROWS ONLY") |> Option.defaultValue "")
        | Oracle
        | Firebird ->
            let withOffset = query.Offset |> Option.map (fun value -> sql + " OFFSET " + string value + " ROWS") |> Option.defaultValue sql
            query.Limit |> Option.map (fun value -> withOffset + " FETCH NEXT " + string value + " ROWS ONLY") |> Option.defaultValue withOffset

    and private renderQuery ctx query =
        let mutable sql = renderSelect ctx query.Head
        for branch in query.SetOperations do
            sql <- sql + " " + setText branch.Operator + " " + renderQuery ctx branch.Query
        if not query.OrderBy.IsEmpty then sql <- sql + " ORDER BY " + (query.OrderBy |> List.map (renderOrderBy ctx) |> String.concat ", ")
        renderPaging ctx query sql

    let private renderReturning ctx items =
        if items.IsEmpty then ""
        else
            match ctx.Provider with
            | PostgreSql
            | SQLite
            | Firebird ->
                " RETURNING "
                + (items
                   |> List.map (fun item ->
                       renderExpr ctx item.Expression
                       + (item.Alias |> Option.map (fun alias -> " AS " + renderAlias ctx.Provider alias) |> Option.defaultValue ""))
                   |> String.concat ", ")
            | _ -> invalidOp "RETURNING requires provider-specific lowering before rendering."

    let private renderInsert ctx insert =
        let columns =
            if insert.Columns.IsEmpty then ""
            else " (" + (insert.Columns |> List.map (renderAlias ctx.Provider) |> String.concat ", ") + ")"
        let sourceSql =
            match insert.Source, insert.Rows with
            | Some query, _ -> " " + renderQuery ctx query
            | None, rows when not rows.IsEmpty ->
                " VALUES "
                + (rows |> List.map (fun row -> "(" + (row |> List.map (renderExpr ctx) |> String.concat ", ") + ")") |> String.concat ", ")
            | _ -> " DEFAULT VALUES"
        "INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns + sourceSql + renderReturning ctx insert.Returning

    let private renderUpdate ctx update =
        let assignments =
            update.Assignments
            |> List.map (fun assignment -> renderIdentifier ctx.Provider assignment.Target + " = " + renderExpr ctx assignment.Value)
            |> String.concat ", "
        let mutable sql = "UPDATE " + renderIdentifier ctx.Provider update.Target + " SET " + assignments
        update.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderExpr ctx predicate)
        sql + renderReturning ctx update.Returning

    let private renderDelete ctx delete =
        let mutable sql = "DELETE FROM " + renderIdentifier ctx.Provider delete.Target
        delete.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderExpr ctx predicate)
        sql + renderReturning ctx delete.Returning

    let render provider executable =
        let ctx = RenderContext(provider)
        let document = Executable.value executable
        let sql =
            match document.Statement with
            | Statement.QueryStatement query -> renderQuery ctx query
            | Statement.InsertStatement insert -> renderInsert ctx insert
            | Statement.UpdateStatement update -> renderUpdate ctx update
            | Statement.DeleteStatement delete -> renderDelete ctx delete
        { Sql = sql; Parameters = ctx.Parameters }
