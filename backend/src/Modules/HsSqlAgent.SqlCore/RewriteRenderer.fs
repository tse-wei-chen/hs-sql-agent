namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Generic
open System.Text
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteRenderer =

    type Provider = PostgreSql | MySql | SqlServer | SQLite | Oracle | Firebird
    type RenderedCommand = { Sql: string; Parameters: (obj | null) list; ReturnsRows: bool }

    type private RenderContext(provider: Provider, targetRuntime: TargetRuntime) =
        let parameters = ResizeArray<obj | null>()
        let sharedParameters = Dictionary<string, string * (obj | null)>(StringComparer.Ordinal)
        let mutable sharedScope: string option = None
        let mutable sharedOrdinal = 0

        let parameterName index =
            match provider with Oracle -> ":p" + string index | _ -> "@p" + string index

        member _.Provider = provider
        member _.TargetRuntime = targetRuntime

        member this.Bind(value: obj | null) =
            match sharedScope with
            | Some scope ->
                let key = scope + ":" + string sharedOrdinal
                sharedOrdinal <- sharedOrdinal + 1
                this.BindShared(key, value)
            | None ->
                let name = parameterName parameters.Count
                parameters.Add(value)
                name

        member _.BindShared(key: string, value: obj | null) =
            match sharedParameters.TryGetValue(key) with
            | true, (name, existing) ->
                if not (Object.Equals(existing, value)) then
                    raise (SqlCompilationException(
                        "Native SQL renderer reused semantic binding key '" + key + "' for different values."))
                name
            | false, _ ->
                let name = parameterName parameters.Count
                parameters.Add(value)
                sharedParameters.Add(key, (name, value))
                name

        member _.WithSharedBindings(scope: string, action: unit -> 'T) : 'T =
            let previousScope = sharedScope
            let previousOrdinal = sharedOrdinal
            sharedScope <- Some scope
            sharedOrdinal <- 0
            try action ()
            finally
                sharedScope <- previousScope
                sharedOrdinal <- previousOrdinal

        member _.Parameters = parameters |> Seq.toList

    let private quotePart provider (part: IdentifierPart) =
        let raw =
            if part.WasQuoted || part.PreserveSpelling then part.Value
            else
                match provider with
                | PostgreSql -> part.Value.ToLowerInvariant()
                | Oracle | Firebird -> part.Value.ToUpperInvariant()
                | _ -> part.Value
        match provider with
        | MySql -> "`" + raw.Replace("`", "``") + "`"
        | SqlServer -> "[" + raw.Replace("]", "]]" ) + "]"
        | _ -> "\"" + raw.Replace("\"", "\"\"") + "\""

    let private renderIdentifier provider identifier = identifier |> Identifier.parts |> List.map (quotePart provider) |> String.concat "."
    let private renderAlias provider alias = quotePart provider alias

    let private renderFunctionName provider functionName =
        functionName
        |> FunctionName.parts
        |> List.map (fun part ->
            if part.WasQuoted || part.PreserveSpelling then
                quotePart provider part
            else
                match provider with
                | PostgreSql | Oracle | Firebird -> part.Value.ToUpperInvariant()
                | MySql | SqlServer | SQLite -> part.Value)
        |> String.concat "."
    let private tableAliasPrefix = function Oracle -> " " | _ -> " AS "
    let private providerName = function
        | PostgreSql -> "Postgres"
        | MySql -> "MySQL"
        | SqlServer -> "MsSqlServer"
        | SQLite -> "Sqlite"
        | Oracle -> "Oracle"
        | Firebird -> "Firebird"

    let private providerTool = function
        | PostgreSql -> SqlAgentToolType.Postgres
        | MySql -> SqlAgentToolType.MySQL
        | SqlServer -> SqlAgentToolType.MsSqlServer
        | SQLite -> SqlAgentToolType.Sqlite
        | Oracle -> SqlAgentToolType.Oracle
        | Firebird -> SqlAgentToolType.Firebird

    let private jsonPropertyPath =
        Regex("^\\$\\.[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)
    let private capabilityError provider capability =
        "SQL capability '" + capability + "' is not supported by provider " + providerName provider + " for this Core plan."

    let private scalarObject value : obj | null =
        match value with
        | ScalarValue.Null -> null
        | ScalarValue.Boolean value -> box value
        | ScalarValue.Integer value when value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue -> box (int value)
        | ScalarValue.Integer value -> box (decimal value)
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
        let bind scalar = ctx.Bind(scalarObject scalar)
        match ctx.Provider, value with
        | PostgreSql, ScalarValue.OffsetDateTime offset ->
            ctx.Bind(box (offset.ToUniversalTime()))
        | Firebird, ScalarValue.Text text ->
            if text.Length > 8191 then invalidOp "Firebird string literal exceeds the safe UTF8 VARCHAR limit of 8191 characters."
            let placeholder = bind value
            "CAST(" + placeholder + " AS VARCHAR(" + string (max 1 text.Length) + "))"
        | Firebird, ScalarValue.Boolean _ ->
            "CAST(" + bind value + " AS BOOLEAN)"
        | Firebird, ScalarValue.Integer integer when integer >= int64 Int32.MinValue && integer <= int64 Int32.MaxValue ->
            "CAST(" + bind value + " AS INTEGER)"
        | Firebird, ScalarValue.Integer _ ->
            "CAST(" + bind value + " AS BIGINT)"
        | Firebird, ScalarValue.Decimal decimalValue ->
            "CAST(" + bind value + " AS " + SqlFirebirdDecimalCapabilityRules.FirebirdCastType(decimalValue) + ")"
        | Firebird, ScalarValue.Floating _ ->
            "CAST(" + bind value + " AS DOUBLE PRECISION)"
        | Firebird, ScalarValue.Date _ ->
            "CAST(" + bind value + " AS DATE)"
        | Firebird, ScalarValue.Time _ ->
            "CAST(" + bind value + " AS TIME)"
        | Firebird, ScalarValue.LocalDateTime _ ->
            "CAST(" + bind value + " AS TIMESTAMP)"
        | Firebird, ScalarValue.OffsetDateTime _ ->
            "CAST(" + bind value + " AS TIMESTAMP WITH TIME ZONE)"
        | _ -> bind value

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
        | BinaryOperator.DistinctFrom -> "IS DISTINCT FROM" | BinaryOperator.NotDistinctFrom -> "IS NOT DISTINCT FROM"
        | BinaryOperator.And -> "AND" | BinaryOperator.Or -> "OR"

    let private joinText = function
        | JoinKind.Inner -> "INNER JOIN" | JoinKind.Left -> "LEFT JOIN" | JoinKind.Right -> "RIGHT JOIN"
        | JoinKind.Full -> "FULL OUTER JOIN" | JoinKind.Cross -> "CROSS JOIN"

    let private naturalJoinText = function
        | OnJoinKind.Inner -> "NATURAL JOIN"
        | OnJoinKind.Left -> "NATURAL LEFT JOIN"
        | OnJoinKind.Right -> "NATURAL RIGHT JOIN"
        | OnJoinKind.Full -> "NATURAL FULL OUTER JOIN"

    let private setText = function
        | SetOperator.Union -> "UNION" | SetOperator.UnionAll -> "UNION ALL"
        | SetOperator.Intersect -> "INTERSECT" | SetOperator.IntersectAll -> "INTERSECT ALL"
        | SetOperator.Except -> "EXCEPT" | SetOperator.ExceptAll -> "EXCEPT ALL"

    let private frameBoundText = function
        | WindowFrameBound.UnboundedPreceding -> "UNBOUNDED PRECEDING"
        | WindowFrameBound.Preceding value -> string (FrameOffset.value value) + " PRECEDING"
        | WindowFrameBound.CurrentRow -> "CURRENT ROW"
        | WindowFrameBound.Following value -> string (FrameOffset.value value) + " FOLLOWING"
        | WindowFrameBound.UnboundedFollowing -> "UNBOUNDED FOLLOWING"

    let private projectionOutputName (item: SelectItem) =
        match item.Alias, Expr.unspan item.Expression with
        | Some alias, _ -> alias
        | None, Column identifier
        | None, BoundColumn(identifier, _) -> Identifier.parts identifier |> List.last
        | _ -> invalidOp "Pagination requires every projected output to have a stable name; use explicit aliases for computed expressions."

    let private isSetQuery (query: Query) = not query.SetOperations.IsEmpty
    let private hasTail (query: Query) =
        not query.OrderBy.IsEmpty || query.Limit.IsSome || query.Offset.IsSome || query.FetchPercent.IsSome || query.FetchWithTies

    let rec private isBooleanExpression expression =
        match expression with
        | Spanned(_, inner) -> isBooleanExpression inner
        | Literal(ScalarValue.Boolean _) -> true
        | IsNull _ | InList _ | InSubquery _ | Between _ | Exists _ | Like _ | RegexMatch _ -> true
        | Unary(UnaryOperator.Not, _) -> true
        | Binary((BinaryOperator.Equal
                 | BinaryOperator.NotEqual
                 | BinaryOperator.GreaterThan
                 | BinaryOperator.LessThan
                 | BinaryOperator.GreaterThanOrEqual
                 | BinaryOperator.LessThanOrEqual
                 | BinaryOperator.DistinctFrom
                 | BinaryOperator.NotDistinctFrom
                 | BinaryOperator.And
                 | BinaryOperator.Or), _, _) -> true
        | SimpleCase(_, branches, fallback) ->
            let values =
                (branches |> NonEmpty.toList |> List.map (fun branch -> branch.Result))
                @ (fallback |> Option.toList)
            let nonNull =
                values
                |> List.filter (fun value ->
                    match Expr.unspan value with
                    | Literal ScalarValue.Null -> false
                    | _ -> true)
            not nonNull.IsEmpty && nonNull |> List.forall isBooleanExpression
        | SearchedCase(branches, fallback) ->
            let values =
                (branches |> NonEmpty.toList |> List.map (fun branch -> branch.Result))
                @ (fallback |> Option.toList)
            let nonNull =
                values
                |> List.filter (fun value ->
                    match Expr.unspan value with
                    | Literal ScalarValue.Null -> false
                    | _ -> true)
            not nonNull.IsEmpty && nonNull |> List.forall isBooleanExpression
        | _ -> false

    let rec private renderBooleanTruthValue expression =
        match expression with
        | Spanned(_, inner) -> renderBooleanTruthValue inner
        | Literal(ScalarValue.Boolean true) -> "1"
        | Literal(ScalarValue.Boolean false) -> "0"
        | Literal ScalarValue.Null -> "NULL"
        | _ ->
            raise (SqlCompilationException(
                "Boolean CASE predicates currently require literal TRUE, FALSE, or NULL branch results; richer predicate results remain fail-closed."))

    let rec private renderExpr (ctx: RenderContext) expression =
        match expression with
        | Expr.Spanned(_, inner) -> renderExpr ctx inner
        | Expr.Column identifier
        | Expr.BoundColumn(identifier, _) -> renderIdentifier ctx.Provider identifier
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
            match operator, ctx.Provider, ctx.TargetRuntime with
            | BinaryOperator.Modulo, (Oracle | Firebird), _ -> "MOD(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, MySql, _ -> "CONCAT(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Proven NativePipes) ->
                "(" + leftSql + " || " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Proven PlusOperator) ->
                "(" + leftSql + " + " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Unproven message) ->
                invalidOp ("Validated SQL reached rendering without SQL Server concat proof: " + message)
            | BinaryOperator.NotDistinctFrom, MySql, _ ->
                "(" + leftSql + " <=> " + rightSql + ")"
            | BinaryOperator.DistinctFrom, MySql, _ ->
                "NOT (" + leftSql + " <=> " + rightSql + ")"
            | (BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom), Oracle, _ ->
                let equal =
                    "CASE WHEN " + leftSql + " IS NULL THEN CASE WHEN " + rightSql
                    + " IS NULL THEN 1 ELSE 0 END WHEN " + rightSql
                    + " IS NULL THEN 0 WHEN " + leftSql + " = " + rightSql
                    + " THEN 1 ELSE 0 END"
                "(" + equal
                + (if operator = BinaryOperator.NotDistinctFrom then " = 1)" else " = 0)")
            | _ -> "(" + leftSql + " " + binaryText operator + " " + rightSql + ")"
        | Expr.Like(value, pattern, escape, negated, caseInsensitive) ->
            if caseInsensitive && ctx.Provider <> PostgreSql then invalidOp (capabilityError ctx.Provider "operator.ilike")
            let positive =
                "(" + renderExpr ctx value + " " + (if caseInsensitive then "ILIKE" else "LIKE") + " " + renderExpr ctx pattern
                + (escape |> Option.map (fun value -> " ESCAPE " + renderLikeEscape ctx.Provider value) |> Option.defaultValue "") + ")"
            if negated then "NOT (" + positive + ")" else positive
        | Expr.RawRegexCall _ ->
            invalidOp "Raw REGEXP_LIKE reached rendering before canonicalization."
        | Expr.RegexMatch(value, pattern) ->
            let valueSql = renderExpr ctx value
            let patternSql = renderExpr ctx pattern
            match ctx.Provider with
            | PostgreSql -> "(" + valueSql + " ~ " + patternSql + ")"
            | MySql | Oracle | SqlServer -> "REGEXP_LIKE(" + valueSql + ", " + patternSql + ")"
            | SQLite | Firebird -> invalidOp (capabilityError ctx.Provider "function.regex_match")
        | Expr.FunctionCall call ->
            renderFunction ctx call
        | Expr.FilteredAggregate(value, predicate) ->
            match ctx.Provider with
            | PostgreSql | SQLite | Oracle | Firebird ->
                renderExpr ctx value + " FILTER (WHERE " + renderPredicate ctx predicate + ")"
            | _ -> invalidOp "Aggregate FILTER requires provider-specific lowering before rendering."
        | Expr.Windowed(value, window) -> renderExpr ctx value + " OVER (" + renderWindow ctx window + ")"
        | Expr.Cast(value, targetType) -> "CAST(" + renderExpr ctx value + " AS " + RewriteCastTypes.renderTarget (providerTool ctx.Provider) targetType + ")"
        | Expr.Extract(field, value) ->
            let part = ExtractField.value field |> fun value -> value.Trim().ToUpperInvariant()
            let tool = providerTool ctx.Provider
            match SqlDatePartCapabilityRules.TargetValidationError(part, tool) with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            let rendered = renderExpr ctx value
            match ctx.Provider with
            | SqlServer -> "DATEPART(" + part + ", " + rendered + ")"
            | MySql -> part + "(" + rendered + ")"
            | PostgreSql
            | Oracle -> "EXTRACT(" + part + " FROM " + rendered + ")"
            | Firebird -> "EXTRACT(" + part + " FROM CAST(" + rendered + " AS DATE))"
            | SQLite ->
                match part with
                | "YEAR" -> "CAST(STRFTIME('%Y', " + rendered + ") AS INTEGER)"
                | "MONTH" -> "CAST(STRFTIME('%m', " + rendered + ") AS INTEGER)"
                | "DAY" -> "CAST(STRFTIME('%d', " + rendered + ") AS INTEGER)"
                | _ -> raise (SqlCompilationException("SQLite does not support date part " + part + "."))
        | Expr.SimpleCase(input, branches, fallback) ->
            let cases = branches |> NonEmpty.toList |> List.map (fun branch -> " WHEN " + renderExpr ctx branch.Match + " THEN " + renderExpr ctx branch.Result) |> String.concat ""
            "CASE " + renderExpr ctx input + cases + (fallback |> Option.map (fun value -> " ELSE " + renderExpr ctx value) |> Option.defaultValue "") + " END"
        | Expr.SearchedCase(branches, fallback) ->
            let cases =
                branches
                |> NonEmpty.toList
                |> List.map (fun branch ->
                    " WHEN " + renderPredicate ctx branch.Condition + " THEN " + renderExpr ctx branch.Result)
                |> String.concat ""
            "CASE" + cases + (fallback |> Option.map (fun value -> " ELSE " + renderExpr ctx value) |> Option.defaultValue "") + " END"
        | Expr.InList(value, items, negated) ->
            "(" + renderExpr ctx value + (if negated then " NOT IN " else " IN ") + "(" + (items |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", ") + "))"
        | Expr.InSubquery(value, query, negated) ->
            "(" + renderExpr ctx value
            + (if negated then " NOT IN (" else " IN (")
            + renderSubquery ctx query + "))"
        | Expr.Between(value, lower, upper, negated) -> "(" + renderExpr ctx value + (if negated then " NOT BETWEEN " else " BETWEEN ") + renderExpr ctx lower + " AND " + renderExpr ctx upper + ")"
        | Expr.IsNull(value, negated) -> "(" + renderExpr ctx value + (if negated then " IS NOT NULL)" else " IS NULL)")
        | Expr.ScalarSubquery query -> "(" + renderSubquery ctx query + ")"
        | Expr.Exists(query, negated) ->
            (if negated then "NOT EXISTS (" else "EXISTS (") + renderSubquery ctx query + ")"

    and private renderPredicate (ctx: RenderContext) expression =
        match ctx.Provider with
        | Oracle | SqlServer ->
            match expression with
            | Spanned(_, inner) -> renderPredicate ctx inner
            | Literal(ScalarValue.Boolean true) -> "(1 = 1)"
            | Literal(ScalarValue.Boolean false) -> "(1 = 0)"
            | Unary(UnaryOperator.Not, operand) ->
                "NOT (" + renderPredicate ctx operand + ")"
            | Binary(BinaryOperator.And, left, right) ->
                "(" + renderPredicate ctx left + " AND " + renderPredicate ctx right + ")"
            | Binary(BinaryOperator.Or, left, right) ->
                "(" + renderPredicate ctx left + " OR " + renderPredicate ctx right + ")"
            | SimpleCase(input, branches, fallback) when isBooleanExpression expression ->
                let cases =
                    branches
                    |> NonEmpty.toList
                    |> List.map (fun branch ->
                        " WHEN " + renderExpr ctx branch.Match
                        + " THEN " + renderBooleanTruthValue branch.Result)
                    |> String.concat ""
                let otherwise =
                    fallback
                    |> Option.map (fun value -> " ELSE " + renderBooleanTruthValue value)
                    |> Option.defaultValue ""
                "(CASE " + renderExpr ctx input + cases + otherwise + " END = 1)"
            | SearchedCase(branches, fallback) when isBooleanExpression expression ->
                let cases =
                    branches
                    |> NonEmpty.toList
                    |> List.map (fun branch ->
                        " WHEN " + renderPredicate ctx branch.Condition
                        + " THEN " + renderBooleanTruthValue branch.Result)
                    |> String.concat ""
                let otherwise =
                    fallback
                    |> Option.map (fun value -> " ELSE " + renderBooleanTruthValue value)
                    |> Option.defaultValue ""
                "(CASE" + cases + otherwise + " END = 1)"
            | _ -> renderExpr ctx expression
        | PostgreSql | MySql | SQLite | Firebird ->
            renderExpr ctx expression

    and private renderFunction (ctx: RenderContext) (call: FunctionCall) =
        let name = FunctionName.value call.Name |> fun value -> value.Trim().ToUpperInvariant()
        let dispatchName =
            if FunctionName.hasQuotedParts call.Name then String.Empty
            else name
        let nativeName = renderFunctionName ctx.Provider call.Name
        let tool = providerTool ctx.Provider

        let fail message = raise (SqlCompilationException(message))
        let requireCount count =
            if call.Arguments.Length <> count then
                fail ("Canonical function '" + name + "' requires " + string count + " argument(s).")

        let rec literalText label expression =
            match expression with
            | Spanned(_, inner) -> literalText label inner
            | Literal(ScalarValue.Text value) -> value
            | _ -> fail (label + " must be a string literal.")

        let literalKeyword label expression =
            let value = literalText label expression |> fun value -> value.Trim().ToUpperInvariant()
            if not (Regex.IsMatch(value, "^[A-Z_]+$", RegexOptions.CultureInvariant)) then
                fail ("Unsafe " + label + " '" + value + "'.")
            value

        let sqlStringLiteral expression label =
            let value = literalText label expression
            if ctx.Provider = MySql
               && value |> Seq.exists (fun character -> character = '\\' || Char.IsControl(character)) then
                "0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(value))
            else
                "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"

        let renderOrdinary () =
            if dispatchName.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase) then
                fail ("Canonical function '" + name + "' has no native lowering implementation; compilation was rejected.")
            if not call.AggregateOrderBy.IsEmpty || call.AggregateSeparator.IsSome then
                fail ("Aggregate-local modifiers are not supported for ordinary function '" + name + "'.")
            let rendered =
                call.Arguments
                |> List.mapi (fun index argument ->
                    let sql = renderExpr ctx argument
                    if ctx.Provider = PostgreSql && dispatchName = "ROUND" && call.Arguments.Length = 2 && index = 0 then
                        "CAST(" + sql + " AS numeric)"
                    else sql)
            let args = String.concat ", " rendered
            nativeName + "(" + (if call.IsDistinct then "DISTINCT " else "") + args + ")"

        match dispatchName with
        | "CORE_DATE_ADD" ->
            requireCount 3
            let unit = literalKeyword "DATEADD unit" call.Arguments[0]
            match SqlDateMathCapabilityRules.TargetValidationError(unit, tool, "CORE_DATE_ADD") with
            | null -> ()
            | message -> fail message
            let amount = renderExpr ctx call.Arguments[1]
            let value = renderExpr ctx call.Arguments[2]
            match ctx.Provider with
            | SqlServer -> "DATEADD(" + unit + ", " + amount + ", " + value + ")"
            | MySql -> "TIMESTAMPADD(" + unit + ", " + amount + ", " + value + ")"
            | PostgreSql -> "(" + value + " + (" + amount + " * INTERVAL '1 day'))"
            | Oracle -> "(" + value + " + " + amount + ")"
            | SQLite -> "DATETIME(" + value + ", PRINTF('%+d day', " + amount + "))"
            | Firebird -> "DATEADD(" + unit + ", " + amount + ", " + value + ")"

        | "CORE_DATE_DIFF" ->
            requireCount 3
            let unit = literalKeyword "DATEDIFF unit" call.Arguments[0]
            match SqlDateMathCapabilityRules.TargetValidationError(unit, tool, "CORE_DATE_DIFF") with
            | null -> ()
            | message -> fail message
            match ctx.Provider with
            | PostgreSql ->
                let finish = renderExpr ctx call.Arguments[2]
                let startValue = renderExpr ctx call.Arguments[1]
                "(CAST(" + finish + " AS date) - CAST(" + startValue + " AS date))"
            | Oracle ->
                let finish = renderExpr ctx call.Arguments[2]
                let startValue = renderExpr ctx call.Arguments[1]
                "(CAST(" + finish + " AS DATE) - CAST(" + startValue + " AS DATE))"
            | SQLite ->
                let finish = renderExpr ctx call.Arguments[2]
                let startValue = renderExpr ctx call.Arguments[1]
                "(JULIANDAY(" + finish + ") - JULIANDAY(" + startValue + "))"
            | SqlServer ->
                let startValue = renderExpr ctx call.Arguments[1]
                let finish = renderExpr ctx call.Arguments[2]
                "DATEDIFF(" + unit + ", " + startValue + ", " + finish + ")"
            | MySql ->
                let startValue = renderExpr ctx call.Arguments[1]
                let finish = renderExpr ctx call.Arguments[2]
                "TIMESTAMPDIFF(" + unit + ", " + startValue + ", " + finish + ")"
            | Firebird ->
                let startValue = renderExpr ctx call.Arguments[1]
                let finish = renderExpr ctx call.Arguments[2]
                "DATEDIFF(" + unit + " FROM " + startValue + " TO " + finish + ")"

        | "CORE_DATE_PART" ->
            requireCount 2
            let part = literalKeyword "date part" call.Arguments[0]
            match SqlDatePartCapabilityRules.TargetValidationError(part, tool) with
            | null -> ()
            | message -> fail message
            let value = renderExpr ctx call.Arguments[1]
            match ctx.Provider with
            | SqlServer
            | MySql -> part + "(" + value + ")"
            | PostgreSql
            | Oracle -> "EXTRACT(" + part + " FROM " + value + ")"
            | Firebird -> "EXTRACT(" + part + " FROM CAST(" + value + " AS DATE))"
            | SQLite ->
                match part with
                | "YEAR" -> "CAST(STRFTIME('%Y', " + value + ") AS INTEGER)"
                | "MONTH" -> "CAST(STRFTIME('%m', " + value + ") AS INTEGER)"
                | "DAY" -> "CAST(STRFTIME('%d', " + value + ") AS INTEGER)"
                | _ -> fail ("SQLite does not support date part " + part + ".")

        | "CORE_DATE_FORMAT" ->
            requireCount 2
            match SqlTemporalFormatCapabilityRules.TargetValidationError("CORE_DATE_FORMAT", tool) with
            | null -> ()
            | message -> fail message
            let value = renderExpr ctx call.Arguments[0]
            let formatValue = literalText "date format" call.Arguments[1]
            let format = ctx.BindShared("date-format:" + formatValue, box formatValue)
            match ctx.Provider with
            | SqlServer -> "FORMAT(" + value + ", " + format + ")"
            | PostgreSql
            | Oracle -> "TO_CHAR(" + value + ", " + format + ")"
            | MySql -> "DATE_FORMAT(" + value + ", " + format + ")"
            | SQLite -> "STRFTIME(" + format + ", " + value + ")"
            | Firebird -> fail "portable date formatting is not supported by Firebird."

        | "CORE_DATE_ONLY" ->
            requireCount 1
            match SqlDateOnlyCapabilityRules.TargetValidationError(tool) with
            | null -> ()
            | message -> fail message
            "DATE(" + renderExpr ctx call.Arguments[0] + ")"

        | "CORE_DATE_PARSE" ->
            requireCount 2
            match SqlTemporalFormatCapabilityRules.TargetValidationError("CORE_DATE_PARSE", tool) with
            | null -> ()
            | message -> fail message
            let value = renderExpr ctx call.Arguments[0]
            let formatValue = literalText "date parse format" call.Arguments[1]
            let format = ctx.BindShared("date-parse-format:" + formatValue, box formatValue)
            match ctx.Provider with
            | MySql -> "DATE(STR_TO_DATE(" + value + ", " + format + "))"
            | PostgreSql
            | Oracle -> "TO_DATE(" + value + ", " + format + ")"
            | _ -> fail "formatted date parsing is not supported by this provider."

        | "CORE_POSITION" ->
            requireCount 2
            let haystack = renderExpr ctx call.Arguments[0]
            let needle = renderExpr ctx call.Arguments[1]
            match ctx.Provider with
            | SqlServer -> "CHARINDEX(" + needle + ", " + haystack + ")"
            | PostgreSql -> "STRPOS(" + haystack + ", " + needle + ")"
            | MySql -> "LOCATE(" + needle + ", " + haystack + ")"
            | SQLite
            | Oracle -> "INSTR(" + haystack + ", " + needle + ")"
            | Firebird -> "POSITION(" + needle + ", " + haystack + ")"

        | "CORE_JSON_EXTRACT"
        | "CORE_JSON_SET" as canonical ->
            let expected = if canonical = "CORE_JSON_EXTRACT" then 2 else 3
            requireCount expected
            match SqlJsonCapabilityRules.TargetValidationError(canonical, tool) with
            | null -> ()
            | message -> fail message
            let path = literalText canonical call.Arguments[1]
            if not (jsonPropertyPath.IsMatch(path)) then
                fail (
                    "JSON path '" + path + "' is outside the portable Core property-chain subset. "
                    + "Only paths such as '$.user.name' are supported; root-only paths, array indexes, wildcards, filters, "
                    + "quoted property names, and recursive descent fail closed. SQL capability 'json.path.property_chain' "
                    + "is not supported by provider " + string tool + " for this Core plan.")
            let value = renderExpr ctx call.Arguments[0]
            let pathExpr () = renderExpr ctx call.Arguments[1]
            if canonical = "CORE_JSON_EXTRACT" then
                match ctx.Provider with
                | MySql
                | SQLite ->
                    "JSON_EXTRACT(" + value + ", " + pathExpr () + ")"
                | PostgreSql ->
                    let placeholders =
                        path.Substring(2).Split('.', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                        |> Array.map (fun segment -> ctx.Bind(box segment))
                        |> String.concat ", "
                    "JSONB_EXTRACT_PATH(CAST(" + value + " AS jsonb), " + placeholders + ")"
                | _ -> fail "JSON_EXTRACT is not supported losslessly by this provider."
            else
                let newValue =
                    match ctx.Provider with
                    | PostgreSql ->
                        let pgPath =
                            "{" + String.concat "," (path.Substring(2).Split('.', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)) + "}"
                        let pgPathPlaceholder = ctx.Bind(box pgPath)
                        let rendered = renderExpr ctx call.Arguments[2]
                        "JSONB_SET(CAST(" + value + " AS jsonb), CAST(" + pgPathPlaceholder + " AS text[]), TO_JSONB(" + rendered + "))"
                    | MySql
                    | SQLite ->
                        let pathSql = pathExpr ()
                        let rendered = renderExpr ctx call.Arguments[2]
                        "JSON_SET(" + value + ", " + pathSql + ", " + rendered + ")"
                    | SqlServer ->
                        let pathSql = pathExpr ()
                        let rendered = renderExpr ctx call.Arguments[2]
                        "JSON_MODIFY(" + value + ", " + pathSql + ", " + rendered + ")"
                    | _ -> fail "JSON_SET is not supported by this provider."
                newValue

        | "CORE_CURRENT_DATE" ->
            requireCount 0
            if call.IsDistinct then fail "Canonical current temporal function 'CORE_CURRENT_DATE' cannot be DISTINCT."
            if ctx.Provider = SqlServer then "CAST(CURRENT_TIMESTAMP AS date)" else "CURRENT_DATE"

        | "CORE_CURRENT_TIME" ->
            requireCount 0
            if call.IsDistinct then fail "Canonical current temporal function 'CORE_CURRENT_TIME' cannot be DISTINCT."
            if ctx.Provider = Oracle then fail "CURRENT_TIME is not supported by Oracle."
            elif ctx.Provider = SqlServer then "CAST(CURRENT_TIMESTAMP AS time)"
            else "CURRENT_TIME"

        | "CORE_CURRENT_TIMESTAMP" ->
            requireCount 0
            if call.IsDistinct then fail "Canonical current temporal function 'CORE_CURRENT_TIMESTAMP' cannot be DISTINCT."
            "CURRENT_TIMESTAMP"

        | "CORE_ORACLE_SYSDATE" ->
            requireCount 0
            if call.IsDistinct then fail "Oracle SYSDATE cannot be DISTINCT."
            if ctx.Provider <> Oracle then
                fail ("SQL capability 'function.oracle_sysdate' is native-only and cannot render for provider " + string (providerTool ctx.Provider) + ".")
            "SYSDATE"

        | "CORE_STRING_AGG" ->
            requireCount 2
            if call.IsDistinct then fail "Canonical CORE_STRING_AGG DISTINCT semantics are not enabled."
            let value = renderExpr ctx call.Arguments[0]
            let separator =
                if ctx.Provider = PostgreSql || ctx.Provider = SqlServer then
                    ctx.Bind(box (literalText "string aggregate separator" call.Arguments[1]))
                else
                    sqlStringLiteral call.Arguments[1] "string aggregate separator"
            let ordering =
                if call.AggregateOrderBy.IsEmpty then None
                else
                    call.AggregateOrderBy
                    |> List.collect (renderOrderBy ctx false)
                    |> String.concat ", "
                    |> fun sql -> Some("ORDER BY " + sql)
            match ordering, ctx.Provider with
            | Some order, PostgreSql -> "STRING_AGG(" + value + ", " + separator + " " + order + ")"
            | Some order, SQLite -> "GROUP_CONCAT(" + value + ", " + separator + " " + order + ")"
            | Some order, SqlServer -> "STRING_AGG(" + value + ", " + separator + ") WITHIN GROUP (" + order + ")"
            | Some order, Oracle -> "LISTAGG(" + value + ", " + separator + ") WITHIN GROUP (" + order + ")"
            | Some order, MySql -> "GROUP_CONCAT(" + value + " " + order + " SEPARATOR " + separator + ")"
            | Some _, Firebird -> fail "Aggregate-local ORDER BY lowering is not supported by Firebird."
            | None, SqlServer
            | None, PostgreSql -> "STRING_AGG(" + value + ", " + separator + ")"
            | None, MySql -> "GROUP_CONCAT(" + value + " SEPARATOR " + separator + ")"
            | None, SQLite -> "GROUP_CONCAT(" + value + ", " + separator + ")"
            | None, Oracle -> "LISTAGG(" + value + ", " + separator + ")"
            | None, Firebird -> "LIST(" + value + ", " + separator + ")"

        | _ -> renderOrdinary ()

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
                match Expr.unspan order.Expression with
                | BoundColumn(_, LocalRowSource)
                | BoundColumn(_, OuterRowSource) ->
                    let nullRank, nonNullRank = if explicitNulls = NullOrdering.NullsLast then 1, 0 else 0, 1
                    let nullRankSql = ctx.Bind(box nullRank)
                    let nonNullRankSql = ctx.Bind(box nonNullRank)
                    [ "CASE WHEN (" + expression + " IS NULL) THEN " + nullRankSql + " ELSE " + nonNullRankSql + " END ASC"; expression + direction ]
                | _ -> invalidOp (capabilityError ctx.Provider "ordering.nulls")

    and private renderCtes (ctx: RenderContext) ctes =
        if List.isEmpty ctes then ""
        else
            let recursiveScope = ctes |> List.exists (fun cte -> cte.RecursiveScope)
            if recursiveScope then
                let provider = providerTool ctx.Provider
                if not (SqlRecursiveCteCapabilityRules.SupportsWithRecursiveSyntax(provider)) then
                    raise (SqlCompilationException(
                        "SQL capability 'select.recursive_cte' is not supported by target provider "
                        + string provider + "; this provider does not use the modeled WITH RECURSIVE syntax contract."))
            "WITH "
            + (if recursiveScope then "RECURSIVE " else "")
            + (ctes
               |> List.map (fun cte -> renderAlias ctx.Provider cte.Name + " AS (" + renderCteQuery ctx cte.Query + ")")
               |> String.concat ", ")
            + " "

    and private renderSource (ctx: RenderContext) source =
        match source with
        | TableSource.NamedTable(identifier, alias) | TableSource.CteTable(identifier, alias) ->
            renderIdentifier ctx.Provider identifier + (alias |> Option.map (fun value -> tableAliasPrefix ctx.Provider + renderAlias ctx.Provider value) |> Option.defaultValue "")
        | TableSource.DerivedTable(query, alias) ->
            "(" + renderQuery ctx query + ")" + tableAliasPrefix ctx.Provider + renderAlias ctx.Provider alias
        | TableSource.LateralDerivedTable(query, alias) ->
            match SqlLateralDerivedTableCapabilityRules.TargetValidationError(providerTool ctx.Provider, null) with
            | null ->
                "LATERAL (" + renderQuery ctx query + ")" + tableAliasPrefix ctx.Provider + renderAlias ctx.Provider alias
            | message -> raise (SqlCompilationException(message))

    and private renderSelectBody (ctx: RenderContext) (select: Select) =
        let groupScope expression =
            if ctx.Provider <> PostgreSql then None
            else
                select.GroupBy
                |> List.tryFindIndex (fun grouped -> Expr.equivalent expression grouped)
                |> Option.map (fun index -> "postgres-group:" + string index)

        let renderProjectionExpression expression =
            match groupScope expression with
            | Some scope -> ctx.WithSharedBindings(scope, fun () -> renderExpr ctx expression)
            | None -> renderExpr ctx expression

        let projection =
            select.Projection
            |> List.map (fun item ->
                renderProjectionExpression item.Expression
                + (item.Alias
                   |> Option.map (fun alias -> " AS " + renderAlias ctx.Provider alias)
                   |> Option.defaultValue ""))
            |> String.concat ", "

        let distinctSql =
            match select.DistinctMode with
            | SelectDistinct.AllRows -> ""
            | SelectDistinct.DistinctRows -> "DISTINCT "
            | SelectDistinct.DistinctOn expressions ->
                if ctx.Provider <> PostgreSql then
                    raise (SqlCompilationException(
                        "SQL capability 'select.distinct_on' is not supported by this target provider."))
                "DISTINCT ON ("
                + (expressions
                   |> NonEmpty.toList
                   |> List.map (renderExpr ctx)
                   |> String.concat ", ")
                + ") "
        let mutable sql = "SELECT " + distinctSql + projection
        select.From |> Option.iter (fun source -> sql <- sql + " FROM " + renderSource ctx source)
        if select.From.IsNone then
            match ctx.Provider with Oracle -> sql <- sql + " FROM DUAL" | Firebird -> sql <- sql + " FROM RDB$DATABASE" | _ -> ()
        for join in select.Joins do
            let joinSql =
                match join with
                | NaturalJoin(kind, _) ->
                    match SqlNaturalJoinCapabilityRules.TargetValidationError(providerTool ctx.Provider) with
                    | null -> naturalJoinText kind
                    | message -> raise (SqlCompilationException(message))
                | _ -> joinText join.Kind
            sql <- sql + " " + joinSql + " " + renderSource ctx join.Source
            match join with
            | CrossJoin _
            | NaturalJoin _ -> ()
            | OnJoin(_, _, predicate) ->
                sql <- sql + " ON " + renderPredicate ctx predicate
            | UsingJoin(_, _, columns) ->
                match SqlUsingJoinCapabilityRules.TargetValidationError(providerTool ctx.Provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
                sql <-
                    sql
                    + " USING ("
                    + (columns |> NonEmpty.toList |> List.map (renderAlias ctx.Provider) |> String.concat ", ")
                    + ")"
        select.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderPredicate ctx predicate)
        if not select.GroupBy.IsEmpty then
            let grouped =
                select.GroupBy
                |> List.mapi (fun index expression ->
                    if ctx.Provider = PostgreSql then
                        ctx.WithSharedBindings("postgres-group:" + string index, fun () -> renderExpr ctx expression)
                    else
                        renderExpr ctx expression)
                |> String.concat ", "
            sql <- sql + " GROUP BY " + grouped
        select.Having |> Option.iter (fun predicate -> sql <- sql + " HAVING " + renderPredicate ctx predicate)
        sql

    and private renderSetBody (ctx: RenderContext) (query: Query) =
        let headNoCtes = { query.Head with Ctes = [] }
        let mutable sql = renderSelectBody ctx headNoCtes
        for branch in query.SetOperations do
            let branchNoTail =
                { branch.Query with
                    OrderBy = []
                    Limit = None
                    Offset = None
                    FetchPercent = None
                    FetchWithTies = false }
            let branchSql =
                if ctx.Provider = PostgreSql && not branchNoTail.SetOperations.IsEmpty then
                    "(" + renderQueryCore ctx branchNoTail + ")"
                elif branchNoTail.Head.Ctes.IsEmpty then
                    renderQueryCore ctx branchNoTail
                else
                    "SELECT * FROM ("
                    + renderQueryCore ctx branchNoTail
                    + ") AS "
                    + renderAlias ctx.Provider
                        { Value = "_set_branch"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            sql <- sql + " " + setText branch.Operator + " " + branchSql
        sql

    and private renderOrderClause (ctx: RenderContext) setTail orderBy =
        if List.isEmpty orderBy then ""
        else " ORDER BY " + (orderBy |> List.collect (renderOrderBy ctx setTail) |> String.concat ", ")

    and private renderPaging (ctx: RenderContext) (query: Query) sql =
        let intValue value = NonNegativeRowCount.value value
        let percentValue value = NonNegativePercentage.value value
        if query.FetchPercent.IsSome && ctx.Provider <> Oracle then
            invalidOp ("FETCH PERCENT reached rendering for unsupported provider " + string (providerTool ctx.Provider) + ".")
        match ctx.Provider with
        | PostgreSql when query.FetchWithTies ->
            let withOffset =
                query.Offset
                |> Option.map (fun value -> sql + " OFFSET " + ctx.Bind(box (intValue value)) + " ROWS")
                |> Option.defaultValue sql
            match query.Limit with
            | Some limit ->
                withOffset + " FETCH FIRST " + ctx.Bind(box (intValue limit)) + " ROWS WITH TIES"
            | None ->
                invalidOp "FETCH WITH TIES reached PostgreSQL rendering without a row-count limit."
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
            match query.Limit, query.FetchPercent, query.Offset, query.FetchWithTies with
            | None, None, None, false -> sql
            | None, None, Some offset, false ->
                sql + " OFFSET " + ctx.Bind(box (int64 (intValue offset))) + " ROWS"
            | Some limit, None, offset, true ->
                sql
                + " OFFSET "
                + ctx.Bind(box (int64 (offset |> Option.map intValue |> Option.defaultValue 0)))
                + " ROWS FETCH NEXT "
                + ctx.Bind(box (intValue limit))
                + " ROWS WITH TIES"
            | Some limit, None, offset, false ->
                sql
                + " OFFSET "
                + ctx.Bind(box (int64 (offset |> Option.map intValue |> Option.defaultValue 0)))
                + " ROWS FETCH NEXT "
                + ctx.Bind(box (intValue limit))
                + " ROWS ONLY"
            | None, Some percent, offset, ties ->
                sql
                + " OFFSET "
                + ctx.Bind(box (int64 (offset |> Option.map intValue |> Option.defaultValue 0)))
                + " ROWS FETCH NEXT "
                + ctx.Bind(box (percentValue percent))
                + " PERCENT ROWS "
                + (if ties then "WITH TIES" else "ONLY")
            | Some _, Some _, _, _ ->
                invalidOp "FETCH row count and percentage reached Oracle rendering simultaneously."
            | None, None, _, true ->
                invalidOp "FETCH WITH TIES reached Oracle rendering without a row count or percentage."
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
        let internalNames =
            outputNames
            |> List.mapi (fun index _ ->
                { Value = "_core_page_" + string index
                  WasQuoted = false
                  PreserveSpelling = false
                  Span = { Start = 0; Length = 0 } })
        let baseAlias = "[_core_page_base]"
        let wrapperAlias = "[results_wrapper]"
        let rowAlias = "[_core_page_row]"
        let baseProjection = ResizeArray<string>()
        (projection, internalNames)
        ||> List.iter2 (fun item alias ->
            baseProjection.Add(renderExpr ctx item.Expression + " AS " + renderAlias ctx.Provider alias))

        let projectionIndex (order: OrderBy) =
            match Expr.unspan order.Expression with
            | OrderOrdinal ordinal ->
                let index = PositiveRowCount.value ordinal - 1
                if index >= 0 && index < projection.Length then Some index else None
            | Column identifier
            | BoundColumn(identifier, _) when Identifier.parts identifier |> List.length = 1 ->
                let name = Identifier.parts identifier |> List.head |> fun part -> part.Value
                let aliasMatches =
                    projection
                    |> List.indexed
                    |> List.choose (fun (index, item) ->
                        item.Alias
                        |> Option.bind (fun alias ->
                            if StringComparer.OrdinalIgnoreCase.Equals(alias.Value, name) then Some index else None))
                match aliasMatches with
                | [ index ] -> Some index
                | _ :: _ :: _ -> invalidOp ("SQL Server OFFSET pagination ORDER BY alias '" + name + "' is ambiguous.")
                | [] -> projection |> List.tryFindIndex (fun item -> Expr.equivalent item.Expression order.Expression)
            | _ -> projection |> List.tryFindIndex (fun item -> Expr.equivalent item.Expression order.Expression)

        let windowOrders =
            query.OrderBy
            |> List.mapi (fun orderIndex order ->
                let orderAlias =
                    match projectionIndex order with
                    | Some index -> internalNames[index]
                    | None when not query.Head.Distinct ->
                        let hidden =
                            { Value = "_core_page_order_" + string orderIndex
                              WasQuoted = false
                              PreserveSpelling = false
                              Span = { Start = 0; Length = 0 } }
                        baseProjection.Add(renderExpr ctx order.Expression + " AS " + renderAlias ctx.Provider hidden)
                        hidden
                    | None ->
                        invalidOp "SQL Server DISTINCT OFFSET pagination requires every ORDER BY expression to resolve to a projected output."
                renderAlias ctx.Provider orderAlias + (if order.Descending then " DESC" else " ASC"))

        let mutable baseSql =
            "SELECT "
            + (if query.Head.Distinct then "DISTINCT " else "")
            + (baseProjection |> Seq.toList |> String.concat ", ")
        query.Head.From |> Option.iter (fun source -> baseSql <- baseSql + " FROM " + renderSource ctx source)
        for join in query.Head.Joins do
            baseSql <- baseSql + " " + joinText join.Kind + " " + renderSource ctx join.Source
            match join with
            | CrossJoin _ -> ()
            | NaturalJoin _ ->
                invalidOp "NATURAL JOIN cannot enter SQL Server pagination lowering without an explicit target capability."
            | OnJoin(_, _, predicate) ->
                baseSql <- baseSql + " ON " + renderPredicate ctx predicate
            | UsingJoin(_, _, _) ->
                invalidOp "JOIN USING cannot enter SQL Server pagination lowering without an explicit target capability."
        query.Head.Where |> Option.iter (fun predicate -> baseSql <- baseSql + " WHERE " + renderPredicate ctx predicate)
        if not query.Head.GroupBy.IsEmpty then
            baseSql <- baseSql + " GROUP BY " + (query.Head.GroupBy |> List.map (renderExpr ctx) |> String.concat ", ")
        query.Head.Having |> Option.iter (fun predicate -> baseSql <- baseSql + " HAVING " + renderPredicate ctx predicate)

        let windowOrder =
            if windowOrders.IsEmpty then "ORDER BY (SELECT 0)"
            else "ORDER BY " + String.concat ", " windowOrders
        let middleOutputs =
            internalNames
            |> List.map (fun alias -> baseAlias + "." + renderAlias ctx.Provider alias)
            |> String.concat ", "
        let middleSql =
            "SELECT "
            + middleOutputs
            + ", ROW_NUMBER() OVER ("
            + windowOrder
            + ") AS "
            + rowAlias
            + " FROM ("
            + baseSql
            + ") AS "
            + baseAlias
        let outerOutputs =
            (internalNames, outputNames)
            ||> List.map2 (fun internalName externalName ->
                wrapperAlias
                + "."
                + renderAlias ctx.Provider internalName
                + " AS "
                + renderAlias ctx.Provider externalName)
            |> String.concat ", "
        let offset = query.Offset |> Option.map NonNegativeRowCount.value |> Option.defaultValue 0
        let predicate =
            match query.Limit with
            | None -> wrapperAlias + "." + rowAlias + " >= " + ctx.Bind(box (int64 offset + 1L))
            | Some limit ->
                wrapperAlias
                + "."
                + rowAlias
                + " BETWEEN "
                + ctx.Bind(box (int64 offset + 1L))
                + " AND "
                + ctx.Bind(box (int64 offset + int64 (NonNegativeRowCount.value limit)))
        renderCtes ctx query.Head.Ctes
        + "SELECT "
        + outerOutputs
        + " FROM ("
        + middleSql
        + ") AS "
        + wrapperAlias
        + " WHERE "
        + predicate
        + " ORDER BY "
        + wrapperAlias
        + "."
        + rowAlias
        + " ASC"

    and private renderSqlServerSetOffset (ctx: RenderContext) (query: Query) =
        let outputNames = query.Head.Projection |> List.map projectionOutputName
        let internalNames =
            outputNames
            |> List.mapi (fun index _ ->
                { Value = "_core_page_" + string index
                  WasQuoted = false
                  PreserveSpelling = false
                  Span = { Start = 0; Length = 0 } })
        let setAlias = "[_set]"
        let baseAlias = "[_core_page_base]"
        let wrapperAlias = "[results_wrapper]"
        let rowAlias = "[_core_page_row]"
        let body = renderSetBody ctx query
        let pageSourceOutputs =
            (outputNames, internalNames)
            ||> List.map2 (fun outputName internalName ->
                setAlias
                + "."
                + renderAlias ctx.Provider outputName
                + " AS "
                + renderAlias ctx.Provider internalName)
            |> String.concat ", "
        let pageSource =
            "SELECT " + pageSourceOutputs + " FROM (" + body + ") AS " + setAlias

        let resolveOrder (order: OrderBy) =
            let index =
                match Expr.unspan order.Expression with
                | OrderOrdinal ordinal -> PositiveRowCount.value ordinal - 1
                | Column identifier
                | BoundColumn(identifier, _) when Identifier.parts identifier |> List.length = 1 ->
                    let reference = Identifier.parts identifier |> List.head |> fun part -> part.Value
                    let matches =
                        outputNames
                        |> List.indexed
                        |> List.filter (fun (_, alias) -> StringComparer.OrdinalIgnoreCase.Equals(alias.Value, reference))
                    match matches with
                    | [ (index, _) ] -> index
                    | _ -> invalidOp ("SQL Server set-operation OFFSET pagination ORDER BY reference '" + reference + "' is not a unique combined output name.")
                | _ -> invalidOp "SQL Server set-operation OFFSET pagination supports ORDER BY output names or ordinals only."
            if index < 0 || index >= internalNames.Length then
                invalidOp "SQL Server set-operation OFFSET pagination ORDER BY position is outside the projected output width."
            renderAlias ctx.Provider internalNames[index] + (if order.Descending then " DESC" else " ASC")

        let windowOrder =
            if query.OrderBy.IsEmpty then "ORDER BY (SELECT 0)"
            else "ORDER BY " + (query.OrderBy |> List.map resolveOrder |> String.concat ", ")
        let middleOutputs =
            internalNames
            |> List.map (fun alias -> baseAlias + "." + renderAlias ctx.Provider alias)
            |> String.concat ", "
        let middleSql =
            "SELECT "
            + middleOutputs
            + ", ROW_NUMBER() OVER ("
            + windowOrder
            + ") AS "
            + rowAlias
            + " FROM ("
            + pageSource
            + ") AS "
            + baseAlias
        let outerOutputs =
            (internalNames, outputNames)
            ||> List.map2 (fun internalName externalName ->
                wrapperAlias
                + "."
                + renderAlias ctx.Provider internalName
                + " AS "
                + renderAlias ctx.Provider externalName)
            |> String.concat ", "
        let offset = query.Offset |> Option.map NonNegativeRowCount.value |> Option.defaultValue 0
        let predicate =
            match query.Limit with
            | None -> wrapperAlias + "." + rowAlias + " >= " + ctx.Bind(box (int64 offset + 1L))
            | Some limit ->
                wrapperAlias
                + "."
                + rowAlias
                + " BETWEEN "
                + ctx.Bind(box (int64 offset + 1L))
                + " AND "
                + ctx.Bind(box (int64 offset + int64 (NonNegativeRowCount.value limit)))
        renderCtes ctx query.Head.Ctes
        + "SELECT "
        + outerOutputs
        + " FROM ("
        + middleSql
        + ") AS "
        + wrapperAlias
        + " WHERE "
        + predicate
        + " ORDER BY "
        + wrapperAlias
        + "."
        + rowAlias
        + " ASC"

    and private renderSetTailWrapper (ctx: RenderContext) (query: Query) =
        match ctx.Provider, query.Offset, query.Limit with
        | SqlServer, Some offset, limit
            when NonNegativeRowCount.value offset > 0
                 && (limit |> Option.map NonNegativeRowCount.value <> Some 0) ->
            renderSqlServerSetOffset ctx query
        | SqlServer, _, Some limit ->
            let prefix = renderCtes ctx query.Head.Ctes
            let body = renderSetBody ctx query
            let top = ctx.Bind(box (NonNegativeRowCount.value limit))
            let alias = renderAlias ctx.Provider { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            prefix
            + "SELECT TOP ("
            + top
            + ") * FROM ("
            + body
            + ")"
            + tableAliasPrefix ctx.Provider
            + alias
            + renderOrderClause ctx true query.OrderBy
        | Firebird, offset, Some limit when NonNegativeRowCount.value limit = 0 ->
            let first = ctx.Bind(box 0)
            let skip =
                match offset with
                | Some value when NonNegativeRowCount.value value > 0 ->
                    " SKIP " + ctx.Bind(box (NonNegativeRowCount.value value))
                | _ -> ""
            let prefix = renderCtes ctx query.Head.Ctes
            let body = renderSetBody ctx query
            let alias = renderAlias ctx.Provider { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            prefix
            + "SELECT FIRST " + first + skip
            + " * FROM (" + body + ")"
            + tableAliasPrefix ctx.Provider + alias
            + renderOrderClause ctx true query.OrderBy
        | Firebird, None, Some limit ->
            let first = ctx.Bind(box (NonNegativeRowCount.value limit))
            let prefix = renderCtes ctx query.Head.Ctes
            let body = renderSetBody ctx query
            let alias = renderAlias ctx.Provider { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            prefix
            + "SELECT FIRST " + first
            + " * FROM (" + body + ")"
            + tableAliasPrefix ctx.Provider + alias
            + renderOrderClause ctx true query.OrderBy
        | Firebird, Some offset, None when NonNegativeRowCount.value offset > 0 ->
            let skip = ctx.Bind(box (NonNegativeRowCount.value offset))
            let prefix = renderCtes ctx query.Head.Ctes
            let body = renderSetBody ctx query
            let alias = renderAlias ctx.Provider { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            prefix
            + "SELECT SKIP " + skip
            + " * FROM (" + body + ")"
            + tableAliasPrefix ctx.Provider + alias
            + renderOrderClause ctx true query.OrderBy
        | _ ->
            let prefix = renderCtes ctx query.Head.Ctes
            let body = renderSetBody ctx query
            let alias = renderAlias ctx.Provider { Value = "_set"; WasQuoted = false; PreserveSpelling = false; Span = { Start = 0; Length = 0 } }
            let wrapper = "SELECT * FROM (" + body + ")" + tableAliasPrefix ctx.Provider + alias
            let ordered = wrapper + renderOrderClause ctx true query.OrderBy
            prefix + renderPaging ctx query ordered

    and private renderQueryCore (ctx: RenderContext) (query: Query) =
        if isSetQuery query && hasTail query then renderSetTailWrapper ctx query
        elif ctx.Provider = SqlServer
             && query.Offset |> Option.exists (fun value -> NonNegativeRowCount.value value > 0)
             && (query.Limit |> Option.map NonNegativeRowCount.value <> Some 0)
             && not (isSetQuery query) then
            renderSqlServerOffset ctx query
        else
            match ctx.Provider, query.Offset, query.Limit, isSetQuery query with
            | SqlServer, _, Some limit, false when NonNegativeRowCount.value limit = 0 ->
                let top = ctx.Bind(box 0)
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                ctes + head + "TOP (" + top + ") " + withOrder.Substring(head.Length)
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
            | Firebird, Some offset, Some limit, false when NonNegativeRowCount.value limit = 0 ->
                let first = ctx.Bind(box 0)
                let skip =
                    if NonNegativeRowCount.value offset > 0 then
                        " SKIP " + ctx.Bind(box (NonNegativeRowCount.value offset))
                    else ""
                let ctes = renderCtes ctx query.Head.Ctes
                let body = renderSelectBody ctx { query.Head with Ctes = [] }
                let withOrder = body + renderOrderClause ctx false query.OrderBy
                let head = if query.Head.Distinct then "SELECT DISTINCT " else "SELECT "
                let replacement =
                    if query.Head.Distinct then "SELECT FIRST " + first + skip + " DISTINCT "
                    else "SELECT FIRST " + first + skip + " "
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

    and private renderSubquery (ctx: RenderContext) query =
        if isSetQuery query
           && hasTail query
           && (ctx.Provider = PostgreSql || ctx.Provider = MySql || ctx.Provider = SQLite) then
            let prefix = renderCtes ctx query.Head.Ctes
            let body = renderSetBody ctx query
            let ordered = body + renderOrderClause ctx true query.OrderBy
            prefix + renderPaging ctx query ordered
        else
            renderQueryCore ctx query

    and private renderCteQuery (ctx: RenderContext) query =
        if isSetQuery query
           && hasTail query
           && not query.Head.Ctes.IsEmpty
           && (ctx.Provider = PostgreSql || ctx.Provider = MySql || ctx.Provider = SQLite) then
            let inner = renderCtes ctx query.Head.Ctes + renderSetBody ctx query
            let alias =
                renderAlias ctx.Provider
                    { Value = "_set"
                      WasQuoted = false
                      PreserveSpelling = false
                      Span = { Start = 0; Length = 0 } }
            let wrapper = "SELECT * FROM (" + inner + ")" + tableAliasPrefix ctx.Provider + alias
            let ordered = wrapper + renderOrderClause ctx true query.OrderBy
            renderPaging ctx query ordered
        else
            renderQueryCore ctx query

    let private renderReturning (ctx: RenderContext) (items: ReturningItem list) =
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
            let targetClause =
                conflict.TargetColumns
                |> Option.map (fun columns ->
                    " (" + (columns |> NonEmpty.toList |> List.map (renderIdentifier ctx.Provider) |> String.concat ", ") + ")")
                |> Option.defaultValue ""
            match ctx.Provider with
            | PostgreSql | SQLite ->
                match conflict.Action with
                | DoNothing -> " ON CONFLICT" + targetClause + " DO NOTHING"
                | UpdateProposedValues assignments ->
                    if Option.isNone conflict.TargetColumns then
                        invalidOp "ON CONFLICT DO UPDATE reached rendering without an explicit conflict target."
                    let values = assignments |> NonEmpty.toList |> List.map (fun assignment -> renderIdentifier ctx.Provider assignment.Target + " = EXCLUDED." + renderIdentifier ctx.Provider assignment.Proposed) |> String.concat ", "
                    " ON CONFLICT" + targetClause + " DO UPDATE SET " + values
            | _ -> invalidOp "Portable INSERT conflict lowering is not supported by the target provider."

    let private renderInsert (ctx: RenderContext) insert =
        let columns = if insert.Columns.IsEmpty then "" else " (" + (insert.Columns |> List.map (renderAlias ctx.Provider) |> String.concat ", ") + ")"
        match ctx.Provider, insert.Conflict with
        | Firebird, Some conflict ->
            let values =
                match insert.Input with
                | Values rows when NonEmpty.length rows = 1 -> rows |> NonEmpty.toList |> List.head |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", "
                | _ -> invalidOp "Firebird UPDATE OR INSERT requires exactly one VALUES row."
            let targets =
                conflict.TargetColumns
                |> Option.defaultWith (fun () -> invalidOp "Firebird UPDATE OR INSERT requires an explicit MATCHING conflict target.")
                |> NonEmpty.toList
                |> List.map (renderIdentifier ctx.Provider)
                |> String.concat ", "
            "UPDATE OR INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns + " VALUES (" + values + ") MATCHING (" + targets + ")" + renderReturning ctx insert.Returning
        | MySql, Some conflict ->
            let values =
                match insert.Input with
                | Values rows when NonEmpty.length rows = 1 ->
                    rows
                    |> NonEmpty.toList
                    |> List.head
                    |> NonEmpty.toList
                    |> List.map (renderExpr ctx)
                    |> String.concat ", "
                | _ ->
                    invalidOp "Validated MySQL conflict lowering requires exactly one VALUES row."
            let preferredAlias = "__core_proposed"
            let targetName =
                insert.Target
                |> Identifier.parts
                |> List.last
                |> fun part -> part.Value
            let aliasName =
                if StringComparer.OrdinalIgnoreCase.Equals(targetName, preferredAlias) then
                    preferredAlias + "_row"
                else
                    preferredAlias
            let alias =
                renderAlias
                    MySql
                    { Value = aliasName
                      WasQuoted = false
                      PreserveSpelling = false
                      Span = { Start = 0; Length = 0 } }
            let assignments =
                match conflict.Action with
                | DoNothing ->
                    invalidOp "Validated MySQL conflict lowering cannot contain DO NOTHING."
                | UpdateProposedValues assignments ->
                    assignments
                    |> NonEmpty.toList
                    |> List.map (fun assignment ->
                        renderIdentifier MySql assignment.Target
                        + " = "
                        + alias
                        + "."
                        + renderIdentifier MySql assignment.Proposed)
                    |> String.concat ", "
            "INSERT INTO "
            + renderIdentifier MySql insert.Target
            + columns
            + " VALUES ("
            + values
            + ") AS "
            + alias
            + " ON DUPLICATE KEY UPDATE "
            + assignments
        | Oracle, None ->
            let prefix = "INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns
            match insert.Input with
            | Values rows when NonEmpty.length rows > 1 ->
                let table = renderIdentifier ctx.Provider insert.Target
                let parts =
                    rows
                    |> NonEmpty.toList
                    |> List.map (fun row ->
                        " INTO " + table + columns + " VALUES ("
                        + (row |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", ")
                        + ")")
                    |> String.concat ""
                "INSERT ALL" + parts + " SELECT 1 FROM DUAL" + renderReturning ctx insert.Returning
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                prefix + " " + withClause + source + renderReturning ctx insert.Returning
            | QuerySource query -> prefix + " " + renderQuery ctx query + renderReturning ctx insert.Returning
            | Values rows ->
                prefix + " VALUES "
                + (rows |> NonEmpty.toList |> List.map (fun row ->
                    "(" + (row |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", ") + ")")
                   |> String.concat ", ")
                + renderReturning ctx insert.Returning
            | DefaultValues -> prefix + " DEFAULT VALUES" + renderReturning ctx insert.Returning
        | Firebird, None ->
            let prefix = "INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns
            match insert.Input with
            | Values rows when NonEmpty.length rows > 1 ->
                prefix + " "
                + (rows
                   |> NonEmpty.toList
                   |> List.map (fun row ->
                       "SELECT "
                       + (row |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", ")
                       + " FROM RDB$DATABASE")
                   |> String.concat " UNION ALL ")
                + renderReturning ctx insert.Returning
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                prefix + " " + withClause + source + renderReturning ctx insert.Returning
            | QuerySource query -> prefix + " " + renderQuery ctx query + renderReturning ctx insert.Returning
            | Values rows ->
                prefix + " VALUES "
                + (rows |> NonEmpty.toList |> List.map (fun row ->
                    "(" + (row |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", ") + ")")
                   |> String.concat ", ")
                + renderReturning ctx insert.Returning
            | DefaultValues -> prefix + " DEFAULT VALUES" + renderReturning ctx insert.Returning
        | _ ->
            let prefix = "INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns
            match insert.Input with
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                match ctx.Provider with
                | PostgreSql | SqlServer | SQLite ->
                    withClause + prefix + " " + source
                    + renderConflict ctx insert.Conflict + renderReturning ctx insert.Returning
                | MySql ->
                    prefix + " " + withClause + source
                    + renderConflict ctx insert.Conflict + renderReturning ctx insert.Returning
                | Oracle | Firebird ->
                    invalidOp "Provider-specific INSERT ... SELECT path was not selected."
            | QuerySource query ->
                prefix + " " + renderQuery ctx query
                + renderConflict ctx insert.Conflict + renderReturning ctx insert.Returning
            | Values rows ->
                prefix + " VALUES "
                + (rows |> NonEmpty.toList |> List.map (fun row ->
                    "(" + (row |> NonEmpty.toList |> List.map (renderExpr ctx) |> String.concat ", ") + ")")
                   |> String.concat ", ")
                + renderConflict ctx insert.Conflict + renderReturning ctx insert.Returning
            | DefaultValues ->
                prefix + " DEFAULT VALUES"
                + renderConflict ctx insert.Conflict + renderReturning ctx insert.Returning

    let private renderUpdate (ctx: RenderContext) (update: Update) =
        let assignments = update.Assignments |> List.map (fun (assignment: Assignment) -> renderIdentifier ctx.Provider assignment.Target + " = " + renderExpr ctx assignment.Value) |> String.concat ", "
        let targetAlias =
            match update.TargetAlias with
            | None -> ""
            | Some alias when ctx.Provider = PostgreSql -> " AS " + renderAlias ctx.Provider alias
            | Some _ -> invalidOp "DML target aliases are not supported by the target provider."
        let mutable sql = "UPDATE " + renderIdentifier ctx.Provider update.Target + targetAlias + " SET " + assignments
        if not update.From.IsEmpty then
            if ctx.Provider <> PostgreSql && ctx.Provider <> SqlServer then
                invalidOp "UPDATE ... FROM is not supported by the target provider."
            sql <- sql + " FROM " + (update.From |> List.map (renderSource ctx) |> String.concat ", ")
        update.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderPredicate ctx predicate)
        sql + renderReturning ctx update.Returning

    let private renderDelete (ctx: RenderContext) (delete: Delete) =
        let targetAlias =
            match delete.TargetAlias with
            | None -> ""
            | Some alias when ctx.Provider = PostgreSql -> " AS " + renderAlias ctx.Provider alias
            | Some _ -> invalidOp "DML target aliases are not supported by the target provider."
        let mutable sql = "DELETE FROM " + renderIdentifier ctx.Provider delete.Target + targetAlias
        if not delete.Using.IsEmpty then
            if ctx.Provider <> PostgreSql then invalidOp "DELETE ... USING is not supported by the target provider."
            sql <- sql + " USING " + (delete.Using |> List.map (renderSource ctx) |> String.concat ", ")
        delete.Where |> Option.iter (fun predicate -> sql <- sql + " WHERE " + renderPredicate ctx predicate)
        sql + renderReturning ctx delete.Returning

    let private providerForRuntime = function
        | TargetRuntime.PostgreSqlRuntime -> Provider.PostgreSql
        | TargetRuntime.MySqlRuntime -> Provider.MySql
        | TargetRuntime.SqlServerRuntime _ -> Provider.SqlServer
        | TargetRuntime.SQLiteRuntime -> Provider.SQLite
        | TargetRuntime.OracleRuntime -> Provider.Oracle
        | TargetRuntime.FirebirdRuntime -> Provider.Firebird

    let render (executable: RewritePolicy.ExecutableSql) : RenderedCommand =
        let targetRuntime = RewritePolicy.Executable.targetRuntime executable
        let ctx = RenderContext(providerForRuntime targetRuntime, targetRuntime)
        let document = RewritePolicy.Executable.value executable
        let sql, returnsRows =
            match document.Statement with
            | Statement.QueryStatement query -> renderQuery ctx query, false
            | Statement.InsertStatement insert -> renderInsert ctx insert, not insert.Returning.IsEmpty
            | Statement.UpdateStatement update -> renderUpdate ctx update, not update.Returning.IsEmpty
            | Statement.DeleteStatement delete -> renderDelete ctx delete, not delete.Returning.IsEmpty
        { Sql = sql; Parameters = ctx.Parameters; ReturnsRows = returnsRows }
