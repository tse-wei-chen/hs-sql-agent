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

    // Independent of the source lexer character/token caps: a dialect
    // renderer can expand validated AST nodes when lowering SQL.
    [<Literal>]
    let private MaxRenderedSqlLength = 16777216 // UTF-16 code units

    // Guard transient SQL-emitter copies independently of final SQL size.
    // This is a conservative work ceiling, NOT an absolute process RSS cap.
    [<Literal>]
    let private MaxRenderedSqlWorkLength = 134217728L // aggregate appended UTF-16 units

    type private RenderContext(provider: Provider, targetRuntime: TargetRuntime, sourceSpan: Span) =
        let parameters = ResizeArray<obj | null>()
        let sharedParameters = Dictionary<string, string * (obj | null)>(StringComparer.Ordinal)
        let mutable sharedScope: string option = None
        let mutable sharedOrdinal = 0
        let mutable appendedWork = 0L

        let failBudget code detail : 'T =
            let location =
                if sourceSpan.Start >= 0 && sourceSpan.Length >= 0 then
                    SqlDiagnosticSpan(sourceSpan.Start, sourceSpan.Length)
                else
                    Unchecked.defaultof<SqlDiagnosticSpan>
            raise (
                SqlCompilationException(
                    detail,
                    SqlDiagnostic(
                        code,
                        SqlDiagnosticStage.RenderingInvariant,
                        SqlDiagnosticCategory.Resource,
                        detail,
                        location)))

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

        // The limit is checked BEFORE StringBuilder grows, so a huge
        // intermediate buffer cannot allocate above the rendered-SQL cap.
        // Aggregate emitted work also bounds repeated text-copy paths.
        member _.CheckAppend(existingLength: int, addedLength: int) =
            RewriteResourceBudget.checkpoint ()
            if int64 existingLength + int64 addedLength > int64 MaxRenderedSqlLength then
                failBudget
                    "SQL_COMPILER_RENDERED_OUTPUT_LIMIT"
                    ("Rendered SQL exceeds the maximum "
                     + string MaxRenderedSqlLength + " UTF-16 code units.")
            if appendedWork + int64 addedLength > MaxRenderedSqlWorkLength then
                failBudget
                    "SQL_COMPILER_RENDER_WORK_LIMIT"
                    ("SQL rendering exceeds the maximum "
                     + string MaxRenderedSqlWorkLength + " appended UTF-16 code units.")
            appendedWork <- appendedWork + int64 addedLength

    // Check each write before allocation instead of rejecting only after
    // materializing a full (possibly transient) SQL fragment.
    type private RenderBuffer(ctx: RenderContext, initial: string) =
        let output = StringBuilder()
        do
            ctx.CheckAppend(0, initial.Length)
            output.Append(initial) |> ignore

        member this.Append(value: string) =
            ctx.CheckAppend(output.Length, value.Length)
            output.Append(value) |> ignore
            this

        member this.Append(value: char) =
            ctx.CheckAppend(output.Length, 1)
            output.Append(value) |> ignore
            this

        override _.ToString() = output.ToString()

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

    // The renderer's Boolean CASE classifier must not recursively consume
    // one CLR call frame per nested CASE result. Keep its original proof:
    // only non-NULL result arms count, and WHEN predicates are not inspected.
    let private isBooleanExpression expression =
        let pending = ResizeArray<Expr>()
        pending.Add expression
        let mutable isBoolean = true
        while isBoolean && pending.Count > 0 do
            let index = pending.Count - 1
            let current = Expr.unspan pending.[index]
            pending.RemoveAt index
            match current with
            | Literal(ScalarValue.Boolean _) -> ()
            | IsNull _ | InList _ | InSubquery _ | Between _
            | Exists _ | Like _ | RegexMatch _ -> ()
            | Unary(UnaryOperator.Not, _) -> ()
            | Binary((BinaryOperator.Equal
                     | BinaryOperator.NotEqual
                     | BinaryOperator.GreaterThan
                     | BinaryOperator.LessThan
                     | BinaryOperator.GreaterThanOrEqual
                     | BinaryOperator.LessThanOrEqual
                     | BinaryOperator.DistinctFrom
                     | BinaryOperator.NotDistinctFrom
                     | BinaryOperator.And
                     | BinaryOperator.Or), _, _) -> ()
            | SimpleCase(_, branches, fallback) ->
                let mutable nonNull = 0
                let push value =
                    match Expr.unspan value with
                    | Literal ScalarValue.Null -> ()
                    | _ ->
                        nonNull <- nonNull + 1
                        pending.Add value
                fallback |> Option.iter push
                NonEmpty.iterReverse (fun (branch: SimpleCaseBranch) ->
                    push branch.Result) branches
                if nonNull = 0 then isBoolean <- false
            | SearchedCase(branches, fallback) ->
                let mutable nonNull = 0
                let push value =
                    match Expr.unspan value with
                    | Literal ScalarValue.Null -> ()
                    | _ ->
                        nonNull <- nonNull + 1
                        pending.Add value
                fallback |> Option.iter push
                NonEmpty.iterReverse (fun (branch: SearchedCaseBranch) ->
                    push branch.Result) branches
                if nonNull = 0 then isBoolean <- false
            | _ -> isBoolean <- false
        isBoolean

    let private renderBooleanTruthValue expression =
        match Expr.unspan expression with
        | Literal(ScalarValue.Boolean true) -> "1"
        | Literal(ScalarValue.Boolean false) -> "0"
        | Literal ScalarValue.Null -> "NULL"
        | _ ->
            raise (SqlCompilationException(
                "Boolean CASE predicates currently require literal TRUE, FALSE, or NULL branch results; richer predicate results remain fail-closed."))

    // Most SQL predicates contain a bound column and a literal, not a
    // recursive scalar graph. Preserve the old allocation-free leaf path
    // instead of constructing a StringBuilder and two frame stacks for
    // each comparison in a long AND/OR chain.
    let private isScalarLeaf expression =
        match Expr.unspan expression with
        | Expr.Column _ | Expr.BoundColumn _ | Expr.Wildcard _
        | Expr.OrderOrdinal _ | Expr.Literal _ | Expr.Interval _ -> true
        | _ -> false

    // SQL fragments are emitted in source order. Text frames and Expr
    // frames share one heap-backed stack, so right-deep and alternating
    // arithmetic / unary / predicate trees do not consume CLR call frames.
    // Distinct field names are required by an F# struct discriminated union.
    [<Struct>]
    type private ScalarRenderFrame =
        | ScalarNode of expression: Expr
        | ScalarText of emittedText: string
        | ScalarRegexCheck
        | ScalarCastComplete of castExpression: Expr
        | ScalarConcatProof of unprovenMessage: string

    // Nested PostgreSQL SET branches share an append-only SQL buffer.
    // Explicit frames avoid a CLR Query -> branch Query recursion and the
    // previous O(depth^2) intermediate SQL materialization.
    type private SetEmitFrame =
        | EnterSetBody of Query
        | EmitSetBranch of SetBranch
        | CloseNestedSet

    let rec private renderExpr (ctx: RenderContext) expression =
        RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
        match expression with
        | Expr.Spanned _ -> renderExpr ctx (Expr.unspan expression)
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
        | Expr.DateAdd(unit, amountExpression, valueExpression) ->
            match SqlDateMathCapabilityRules.TargetValidationError(unit, providerTool ctx.Provider, "CORE_DATE_ADD") with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            let amount = renderExpr ctx amountExpression
            let value = renderExpr ctx valueExpression
            let keyword = SqlDateMathUnit.Keyword unit
            match ctx.Provider with
            | SqlServer ->
                "DATEADD(" + keyword + ", " + amount + ", " + value + ")"
            | MySql ->
                "TIMESTAMPADD(" + keyword + ", " + amount + ", " + value + ")"
            | Firebird ->
                match unit with
                | SqlDateMathUnit.Quarter ->
                    "DATEADD(MONTH, (" + amount + " * 3), " + value + ")"
                | _ ->
                    "DATEADD(" + keyword + ", " + amount + ", " + value + ")"
            | PostgreSql ->
                let interval =
                    match unit with
                    | SqlDateMathUnit.Day -> "INTERVAL '1 day'"
                    | SqlDateMathUnit.Week -> "INTERVAL '1 week'"
                    | SqlDateMathUnit.Month -> "INTERVAL '1 month'"
                    | SqlDateMathUnit.Quarter -> "INTERVAL '3 months'"
                    | SqlDateMathUnit.Year -> "INTERVAL '1 year'"
                    | SqlDateMathUnit.Hour -> "INTERVAL '1 hour'"
                    | SqlDateMathUnit.Minute -> "INTERVAL '1 minute'"
                    | SqlDateMathUnit.Second -> "INTERVAL '1 second'"
                "(" + value + " + (" + interval + " * " + amount + "))"
            | Oracle ->
                let interval =
                    match unit with
                    | SqlDateMathUnit.Year
                    | SqlDateMathUnit.Quarter
                    | SqlDateMathUnit.Month ->
                        invalidOp "Oracle calendar-unit DATEADD reached rendering without a lossless rollover proof."
                    | SqlDateMathUnit.Week ->
                        "NUMTODSINTERVAL((" + amount + " * 7), 'DAY')"
                    | SqlDateMathUnit.Day ->
                        "NUMTODSINTERVAL(" + amount + ", 'DAY')"
                    | SqlDateMathUnit.Hour ->
                        "NUMTODSINTERVAL(" + amount + ", 'HOUR')"
                    | SqlDateMathUnit.Minute ->
                        "NUMTODSINTERVAL(" + amount + ", 'MINUTE')"
                    | SqlDateMathUnit.Second ->
                        "NUMTODSINTERVAL(" + amount + ", 'SECOND')"
                "(" + value + " + " + interval + ")"
            | SQLite ->
                let multiplier, modifier =
                    match unit with
                    | SqlDateMathUnit.Month
                    | SqlDateMathUnit.Quarter
                    | SqlDateMathUnit.Year ->
                        invalidOp "SQLite calendar-unit DATEADD reached rendering without a versioned floor-modifier proof."
                    | SqlDateMathUnit.Week -> 7, "day"
                    | SqlDateMathUnit.Day -> 1, "day"
                    | SqlDateMathUnit.Hour -> 1, "hour"
                    | SqlDateMathUnit.Minute -> 1, "minute"
                    | SqlDateMathUnit.Second -> 1, "second"
                let adjusted =
                    if multiplier = 1 then amount
                    else "(" + amount + " * " + string multiplier + ")"
                "DATETIME(" + value + ", PRINTF('%+d " + modifier + "', " + adjusted + "))"
        | Expr.DateDiff(unit, startExpression, finishExpression) ->
            match SqlDateMathCapabilityRules.TargetValidationError(unit, providerTool ctx.Provider, "CORE_DATE_DIFF") with
            | null -> ()
            | message -> raise (SqlCompilationException(message))
            let keyword = SqlDateMathUnit.Keyword unit
            let startValue = renderExpr ctx startExpression
            let finish = renderExpr ctx finishExpression
            match ctx.Provider with
            | PostgreSql ->
                "(CAST(" + finish + " AS date) - CAST(" + startValue + " AS date))"
            | Oracle ->
                "(CAST(" + finish + " AS DATE) - CAST(" + startValue + " AS DATE))"
            | SQLite ->
                "(JULIANDAY(" + finish + ") - JULIANDAY(" + startValue + "))"
            | SqlServer ->
                "DATEDIFF(" + keyword + ", " + startValue + ", " + finish + ")"
            | MySql ->
                "TIMESTAMPDIFF(" + keyword + ", " + startValue + ", " + finish + ")"
            | Firebird ->
                "DATEDIFF(" + keyword + " FROM " + startValue + " TO " + finish + ")"
        | Expr.Unary _ -> renderScalarGraph ctx expression
        | Expr.Binary((BinaryOperator.And | BinaryOperator.Or), _, _) as chain ->
            renderBooleanGraph ctx renderExpr chain
        | Expr.Binary((BinaryOperator.DistinctFrom | BinaryOperator.NotDistinctFrom as op), left, right)
            when ctx.Provider = Oracle ->
            // Oracle null-safe comparison expands the already rendered
            // operands several times. Cache their SQL text once so bind
            // parameters are NOT allocated twice.
            let leftSql = renderExpr ctx left
            let rightSql = renderExpr ctx right
            let equal =
                "CASE WHEN " + leftSql + " IS NULL THEN CASE WHEN " + rightSql
                + " IS NULL THEN 1 ELSE 0 END WHEN " + rightSql
                + " IS NULL THEN 0 WHEN " + leftSql + " = " + rightSql
                + " THEN 1 ELSE 0 END"
            "(" + equal
            + (if op = BinaryOperator.NotDistinctFrom then " = 1)" else " = 0)")
        | Expr.Binary(operator, left, right)
            when isScalarLeaf left && isScalarLeaf right ->
            // The scalar leaves have no nested render graph, so the original
            // direct binary path remains cheaper and retains the exact
            // provider/parameter semantics.
            let leftSql = renderExpr ctx left
            let rightSql = renderExpr ctx right
            match operator, ctx.Provider, ctx.TargetRuntime with
            | BinaryOperator.Modulo, (Oracle | Firebird), _ ->
                "MOD(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, MySql, _ ->
                "CONCAT(" + leftSql + ", " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Proven NativePipes) ->
                "(" + leftSql + " || " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Proven PlusOperator) ->
                "(" + leftSql + " + " + rightSql + ")"
            | BinaryOperator.Concat, SqlServer, SqlServerRuntime(Unproven message) ->
                invalidOp (
                    "Validated SQL reached rendering without SQL Server concat proof: "
                    + message)
            | BinaryOperator.NotDistinctFrom, MySql, _ ->
                "(" + leftSql + " <=> " + rightSql + ")"
            | BinaryOperator.DistinctFrom, MySql, _ ->
                "NOT (" + leftSql + " <=> " + rightSql + ")"
            | _ ->
                "(" + leftSql + " " + binaryText operator + " " + rightSql + ")"
        | Expr.Binary _ | Expr.Like _ -> renderScalarGraph ctx expression
        | Expr.RawRegexCall _ ->
            invalidOp "Raw REGEXP_LIKE reached rendering before canonicalization."
        | Expr.RegexMatch _ | Expr.PostgresJsonAccess _ ->
            renderScalarGraph ctx expression
        | Expr.FunctionCall call ->
            renderFunction ctx call
        | Expr.FilteredAggregate(value, predicate) ->
            match ctx.Provider with
            | PostgreSql | SQLite | Oracle | Firebird ->
                renderExpr ctx value + " FILTER (WHERE " + renderPredicate ctx predicate + ")"
            | _ -> invalidOp "Aggregate FILTER requires provider-specific lowering before rendering."
        | Expr.Windowed(value, window) -> renderExpr ctx value + " OVER (" + renderWindow ctx window + ")"
        | Expr.Cast _ | Expr.Extract _ -> renderScalarGraph ctx expression
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
        | Expr.InList _ -> renderScalarGraph ctx expression
        | Expr.InSubquery(value, query, negated) ->
            "(" + renderExpr ctx value
            + (if negated then " NOT IN (" else " IN (")
            + renderSubquery ctx query + "))"
        | Expr.Between _ | Expr.IsNull _ ->
            renderScalarGraph ctx expression
        | Expr.ScalarSubquery query ->
            // Materializing the entire inner SELECT at every level was O(n^2)
            // in SQL-string allocations for a pure nested scalar SELECT chain.
            // Preserve provider-specific dummy FROM and bind the innermost
            // literal exactly once, without changing any expression order.
            let mutable current = query
            let mutable wrappers = 0
            let mutable scanning = true
            while scanning do
                let head = current.Head
                let simple =
                    head.Ctes.IsEmpty
                    && head.DistinctMode = SelectDistinct.AllRows
                    && head.From.IsNone && head.Joins.IsEmpty
                    && head.Where.IsNone && head.GroupBy.IsEmpty
                    && head.Having.IsNone
                    && current.SetOperations.IsEmpty && not (hasTail current)
                if not simple then scanning <- false
                else
                    match head.Projection with
                    | [ { Alias = None; Expression = value } ] ->
                        match Expr.unspan value with
                        | Expr.ScalarSubquery child ->
                            wrappers <- wrappers + 1
                            current <- child
                        | _ -> scanning <- false
                    | _ -> scanning <- false

            if wrappers = 0 then "(" + renderSubquery ctx query + ")"
            else
                let dummySource =
                    match ctx.Provider with
                    | Oracle -> " FROM DUAL"
                    | Firebird -> " FROM RDB$DATABASE"
                    | _ -> ""
                let builder = RenderBuffer(ctx, "")
                builder.Append('(') |> ignore
                for _ in 1 .. wrappers do builder.Append("SELECT (") |> ignore
                builder.Append(renderSubquery ctx current) |> ignore
                for _ in 1 .. wrappers do
                    builder.Append(')').Append(dummySource) |> ignore
                builder.Append(')') |> ignore
                builder.ToString()
        | Expr.Exists(query, negated) ->
            renderExistsQueryChain ctx query negated

    // A SELECT-with-EXISTS-WHERE chain forms one SQL output stream, not
    // one completed SQL string per enclosing Query. Materializing every
    // inner SELECT first causes quadratic allocations for mixed Expr/Query
    // nesting even after Binder and Normalizer have become iterative.
    //
    // Restrict this fast path to inert, single-leaf SELECTs so every dialect
    // keeps its exact projection parameter and dummy-FROM order. Any CTE,
    // set branch, paging tail, non-EXISTS predicate, or richer SELECT returns
    // to the original provider-aware renderSubquery path.
    and private renderExistsQueryChain (ctx: RenderContext) query negated =
        let dummyFrom =
            match ctx.Provider with
            | Oracle -> " FROM DUAL"
            | Firebird -> " FROM RDB$DATABASE"
            | _ -> ""
        let tryWrapper (current: Query) =
            let head = current.Head
            if not head.Ctes.IsEmpty
               || head.DistinctMode <> SelectDistinct.AllRows
               || head.From.IsSome || not head.Joins.IsEmpty
               || not head.GroupBy.IsEmpty || head.Having.IsSome
               || not current.SetOperations.IsEmpty || hasTail current then
                None
            else
                match head.Projection, head.Where with
                | [ { Alias = None; Expression = projected } ], Some predicate ->
                    match Expr.unspan projected, Expr.unspan predicate with
                    | (Literal _ | Column _ | BoundColumn _),
                      Exists(child, isNegated) ->
                        Some(projected, child, isNegated)
                    | _ -> None
                | _ -> None
        // Ordinary EXISTS has no wrappers: don't allocate a builder or
        // per-query continuation state just to surround one subquery.
        if Option.isNone (tryWrapper query) then
            (if negated then "NOT EXISTS (" else "EXISTS (")
            + renderSubquery ctx query + ")"
        else
            let mutable current = query
            let mutable currentNegated = negated
            let mutable wrapping = true
            let mutable levels = 0
            let output = RenderBuffer(ctx, "")
            while wrapping do
                match tryWrapper current with
                | Some(projected, child, innerNegated) ->
                    RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
                    output.Append(if currentNegated then "NOT EXISTS (" else "EXISTS (")
                          .Append("SELECT ")
                          .Append(renderExpr ctx projected)
                          .Append(dummyFrom)
                          .Append(" WHERE ") |> ignore
                    levels <- levels + 1
                    current <- child
                    currentNegated <- innerNegated
                | None -> wrapping <- false
            output.Append(if currentNegated then "NOT EXISTS (" else "EXISTS (")
                  .Append(renderSubquery ctx current)
                  .Append(')') |> ignore
            for _ in 1 .. levels do output.Append(')') |> ignore
            output.ToString()

    and private renderPredicate (ctx: RenderContext) expression =
        match ctx.Provider with
        | Oracle | SqlServer ->
            match expression with
            | Spanned(_, inner) -> renderPredicate ctx inner
            | Literal(ScalarValue.Boolean true) -> "(1 = 1)"
            | Literal(ScalarValue.Boolean false) -> "(1 = 0)"
            | Unary(UnaryOperator.Not, operand) ->
                "NOT (" + renderPredicate ctx operand + ")"
            | Binary((BinaryOperator.And | BinaryOperator.Or), _, _) as chain ->
                renderBooleanGraph ctx renderPredicate chain
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

    // A Boolean subtree can be left-deep, right-deep or balanced. Each
    // pending binary uses ONE struct frame: after rendering its left child,
    // append its operator; after rendering its right child, close exactly
    // that parent's parentheses. No node reordering, Boolean reassociation,
    // per-node option allocations or managed recursion is needed.
    and private renderBooleanGraph
        (ctx: RenderContext)
        (renderLeaf: RenderContext -> Expr -> string)
        expression =
        let pending = ResizeArray<struct (BinaryOperator * Expr * bool)>()
        let builder = RenderBuffer(ctx, "")
        let mutable current = expression
        let mutable visiting = true
        while visiting || pending.Count > 0 do
            if visiting then
                match Expr.unspan current with
                | Binary((BinaryOperator.And | BinaryOperator.Or as op), left, right) ->
                    RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
                    builder.Append('(') |> ignore
                    pending.Add(struct (op, right, false))
                    current <- left
                | _ ->
                    builder.Append(renderLeaf ctx current) |> ignore
                    visiting <- false
            else
                let last = pending.Count - 1
                let struct (op, right, leftFinished) = pending.[last]
                if leftFinished then
                    pending.RemoveAt last
                    builder.Append(')') |> ignore
                else
                    pending.[last] <- struct (op, right, true)
                    builder.Append(' ').Append(binaryText op).Append(' ') |> ignore
                    current <- right
                    visiting <- true
        builder.ToString()

    // Preserve exact nested parentheses and left-to-right parameter
    // binding while avoiding depth-proportional recursion and intermediate
    // SQL prefixes for left-deep arithmetic trees. Complex operators (e.g.
    // MOD, CONCAT, IS DISTINCT FROM) continue through their dialect-aware
    // renderer instead of being incorrectly treated as infix arithmetic.
    // Emit the compositional scalar grammar into ONE StringBuilder. The
    // old renderExpr recursively entered itself through every unary,
    // right-nested binary, CAST, LIKE, BETWEEN and IN-list node. Arithmetic
    // is not flattened or reassociated: parentheses are emitted exactly
    // as for the recursive implementation, including dialect-specific
    // MOD/CONCAT/NULL-safe forms and left-to-right parameter binding.
    and private renderScalarGraph (ctx: RenderContext) root =
        let pending = ResizeArray<ScalarRenderFrame>()
        let output = RenderBuffer(ctx, "")
        let inline node value = pending.Add(ScalarNode value)
        let inline text (value: string) = pending.Add(ScalarText value)
        let append (value: string) = output.Append(value) |> ignore
        let wrap prefix suffix child =
            append prefix
            text suffix
            node child
        let binary prefix separator suffix left right =
            append prefix
            text suffix
            node right
            text separator
            node left
        node root
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt last
            match frame with
            | ScalarText fragment -> append fragment
            | ScalarRegexCheck ->
                match ctx.Provider with
                | SQLite | Firebird ->
                    invalidOp (capabilityError ctx.Provider "function.regex_match")
                | _ -> ()
            | ScalarConcatProof message ->
                invalidOp (
                    "Validated SQL reached rendering without SQL Server concat proof: "
                    + message)
            | ScalarCastComplete original ->
                match Expr.unspan original with
                | Expr.Cast(_, targetType) ->
                    append (
                        " AS "
                        + RewriteCastTypes.renderTarget
                            (providerTool ctx.Provider) targetType
                        + ")")
                | _ -> invalidOp "Scalar CAST completion frame lost its source AST."
            | ScalarNode original ->
                RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
                match Expr.unspan original with
                | Expr.Unary(UnaryOperator.Not, operand) ->
                    wrap "NOT (" ")" operand
                | Expr.Unary(UnaryOperator.Negate, operand) ->
                    wrap "(-" ")" operand
                | Expr.Unary(UnaryOperator.Positive, operand) ->
                    wrap "(+" ")" operand
                | Expr.Binary((BinaryOperator.DistinctFrom
                              | BinaryOperator.NotDistinctFrom), _, _)
                    when ctx.Provider = Oracle ->
                    // Oracle expands operands more than once; renderExpr
                    // caches them in the special-case branch above.
                    append (renderExpr ctx original)
                | Expr.Binary(op, left, right) ->
                    match op, ctx.Provider, ctx.TargetRuntime with
                    | BinaryOperator.Modulo, (Oracle | Firebird), _ ->
                        binary "MOD(" ", " ")" left right
                    | BinaryOperator.Concat, MySql, _ ->
                        binary "CONCAT(" ", " ")" left right
                    | BinaryOperator.Concat, SqlServer,
                        SqlServerRuntime(Proven NativePipes) ->
                        binary "(" " || " ")" left right
                    | BinaryOperator.Concat, SqlServer,
                        SqlServerRuntime(Proven PlusOperator) ->
                        binary "(" " + " ")" left right
                    | BinaryOperator.Concat, SqlServer,
                        SqlServerRuntime(Unproven message) ->
                        // A nonportable concat proof originally fails AFTER
                        // its left and right operands have been rendered.
                        pending.Add(ScalarConcatProof message)
                        binary "(" " + " ")" left right
                    | BinaryOperator.NotDistinctFrom, MySql, _ ->
                        binary "(" " <=> " ")" left right
                    | BinaryOperator.DistinctFrom, MySql, _ ->
                        binary "NOT (" " <=> " ")" left right
                    | _ ->
                        binary "(" (" " + binaryText op + " ") ")" left right
                | Expr.IsNull(value, negated) ->
                    wrap "(" (if negated then " IS NOT NULL)" else " IS NULL)") value
                | Expr.Between(value, lower, upper, negated) ->
                    append "("
                    text ")"
                    node upper
                    text " AND "
                    node lower
                    text (if negated then " NOT BETWEEN " else " BETWEEN ")
                    node value
                | Expr.InList(value, items, negated) ->
                    append "("
                    text "))"
                    let entries = NonEmpty.toList items |> List.toArray
                    for index = entries.Length - 1 downto 0 do
                        node entries.[index]
                        if index > 0 then text ", "
                    text (if negated then " NOT IN (" else " IN (")
                    node value
                | Expr.Like(value, pattern, escape, negated, insensitive) ->
                    if insensitive && ctx.Provider <> PostgreSql then
                        invalidOp (capabilityError ctx.Provider "operator.ilike")
                    append (if negated then "NOT ((" else "(")
                    text (if negated then "))" else ")")
                    text (escape
                          |> Option.map (fun value ->
                              " ESCAPE " + renderLikeEscape ctx.Provider value)
                          |> Option.defaultValue "")
                    node pattern
                    text (if insensitive then " ILIKE " else " LIKE ")
                    node value
                | Expr.RegexMatch(value, pattern) ->
                    match ctx.Provider with
                    | PostgreSql ->
                        binary "(" " ~ " ")" value pattern
                    | MySql | Oracle | SqlServer ->
                        binary "REGEXP_LIKE(" ", " ")" value pattern
                    | SQLite | Firebird ->
                        // Historically target checks happen after both
                        // operand subtrees are rendered.
                        pending.Add ScalarRegexCheck
                        binary "REGEXP_LIKE(" ", " ")" value pattern
                | Expr.PostgresJsonAccess(value, selector, resultKind) ->
                    if ctx.Provider <> PostgreSql then
                        invalidOp (capabilityError ctx.Provider "json.operator.postgres_arrow")
                    // The legacy renderer binds the selector first, then
                    // visits the value expression. Retain that exact order.
                    let selectorSql =
                        match selector with
                        | PostgresJsonProperty key -> ctx.Bind(box key)
                        | PostgresJsonArrayIndex index -> ctx.Bind(box index)
                    let operator =
                        match resultKind with
                        | JsonResult -> "->"
                        | TextResult -> "->>"
                    wrap "(" (" " + operator + " " + selectorSql + ")") value
                | Expr.Cast(value, _) ->
                    // CAST target validation/rendering runs only after its
                    // operand, retaining the previous diagnostic precedence.
                    append "CAST("
                    pending.Add(ScalarCastComplete original)
                    node value
                | Expr.Extract(field, value) ->
                    let part =
                        ExtractField.value field
                        |> fun value -> value.Trim().ToUpperInvariant()
                    match SqlDatePartCapabilityRules.TargetValidationError(
                              part, providerTool ctx.Provider) with
                    | null -> ()
                    | message -> raise (SqlCompilationException(message))
                    match ctx.Provider with
                    | SqlServer ->
                        wrap ("DATEPART(" + part + ", ") ")" value
                    | MySql ->
                        wrap (part + "(") ")" value
                    | PostgreSql | Oracle ->
                        wrap ("EXTRACT(" + part + " FROM ") ")" value
                    | Firebird ->
                        wrap ("EXTRACT(" + part + " FROM CAST(") " AS DATE))" value
                    | SQLite ->
                        let fmt =
                            match part with
                            | "YEAR" -> "%Y" | "MONTH" -> "%m"
                            | "DAY" -> "%d" | "HOUR" -> "%H"
                            | "MINUTE" -> "%M" | "SECOND" -> "%S"
                            | "QUARTER" -> "%m"
                            | _ ->
                                raise (SqlCompilationException(
                                    "SQLite does not support date part " + part + "."))
                        let prefix =
                            if part = "QUARTER" then
                                "CAST(((CAST(STRFTIME('" + fmt + "', "
                            else "CAST(STRFTIME('" + fmt + "', "
                        let suffix =
                            if part = "QUARTER" then
                                ") AS INTEGER) + 2) / 3) AS INTEGER)"
                            else ") AS INTEGER)"
                        wrap prefix suffix value
                | _ ->
                    // Functions, CASE, windows and Query bodies still have
                    // their specialized semantic/parameter pathways. Only
                    // non-compositional leaves are delegated.
                    append (renderExpr ctx original)
        output.ToString()

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

        let literalText label expression =
            match Expr.unspan expression with
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
            let unit =
                literalKeyword "DATEADD unit" call.Arguments[0]
                |> fun value -> SqlDateMathUnit.Parse(value, "DATEADD")
            renderExpr ctx (DateAdd(unit, call.Arguments[1], call.Arguments[2]))

        | "CORE_DATE_DIFF" ->
            requireCount 3
            let unit =
                literalKeyword "DATEDIFF unit" call.Arguments[0]
                |> fun value -> SqlDateMathUnit.Parse(value, "DATEDIFF")
            renderExpr ctx (DateDiff(unit, call.Arguments[1], call.Arguments[2]))

        | "CORE_DATE_PART" ->
            requireCount 2
            let part = literalKeyword "date part" call.Arguments[0]
            renderExpr ctx (Extract(ExtractField.create part, call.Arguments[1]))

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
            let parsedPath =
                match JsonPath.tryParse path with
                | Ok value -> value
                | Error _ ->
                    fail (
                        "JSON path '" + path + "' is outside the portable Core property/array-index subset. "
                        + "SQL capability 'json.path.property_chain' is not supported by provider "
                        + string tool + " for this Core plan.")
            if canonical = "CORE_JSON_SET" && JsonPath.hasArrayIndex parsedPath then
                fail (capabilityError ctx.Provider "json.path.mutation_array_index")
            let value = renderExpr ctx call.Arguments[0]
            let pathExpr () = renderExpr ctx call.Arguments[1]
            if canonical = "CORE_JSON_EXTRACT" then
                match ctx.Provider with
                | MySql
                | SQLite ->
                    "JSON_EXTRACT(" + value + ", " + pathExpr () + ")"
                | PostgreSql ->
                    let placeholders =
                        JsonPath.postgresSegments parsedPath
                        |> List.map (fun segment -> ctx.Bind(box segment))
                        |> String.concat ", "
                    "JSONB_EXTRACT_PATH(CAST(" + value + " AS jsonb), " + placeholders + ")"
                | _ -> fail "JSON_EXTRACT is not supported losslessly by this provider."
            else
                let newValue =
                    match ctx.Provider with
                    | PostgreSql ->
                        let pgPathPlaceholder = ctx.Bind(box (JsonPath.postgresTextArray parsedPath))
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

    and private renderCtes (ctx: RenderContext) (ctes: Cte list) =
        if List.isEmpty ctes then ""
        else
            let recursiveScope = ctes |> List.exists (fun cte -> cte.RecursiveScope)
            if recursiveScope then
                let provider = providerTool ctx.Provider
                if not (SqlRecursiveCteCapabilityRules.SupportsWithRecursiveSyntax(provider)) then
                    raise (SqlCompilationException(
                        "SQL capability 'select.recursive_cte' is not supported by target provider "
                        + string provider + "; this provider does not use the modeled WITH RECURSIVE syntax contract."))
            let result = RenderBuffer(ctx, "WITH ")
            if recursiveScope then result.Append("RECURSIVE ") |> ignore
            ctes
            |> List.iteri (fun index cte ->
                if index > 0 then result.Append(", ") |> ignore
                result.Append(renderAlias ctx.Provider cte.Name).Append(" AS (") |> ignore

                // Streaming one-CTE nesting is crucial: each nested CTE body
                // used to render to a complete string before its parent,
                // allocating O(depth^2) SQL text. Keep Query tails, SET,
                // recursive scopes and multi-CTE bodies on the full path.
                let nested = ResizeArray<Query>()
                let mutable current = cte.Query
                let mutable walking = true
                while walking do
                    RewriteResourceBudget.checkpoint ()
                    match current.Head.Ctes with
                    | [ innerCte ] when not innerCte.RecursiveScope
                                        && current.SetOperations.IsEmpty
                                        && not (hasTail current) ->
                        result.Append("WITH ")
                              .Append(renderAlias ctx.Provider innerCte.Name)
                              .Append(" AS (") |> ignore
                        nested.Add(current)
                        current <- innerCte.Query
                    | _ -> walking <- false

                result.Append((renderCteQuery ctx current) : string) |> ignore
                for nesting = nested.Count - 1 downto 0 do
                    result.Append(") ") |> ignore
                    let query = nested.[nesting]
                    result.Append(
                        (renderSelectBody ctx { query.Head with Ctes = [] }) : string)
                    |> ignore
                result.Append(')') |> ignore)
            result.Append(' ').ToString()

    and private renderSource (ctx: RenderContext) source =
        match source with
        | TableSource.NamedTable(identifier, alias) | TableSource.CteTable(identifier, alias) ->
            renderIdentifier ctx.Provider identifier + (alias |> Option.map (fun value -> tableAliasPrefix ctx.Provider + renderAlias ctx.Provider value) |> Option.defaultValue "")
        | TableSource.DerivedTable(query, alias) ->
            renderDerivedSourceChain ctx query alias false
        | TableSource.LateralDerivedTable(query, alias) ->
            match SqlLateralDerivedTableCapabilityRules.TargetValidationError(providerTool ctx.Provider, null) with
            | null -> renderDerivedSourceChain ctx query alias true
            | message -> raise (SqlCompilationException(message))

    // Outside-in emission preserves projection binding and provider alias
    // syntax without recursively allocating a string at every nested Query.
    and private renderDerivedSourceChain (ctx: RenderContext) query alias lateral =
        let tryWrapper (current: Query) =
            let head = current.Head
            // TOP/FIRST/SKIP need prefix rewriting; PostgreSQL, MySQL,
            // SQLite and Oracle tails are suffixes and can stream safely.
            let tailIsSuffix =
                not (hasTail current)
                || (match ctx.Provider with
                    | PostgreSql | MySql | SQLite | Oracle -> true
                    | SqlServer | Firebird -> false)
            if not head.Ctes.IsEmpty
               || head.DistinctMode <> SelectDistinct.AllRows
               || not head.Joins.IsEmpty
               || not head.GroupBy.IsEmpty || head.Having.IsSome
               || not current.SetOperations.IsEmpty || not tailIsSuffix then
                None
            else
                match head.From with
                | Some (TableSource.DerivedTable(inner, innerAlias)) ->
                    Some(head.Projection, inner, innerAlias)
                | _ -> None
        match tryWrapper query with
        | None ->
            (if lateral then "LATERAL (" else "(")
            + renderQuery ctx query + ")"
            + tableAliasPrefix ctx.Provider + renderAlias ctx.Provider alias
        | Some _ ->
            let result = RenderBuffer(ctx, if lateral then "LATERAL (" else "(")
            let wrappers = ResizeArray<struct (IdentifierPart * Query)>()
            let mutable current = query
            let mutable scanning = true
            while scanning do
                RewriteResourceBudget.checkpoint ()
                match tryWrapper current with
                | None -> scanning <- false
                | Some(projection, inner, childAlias) ->
                    result.Append("SELECT ") |> ignore
                    projection
                    |> List.iteri (fun index item ->
                        if index > 0 then result.Append(", ") |> ignore
                        result.Append(renderExpr ctx item.Expression) |> ignore
                        item.Alias
                        |> Option.iter (fun itemAlias ->
                            result.Append(" AS ").Append(renderAlias ctx.Provider itemAlias) |> ignore))
                    result.Append(" FROM (") |> ignore
                    wrappers.Add(struct (childAlias, current))
                    current <- inner
            result.Append(renderQuery ctx current) |> ignore
            for index = wrappers.Count - 1 downto 0 do
                let struct (childAlias, wrapper) = wrappers.[index]
                result.Append(')')
                      .Append(tableAliasPrefix ctx.Provider)
                      .Append(renderAlias ctx.Provider childAlias) |> ignore
                wrapper.Head.Where
                |> Option.iter (fun predicate ->
                    result.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
                result.Append((renderOrderClause ctx false wrapper.OrderBy) : string) |> ignore
                if hasTail wrapper then
                    // Only providers with a suffix-only paging contract
                    // can reach here; passing "" avoids copying a prefix.
                    result.Append((renderPaging ctx wrapper "") : string) |> ignore
            result.Append(')')
                  .Append(tableAliasPrefix ctx.Provider)
                  .Append(renderAlias ctx.Provider alias)
                  .ToString()

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
        // Projection and DISTINCT ON expressions bind before FROM/JOIN
        // exactly as before. Build the text once instead of copying the
        // complete SELECT prefix for every JOIN, WHERE, GROUP and HAVING.
        let sql = RenderBuffer(ctx, "SELECT ")
        sql.Append(distinctSql).Append(projection) |> ignore
        select.From
        |> Option.iter (fun source ->
            sql.Append(" FROM ").Append(renderSource ctx source) |> ignore)
        if select.From.IsNone then
            match ctx.Provider with
            | Oracle -> sql.Append(" FROM DUAL") |> ignore
            | Firebird -> sql.Append(" FROM RDB$DATABASE") |> ignore
            | _ -> ()
        for join in select.Joins do
            let joinSql =
                match join with
                | NaturalJoin(kind, _) ->
                    match SqlNaturalJoinCapabilityRules.TargetValidationError(providerTool ctx.Provider) with
                    | null -> naturalJoinText kind
                    | message -> raise (SqlCompilationException(message))
                | _ -> joinText join.Kind
            sql.Append(" ").Append(joinSql).Append(" ")
               .Append(renderSource ctx join.Source) |> ignore
            match join with
            | CrossJoin _
            | NaturalJoin _ -> ()
            | OnJoin(_, _, predicate) ->
                sql.Append(" ON ").Append(renderPredicate ctx predicate) |> ignore
            | UsingJoin(_, _, columns) ->
                match SqlUsingJoinCapabilityRules.TargetValidationError(providerTool ctx.Provider) with
                | null -> ()
                | message -> raise (SqlCompilationException(message))
                let names =
                    columns
                    |> NonEmpty.toList
                    |> List.map (renderAlias ctx.Provider)
                    |> String.concat ", "
                sql.Append(" USING (").Append(names).Append(")") |> ignore
        select.Where
        |> Option.iter (fun predicate ->
            sql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
        if not select.GroupBy.IsEmpty then
            let grouped =
                select.GroupBy
                |> List.mapi (fun index expression ->
                    if ctx.Provider = PostgreSql then
                        ctx.WithSharedBindings(
                            "postgres-group:" + string index,
                            fun () -> renderExpr ctx expression)
                    else
                        renderExpr ctx expression)
                |> String.concat ", "
            sql.Append(" GROUP BY ").Append(grouped) |> ignore
        select.Having
        |> Option.iter (fun predicate ->
            sql.Append(" HAVING ").Append(renderPredicate ctx predicate) |> ignore)
        sql.ToString()

    and private renderSetBody (ctx: RenderContext) (query: Query) =
        let sql = RenderBuffer(ctx, "")
        let pending = ResizeArray<SetEmitFrame>()
        pending.Add(EnterSetBody query)
        while pending.Count > 0 do
            RewriteResourceBudget.checkpoint ()
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | EnterSetBody current ->
                sql.Append(renderSelectBody ctx { current.Head with Ctes = [] }) |> ignore
                for branch in List.rev current.SetOperations do
                    pending.Add(EmitSetBranch branch)
            | EmitSetBranch branch ->
                let branchNoTail =
                    { branch.Query with
                        OrderBy = []
                        Limit = None
                        Offset = None
                        FetchPercent = None
                        FetchWithTies = false }
                sql.Append(" ").Append(setText branch.Operator).Append(" ") |> ignore
                if ctx.Provider = PostgreSql
                   && not branchNoTail.SetOperations.IsEmpty
                   && branchNoTail.Head.Ctes.IsEmpty then
                    sql.Append('(') |> ignore
                    pending.Add(CloseNestedSet)
                    pending.Add(EnterSetBody branchNoTail)
                elif ctx.Provider = PostgreSql
                     && not branchNoTail.SetOperations.IsEmpty then
                    // Preserve the full WITH/SET contract for the uncommon
                    // CTE-bearing branch; do not lower away capability proof.
                    sql.Append("(").Append((renderQueryCore ctx branchNoTail) : string)
                       .Append(')') |> ignore
                elif branchNoTail.Head.Ctes.IsEmpty then
                    sql.Append((renderQueryCore ctx branchNoTail) : string) |> ignore
                else
                    sql.Append("SELECT * FROM (")
                       .Append((renderQueryCore ctx branchNoTail) : string)
                       .Append(") AS ")
                       .Append(renderAlias ctx.Provider
                           { Value = "_set_branch"
                             WasQuoted = false
                             PreserveSpelling = false
                             Span = { Start = 0; Length = 0 } }) |> ignore
            | CloseNestedSet -> sql.Append(')') |> ignore
        sql.ToString()

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

        // OFFSET wrapping used to copy the entire growing SELECT prefix for
        // every JOIN/WHERE/GROUP/HAVING. Append once, with a pre-allocation cap.
        let baseSql = RenderBuffer(ctx, "SELECT ")
        if query.Head.Distinct then baseSql.Append("DISTINCT ") |> ignore
        baseSql.Append(baseProjection |> Seq.toList |> String.concat ", ") |> ignore
        query.Head.From
        |> Option.iter (fun source ->
            baseSql.Append(" FROM ").Append(renderSource ctx source) |> ignore)
        for join in query.Head.Joins do
            baseSql.Append(" ").Append(joinText join.Kind).Append(" ")
                   .Append(renderSource ctx join.Source) |> ignore
            match join with
            | CrossJoin _ -> ()
            | NaturalJoin _ ->
                invalidOp "NATURAL JOIN cannot enter SQL Server pagination lowering without an explicit target capability."
            | OnJoin(_, _, predicate) ->
                baseSql.Append(" ON ").Append(renderPredicate ctx predicate) |> ignore
            | UsingJoin(_, _, _) ->
                invalidOp "JOIN USING cannot enter SQL Server pagination lowering without an explicit target capability."
        query.Head.Where
        |> Option.iter (fun predicate ->
            baseSql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
        if not query.Head.GroupBy.IsEmpty then
            baseSql.Append(" GROUP BY ")
                   .Append(query.Head.GroupBy |> List.map (renderExpr ctx) |> String.concat ", ") |> ignore
        query.Head.Having
        |> Option.iter (fun predicate ->
            baseSql.Append(" HAVING ").Append(renderPredicate ctx predicate) |> ignore)

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
            + baseSql.ToString()
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
        RewriteStackSafety.compile SqlDiagnosticStage.RenderingInvariant
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

    let private renderSqlServerOutput image (ctx: RenderContext) (items: ReturningItem list) =
        if List.isEmpty items then ""
        else
            let renderItem = function
                | ReturningColumn(identifier, alias) ->
                    if Identifier.parts identifier |> List.length <> 1 then
                        invalidOp "Validated SQL Server OUTPUT requires target-only single-part columns."
                    image
                    + "."
                    + renderIdentifier SqlServer identifier
                    + (alias
                       |> Option.map (fun value -> " AS " + renderAlias SqlServer value)
                       |> Option.defaultValue "")
                | ReturningWildcard None -> image + ".*"
                | ReturningWildcard(Some _) ->
                    invalidOp "Validated SQL Server OUTPUT wildcard cannot be aliased."
                | ReturningExpression _ ->
                    invalidOp "Validated SQL Server OUTPUT does not admit rich expressions."
            " OUTPUT " + (items |> List.map renderItem |> String.concat ", ")

    let private renderTrailingReturning (ctx: RenderContext) items =
        if ctx.Provider = SqlServer then "" else renderReturning ctx items

    let private conflictArithmeticText = function
        | ConflictAdd -> "+"
        | ConflictSubtract -> "-"
        | ConflictMultiply -> "*"
        | ConflictDivide -> "/"
        | ConflictModulo -> "%"

    [<Struct>]
    type private ConflictRenderFrame =
        | ConflictNode of conflictValue: ConflictValue
        | ConflictText of emittedText: string

    let private renderConflictValue (ctx: RenderContext) (proposedQualifier: string) (value: ConflictValue) =
        // Explicit preorder emission preserves left-to-right parameter
        // binding for nested conflict arithmetic without consuming a CLR
        // call frame for every ConflictBinary/ConflictNegate.
        let pending = ResizeArray<ConflictRenderFrame>()
        let output = RenderBuffer(ctx, "")
        pending.Add(ConflictNode value)
        while pending.Count > 0 do
            let last = pending.Count - 1
            let frame = pending.[last]
            pending.RemoveAt(last)
            match frame with
            | ConflictText text -> output.Append(text) |> ignore
            | ConflictNode node ->
                match node with
                | ConflictProposedColumn identifier ->
                    output.Append(proposedQualifier).Append(".").Append(renderIdentifier ctx.Provider identifier)
                    |> ignore
                | ConflictTargetColumn identifier ->
                    output.Append(renderIdentifier ctx.Provider identifier) |> ignore
                | ConflictLiteral scalar ->
                    output.Append(renderExpr ctx (Expr.Literal scalar)) |> ignore
                | ConflictNegate operand ->
                    output.Append("(-") |> ignore
                    pending.Add(ConflictText ")")
                    pending.Add(ConflictNode operand)
                | ConflictBinary(operator, left, right) ->
                    output.Append("(") |> ignore
                    pending.Add(ConflictText ")")
                    pending.Add(ConflictNode right)
                    pending.Add(ConflictText(" " + conflictArithmeticText operator + " "))
                    pending.Add(ConflictNode left)
        output.ToString()

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
                    let values =
                        assignments
                        |> NonEmpty.toList
                        |> List.map (fun assignment ->
                            renderIdentifier ctx.Provider assignment.Target
                            + " = "
                            + renderConflictValue ctx "EXCLUDED" assignment.Value)
                        |> String.concat ", "
                    " ON CONFLICT" + targetClause + " DO UPDATE SET " + values
            | _ -> invalidOp "Portable INSERT conflict lowering is not supported by the target provider."

    // Row-major VALUES rendering must not allocate one intermediate SQL
    // string per row and a second aggregate concatenation. All writes check
    // the bound before StringBuilder growth, including conflict/RETURNING.
    let private appendValues (ctx: RenderContext) (output: RenderBuffer) rows =
        rows
        |> NonEmpty.toList
        |> List.iteri (fun rowIndex row ->
            if rowIndex > 0 then output.Append(", ") |> ignore
            output.Append('(') |> ignore
            row
            |> NonEmpty.toList
            |> List.iteri (fun columnIndex value ->
                if columnIndex > 0 then output.Append(", ") |> ignore
                output.Append(renderExpr ctx value) |> ignore)
            output.Append(')') |> ignore)

    let private renderInsertValues (ctx: RenderContext) (prefix: string) rows conflict returning =
        let result = RenderBuffer(ctx, prefix)
        result.Append(" VALUES ") |> ignore
        appendValues ctx result rows
        result.Append(renderConflict ctx conflict)
              .Append(renderTrailingReturning ctx returning).ToString()

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
            "UPDATE OR INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns + " VALUES (" + values + ") MATCHING (" + targets + ")" + renderTrailingReturning ctx insert.Returning
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
                        let proposed =
                            match ConflictValue.trySimpleProposed assignment.Value with
                            | Some identifier -> identifier
                            | None ->
                                invalidOp "Validated MySQL conflict lowering admits only direct proposed-row assignments."
                        renderIdentifier MySql assignment.Target
                        + " = "
                        + alias
                        + "."
                        + renderIdentifier MySql proposed)
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
                let result = RenderBuffer(ctx, "INSERT ALL")
                rows |> NonEmpty.toList |> List.iter (fun row ->
                    result.Append(" INTO ").Append(table).Append(columns)
                          .Append(" VALUES (") |> ignore
                    row |> NonEmpty.toList |> List.iteri (fun index value ->
                        if index > 0 then result.Append(", ") |> ignore
                        result.Append(renderExpr ctx value) |> ignore)
                    result.Append(')') |> ignore)
                result.Append(" SELECT 1 FROM DUAL")
                      .Append(renderTrailingReturning ctx insert.Returning).ToString()
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                prefix + " " + withClause + source + renderTrailingReturning ctx insert.Returning
            | QuerySource query -> prefix + " " + renderQuery ctx query + renderTrailingReturning ctx insert.Returning
            | Values rows ->
                renderInsertValues ctx prefix rows None insert.Returning
            | DefaultValues -> prefix + " DEFAULT VALUES" + renderTrailingReturning ctx insert.Returning
        | Firebird, None ->
            let prefix = "INSERT INTO " + renderIdentifier ctx.Provider insert.Target + columns
            match insert.Input with
            | Values rows when NonEmpty.length rows > 1 ->
                let result = RenderBuffer(ctx, prefix + " ")
                rows |> NonEmpty.toList |> List.iteri (fun rowIndex row ->
                    if rowIndex > 0 then result.Append(" UNION ALL ") |> ignore
                    result.Append("SELECT ") |> ignore
                    row |> NonEmpty.toList |> List.iteri (fun index value ->
                        if index > 0 then result.Append(", ") |> ignore
                        result.Append(renderExpr ctx value) |> ignore)
                    result.Append(" FROM RDB$DATABASE") |> ignore)
                result.Append(renderTrailingReturning ctx insert.Returning).ToString()
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                prefix + " " + withClause + source + renderTrailingReturning ctx insert.Returning
            | QuerySource query -> prefix + " " + renderQuery ctx query + renderTrailingReturning ctx insert.Returning
            | Values rows ->
                renderInsertValues ctx prefix rows None insert.Returning
            | DefaultValues -> prefix + " DEFAULT VALUES" + renderTrailingReturning ctx insert.Returning
        | _ ->
            let prefix =
                "INSERT INTO "
                + renderIdentifier ctx.Provider insert.Target
                + columns
                + (if ctx.Provider = SqlServer then
                       renderSqlServerOutput "INSERTED" ctx insert.Returning
                   else
                       "")
            match insert.Input with
            | QuerySource query when not query.Head.Ctes.IsEmpty ->
                let withClause = renderCtes ctx query.Head.Ctes
                let source = renderQuery ctx { query with Head = { query.Head with Ctes = [] } }
                match ctx.Provider with
                | PostgreSql | SqlServer | SQLite ->
                    withClause + prefix + " " + source
                    + renderConflict ctx insert.Conflict + renderTrailingReturning ctx insert.Returning
                | MySql ->
                    prefix + " " + withClause + source
                    + renderConflict ctx insert.Conflict + renderTrailingReturning ctx insert.Returning
                | Oracle | Firebird ->
                    invalidOp "Provider-specific INSERT ... SELECT path was not selected."
            | QuerySource query ->
                prefix + " " + renderQuery ctx query
                + renderConflict ctx insert.Conflict + renderTrailingReturning ctx insert.Returning
            | Values rows ->
                renderInsertValues ctx prefix rows insert.Conflict insert.Returning
            | DefaultValues ->
                prefix + " DEFAULT VALUES"
                + renderConflict ctx insert.Conflict + renderTrailingReturning ctx insert.Returning

    let private renderUpdate (ctx: RenderContext) (update: Update) =
        let assignments = update.Assignments |> List.map (fun (assignment: Assignment) -> renderIdentifier ctx.Provider assignment.Target + " = " + renderExpr ctx assignment.Value) |> String.concat ", "
        let targetAlias =
            match update.TargetAlias with
            | None -> ""
            | Some alias when ctx.Provider = PostgreSql || ctx.Provider = Firebird ->
                " AS " + renderAlias ctx.Provider alias
            | Some _ -> invalidOp "DML target aliases are not supported by the target provider."
        let sql = RenderBuffer(ctx,
            "UPDATE "
            + renderIdentifier ctx.Provider update.Target
            + targetAlias
            + " SET "
            + assignments
            + (if ctx.Provider = SqlServer then
                   renderSqlServerOutput "INSERTED" ctx update.Returning
               else
                   ""))
        if not update.From.IsEmpty then
            if ctx.Provider <> PostgreSql
               && ctx.Provider <> SqlServer
               && ctx.Provider <> SQLite
               && ctx.Provider <> Oracle then
                invalidOp "UPDATE ... FROM is not supported by the target provider."
            sql.Append(" FROM ").Append(update.From |> List.map (renderSource ctx) |> String.concat ", ") |> ignore
        update.Where
        |> Option.iter (fun predicate ->
            sql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
        sql.Append(renderTrailingReturning ctx update.Returning).ToString()

    let private renderDelete (ctx: RenderContext) (delete: Delete) =
        let targetAlias =
            match delete.TargetAlias with
            | None -> ""
            | Some alias when ctx.Provider = PostgreSql || ctx.Provider = Firebird ->
                " AS " + renderAlias ctx.Provider alias
            | Some _ -> invalidOp "DML target aliases are not supported by the target provider."
        let renderedTarget = renderIdentifier ctx.Provider delete.Target
        let sql = RenderBuffer(ctx,
            "DELETE FROM "
            + renderedTarget
            + targetAlias
            + (if ctx.Provider = SqlServer then
                   renderSqlServerOutput "DELETED" ctx delete.Returning
               else
                   ""))
        if not delete.Using.IsEmpty then
            match ctx.Provider with
            | PostgreSql ->
                sql.Append(" USING ").Append(delete.Using |> List.map (renderSource ctx) |> String.concat ", ") |> ignore
            | SqlServer ->
                if delete.TargetAlias.IsSome then
                    invalidOp "SQL Server joined DELETE target aliases require FROM-scope alias declaration and are not represented by the current Core DML target-alias shape."
                sql.Append(" FROM ").Append(renderedTarget).Append(", ")
                   .Append(delete.Using |> List.map (renderSource ctx) |> String.concat ", ") |> ignore
            | Oracle ->
                if delete.TargetAlias.IsSome then
                    invalidOp "Oracle direct-join DELETE target aliases are not represented by the current Core target-alias lowering."
                sql.Append(" FROM ").Append(delete.Using |> List.map (renderSource ctx) |> String.concat ", ") |> ignore
            | _ ->
                invalidOp "Joined DELETE is not supported by the target provider."
        delete.Where
        |> Option.iter (fun predicate ->
            sql.Append(" WHERE ").Append(renderPredicate ctx predicate) |> ignore)
        sql.Append(renderTrailingReturning ctx delete.Returning).ToString()

    let private renderMerge (ctx: RenderContext) (merge: Merge) =
        if ctx.Provider <> SqlServer then
            invalidOp "Validated canonical MERGE reached a non-SQL Server renderer."
        let sourceColumns =
            merge.Source.SourceColumns
            |> NonEmpty.toList
            |> List.map (renderAlias SqlServer)
            |> String.concat ", "
        let sourceValues =
            merge.Source.SourceValues
            |> NonEmpty.toList
            |> List.map (renderExpr ctx)
            |> String.concat ", "
        let sql = RenderBuffer(ctx,
            "MERGE INTO "
            + renderIdentifier SqlServer merge.Target
            + " AS "
            + renderAlias SqlServer merge.TargetAlias
            + " USING (VALUES ("
            + sourceValues
            + ")) AS "
            + renderAlias SqlServer merge.Source.Alias
            + " ("
            + sourceColumns
            + ") ON "
            + renderPredicate ctx merge.MatchPredicate)

        merge.Matched
        |> Option.iter (function
            | MergeDelete ->
                sql.Append(" WHEN MATCHED THEN DELETE") |> ignore
            | MergeUpdate assignments ->
                let rendered =
                    assignments
                    |> NonEmpty.toList
                    |> List.map (fun assignment ->
                        renderIdentifier SqlServer assignment.Target
                        + " = "
                        + renderExpr ctx assignment.Value)
                    |> String.concat ", "
                sql.Append(" WHEN MATCHED THEN UPDATE SET ").Append(rendered) |> ignore)

        merge.NotMatched
        |> Option.iter (fun mergeInsert ->
            let columns =
                mergeInsert.InsertColumns
                |> NonEmpty.toList
                |> List.map (renderIdentifier SqlServer)
                |> String.concat ", "
            let values =
                mergeInsert.InsertValues
                |> NonEmpty.toList
                |> List.map (renderExpr ctx)
                |> String.concat ", "
            sql.Append(" WHEN NOT MATCHED THEN INSERT (").Append(columns)
               .Append(") VALUES (").Append(values).Append(')') |> ignore)

        sql.Append(';').ToString()

    let private providerForRuntime = function
        | TargetRuntime.PostgreSqlRuntime -> Provider.PostgreSql
        | TargetRuntime.MySqlRuntime -> Provider.MySql
        | TargetRuntime.SqlServerRuntime _ -> Provider.SqlServer
        | TargetRuntime.SQLiteRuntime -> Provider.SQLite
        | TargetRuntime.OracleRuntime -> Provider.Oracle
        | TargetRuntime.FirebirdRuntime -> Provider.Firebird

    let render (executable: RewritePolicy.ExecutableSql) : RenderedCommand =
        let targetRuntime = RewritePolicy.Executable.targetRuntime executable
        let document = RewritePolicy.Executable.value executable
        let ctx = RenderContext(providerForRuntime targetRuntime, targetRuntime, document.Span)
        let sql, returnsRows =
            match document.Statement with
            | Statement.QueryStatement query -> renderQuery ctx query, false
            | Statement.InsertStatement insert -> renderInsert ctx insert, not insert.Returning.IsEmpty
            | Statement.UpdateStatement update -> renderUpdate ctx update, not update.Returning.IsEmpty
            | Statement.DeleteStatement delete -> renderDelete ctx delete, not delete.Returning.IsEmpty
            | Statement.MergeStatement merge -> renderMerge ctx merge, false
        // Fail closed before exposing a generated command to the executor.
        // This guards the delivered artifact size; individual specialized
        // emitters still need streaming checks to cap transient allocations.
        if sql.Length > MaxRenderedSqlLength then
            let detail =
                "Rendered SQL exceeds the maximum "
                + string MaxRenderedSqlLength
                + " UTF-16 code units."
            let location =
                if document.Span.Start >= 0 && document.Span.Length >= 0 then
                    SqlDiagnosticSpan(document.Span.Start, document.Span.Length)
                else
                    Unchecked.defaultof<SqlDiagnosticSpan>
            raise (
                SqlCompilationException(
                    detail,
                    SqlDiagnostic(
                        "SQL_COMPILER_RENDERED_OUTPUT_LIMIT",
                        SqlDiagnosticStage.RenderingInvariant,
                        SqlDiagnosticCategory.Resource,
                        detail,
                        location)))
        { Sql = sql; Parameters = ctx.Parameters; ReturnsRows = returnsRows }
