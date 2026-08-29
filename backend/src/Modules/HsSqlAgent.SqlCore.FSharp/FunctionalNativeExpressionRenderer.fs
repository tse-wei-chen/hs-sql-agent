namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open System.Globalization
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# ownership boundary for structural native-expression lowering.
/// All query-expression leaves, provider-sensitive literal/interval lowering, and boolean
/// predicate compatibility are owned here; the query path no longer falls back to the C# renderer.
module internal FunctionalNativeExpressionRenderer =

    let private emptyBindings = ImmutableArray<obj | null>.Empty
    let private parameterPlaceholder = NativeSqlParameterizer.Placeholder

    let private safeFunctionName =
        Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)

    let private safeKeyword =
        Regex(@"^[A-Z_]+$", RegexOptions.CultureInvariant)

    let private safeCastType =
        Regex(
            @"^[A-Za-z_][A-Za-z0-9_.]*(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*(?:\((?:MAX|[0-9]+(?:,[0-9]+)?)\))?(?:\s+(?:PRECISION|VARYING|WITH|WITHOUT|TIME|ZONE|SIGNED|UNSIGNED))*$",
            RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase)

    let private combine sql (left: NativeSqlFragment) (right: NativeSqlFragment) =
        NativeSqlFragment(sql, left.Bindings.AddRange(right.Bindings))

    let private bind value =
        NativeSqlFragment(parameterPlaceholder, ImmutableArray.Create<obj | null>(value))

    let private castBinding castType value =
        NativeSqlFragment(
            "CAST(" + parameterPlaceholder + " AS " + castType + ")",
            ImmutableArray.Create<obj | null>(value))

    let private identifierText (identifier: SqlIdentifier) =
        identifier.Parts
        |> Seq.map (fun part -> part.Value)
        |> String.concat "."

    let private equivalentFragment (left: NativeSqlFragment) (right: NativeSqlFragment) =
        left.Sql = right.Sql
        && left.Bindings.Length = right.Bindings.Length
        && Seq.forall2 (fun leftBinding rightBinding -> Object.Equals(leftBinding, rightBinding)) left.Bindings right.Bindings

    let private requireSimpleCaseComparison (branch: CaseBranch) =
        match branch.Condition with
        | :? BinaryExpr as comparison when comparison.Operator = "=" -> comparison
        | _ ->
            raise (SqlCompilationException(
                "Simple CASE branch lost its canonical equality shape before lowering."))

    let private isDirectProjectionWildcard (expression: SqlExpr) =
        let identifier =
            match expression with
            | :? ColumnExpr as column -> Some column.Name
            | :? BoundColumnExpr as column -> Some column.Name
            | _ -> None

        match identifier with
        | Some name when not name.Parts.IsDefaultOrEmpty ->
            let last = name.Parts[name.Parts.Length - 1]
            last.Value = "*" && not last.WasQuoted
        | _ -> false

    let private validateScalarSubqueryProjection (statement: SqlStatement) =
        let projection =
            match statement with
            | :? SelectStatement as select -> select.Select
            | :? QueryStatement as query -> query.Head.Select
            | _ ->
                raise (SqlCompilationException(
                    "Scalar subquery must contain a SELECT-compatible query statement."))

        if projection.Length <> 1 || isDirectProjectionWildcard projection[0].Expression then
            raise (SqlCompilationException(
                "Scalar subquery must expose exactly one statically known output column."))

    let private requireCurrentTemporalShape (functionCall: FunctionCallExpr) =
        if functionCall.IsDistinct || not functionCall.Arguments.IsDefaultOrEmpty then
            raise (SqlCompilationException(
                "Canonical current temporal function '" + identifierText functionCall.Name +
                "' must have zero arguments and cannot be DISTINCT."))

    let private requireArguments (functionCall: FunctionCallExpr) count =
        if functionCall.Arguments.Length <> count then
            raise (SqlCompilationException(
                "Canonical function '" + identifierText functionCall.Name +
                "' requires " + string count + " argument(s)."))

    let private literalKeyword (expression: SqlExpr) label =
        match expression with
        | :? LiteralExpr as literal ->
            match literal.Value with
            | :? string as value ->
                let normalized = value.Trim().ToUpperInvariant()
                if not (safeKeyword.IsMatch(normalized)) then
                    raise (SqlCompilationException(
                        "Unsafe " + label + " '" + value + "'."))
                normalized
            | _ -> raise (SqlCompilationException(label + " must be a canonical literal keyword."))
        | _ -> raise (SqlCompilationException(label + " must be a canonical literal keyword."))

    let private formatFirebirdOffsetTimestamp (value: DateTimeOffset) =
        value.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)

    let private renderFirebirdString value =
        let maxFirebirdUtf8VarcharChars = 8191
        if value.Length > maxFirebirdUtf8VarcharChars then
            raise (SqlCompilationException(
                "Firebird string literal exceeds the safe UTF8 VARCHAR limit of " +
                string maxFirebirdUtf8VarcharChars + " characters."))
        castBinding ("VARCHAR(" + string (Math.Max(1, value.Length)) + ")") value

    let private renderLiteral provider (literal: LiteralExpr) =
        match literal.Value with
        | :? SqlTimeValue ->
            match SqlStandaloneTimeCapabilityRules.TargetValidationError(provider) with
            | null -> ()
            | capabilityError -> raise (SqlCompilationException(capabilityError))
        | _ -> ()

        match literal.Value with
        | :? SqlOffsetDateTimeValue when provider = SqlAgentToolType.MySQL ->
            raise (SqlCompilationException(
                "MySQL has no native timestamp type that preserves a UTC offset."))
        | _ -> ()

        if provider = SqlAgentToolType.Postgres then
            match literal.Value with
            | :? SqlOffsetDateTimeValue as offsetValue -> bind (box (offsetValue.Value.ToUniversalTime()))
            | :? DateTimeOffset as rawOffset -> bind (box (rawOffset.ToUniversalTime()))
            | _ -> bind (NativeSqlValueNormalizer.Normalize(literal.Value))
        else
            let value = NativeSqlValueNormalizer.Normalize(literal.Value)
            if provider <> SqlAgentToolType.Firebird then
                bind value
            else
                match literal.Value with
                | :? SqlDateValue -> castBinding "DATE" value
                | :? SqlTimeValue -> castBinding "TIME" value
                | :? SqlLocalDateTimeValue -> castBinding "TIMESTAMP" value
                | :? SqlOffsetDateTimeValue as offset ->
                    castBinding "TIMESTAMP WITH TIME ZONE" (box (formatFirebirdOffsetTimestamp offset.Value))
                | _ ->
                    match value with
                    | :? DateOnly -> castBinding "DATE" value
                    | :? TimeOnly
                    | :? TimeSpan -> castBinding "TIME" value
                    | :? DateTime -> castBinding "TIMESTAMP" value
                    | :? DateTimeOffset as dateTimeOffset ->
                        castBinding "TIMESTAMP WITH TIME ZONE" (box (formatFirebirdOffsetTimestamp dateTimeOffset))
                    | :? string as text -> renderFirebirdString text
                    | :? bool -> castBinding "BOOLEAN" value
                    | :? byte
                    | :? sbyte
                    | :? int16
                    | :? uint16
                    | :? int -> castBinding "INTEGER" value
                    | :? uint32
                    | :? int64 -> castBinding "BIGINT" value
                    | :? decimal as decimalValue ->
                        castBinding (SqlFirebirdDecimalCapabilityRules.FirebirdCastType(decimalValue)) (box decimalValue)
                    | :? double
                    | :? single -> castBinding "DOUBLE PRECISION" value
                    | _ -> bind value

    let private renderInterval provider (interval: IntervalExpr) =
        if provider <> SqlAgentToolType.Postgres then
            raise (SqlCompilationException(
                "INTERVAL expressions are supported only by PostgreSQL in the Core backend."))
        NativeSqlFragment(
            "CAST(" + parameterPlaceholder + " AS interval)",
            ImmutableArray.Create<obj | null>(box interval.Literal))

    let private renderWindowBound (bound: WindowFrameBoundCore) =
        match bound.Kind with
        | WindowFrameBoundKindCore.UnboundedPreceding -> "UNBOUNDED PRECEDING"
        | WindowFrameBoundKindCore.Preceding when bound.Offset.HasValue && bound.Offset.Value >= 0 ->
            string bound.Offset.Value + " PRECEDING"
        | WindowFrameBoundKindCore.CurrentRow -> "CURRENT ROW"
        | WindowFrameBoundKindCore.Following when bound.Offset.HasValue && bound.Offset.Value >= 0 ->
            string bound.Offset.Value + " FOLLOWING"
        | WindowFrameBoundKindCore.UnboundedFollowing -> "UNBOUNDED FOLLOWING"
        | _ -> raise (SqlCompilationException("Invalid window frame bound '" + string bound.Kind + "'."))

    let private renderWindowFrame (frame: WindowFrame) =
        let unitText =
            match frame.Unit with
            | WindowFrameUnitKind.Rows -> "ROWS"
            | WindowFrameUnitKind.Range -> "RANGE"
            | value -> raise (SqlCompilationException("Unsupported window frame unit '" + string value + "'."))
        let startText = renderWindowBound frame.Start
        match frame.End with
        | null -> unitText + " " + startText
        | endBound -> unitText + " BETWEEN " + startText + " AND " + renderWindowBound endBound

    let private renderIdentifier provider (identifier: SqlIdentifier) =
        NativeSqlFragment(CoreIdentifierSqlRenderer.Render(identifier, provider, allowWildcard = true), emptyBindings)

    let rec render
        (provider: SqlAgentToolType)
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (expression: SqlExpr) =

        match expression with
        | :? BoundColumnExpr as column -> renderIdentifier provider column.Name
        | :? ColumnExpr as column -> renderIdentifier provider column.Name
        | :? LiteralExpr as literal -> renderLiteral provider literal
        | :? IntervalExpr as interval -> renderInterval provider interval

        | :? UnaryExpr as unary ->
            if unary.Operator <> "NOT" then
                raise (SqlCompilationException("Unsupported unary operator '" + unary.Operator + "'."))
            let operand = render provider renderSubquery unary.Operand
            NativeSqlFragment("NOT (" + operand.Sql + ")", operand.Bindings)

        | :? BinaryExpr as binary ->
            if (binary.Operator = "IN" || binary.Operator = "NOT IN") && not (binary.Right :? SubqueryExpr) then
                raise (SqlCompilationException("Canonical binary IN/NOT IN requires a scalar subquery RHS; expression lists must use InExpr."))
            let left = render provider renderSubquery binary.Left
            let right = render provider renderSubquery binary.Right
            let likeEscape = CoreLikeEscapeSqlRenderer.RenderSuffix(binary, provider)
            if binary.Operator = "%" && SqlModuloCapabilityRules.UsesFunctionSyntax(provider) then
                combine ("MOD(" + left.Sql + ", " + right.Sql + ")") left right
            elif binary.Operator = "||" && SqlConcatCapabilityRules.UsesConcatFunctionForCanonicalPipes(provider) then
                combine ("CONCAT(" + left.Sql + ", " + right.Sql + ")") left right
            else
                let operatorText =
                    match binary.Operator with
                    | "+" | "-" | "*" | "/" | "%" | "||"
                    | "=" | "<>" | "!=" | ">" | "<" | ">=" | "<="
                    | "LIKE" | "ILIKE" | "AND" | "OR" | "IN" | "NOT IN" -> binary.Operator
                    | value -> raise (SqlCompilationException("Unsupported binary operator '" + value + "'."))
                combine ("(" + left.Sql + " " + operatorText + " " + right.Sql + likeEscape + ")") left right

        | :? FunctionCallExpr as functionCall ->
            let name = identifierText functionCall.Name
            let canonical = SqlCanonicalFunctionRegistry.Find(name.ToUpperInvariant())
            let loweringKind =
                match canonical with
                | null -> SqlCanonicalNativeLoweringKind.Ordinary
                | value -> value.NativeLoweringKind

            if loweringKind = SqlCanonicalNativeLoweringKind.Position then
                requireArguments functionCall 2
                let haystack = render provider renderSubquery functionCall.Arguments[0]
                let needle = render provider renderSubquery functionCall.Arguments[1]
                match provider with
                | SqlAgentToolType.MsSqlServer -> combine ("CHARINDEX(" + needle.Sql + ", " + haystack.Sql + ")") needle haystack
                | SqlAgentToolType.Postgres -> combine ("STRPOS(" + haystack.Sql + ", " + needle.Sql + ")") haystack needle
                | SqlAgentToolType.MySQL -> combine ("LOCATE(" + needle.Sql + ", " + haystack.Sql + ")") needle haystack
                | SqlAgentToolType.Sqlite | SqlAgentToolType.Oracle -> combine ("INSTR(" + haystack.Sql + ", " + needle.Sql + ")") haystack needle
                | SqlAgentToolType.Firebird -> combine ("POSITION(" + needle.Sql + ", " + haystack.Sql + ")") needle haystack
                | _ -> raise (SqlCompilationException("Unsupported position provider."))
            elif loweringKind = SqlCanonicalNativeLoweringKind.DateAdd then
                requireArguments functionCall 3
                let unit = literalKeyword functionCall.Arguments[0] "DATEADD unit"
                match SqlDateMathCapabilityRules.TargetValidationError(unit, provider, "CORE_DATE_ADD") with
                | null -> ()
                | capabilityError -> raise (SqlCompilationException(capabilityError))
                let amount = render provider renderSubquery functionCall.Arguments[1]
                let value = render provider renderSubquery functionCall.Arguments[2]
                match provider with
                | SqlAgentToolType.MsSqlServer -> combine ("DATEADD(" + unit + ", " + amount.Sql + ", " + value.Sql + ")") amount value
                | SqlAgentToolType.MySQL -> combine ("TIMESTAMPADD(" + unit + ", " + amount.Sql + ", " + value.Sql + ")") amount value
                | SqlAgentToolType.Postgres -> combine ("(" + value.Sql + " + (" + amount.Sql + " * INTERVAL '1 day'))") value amount
                | SqlAgentToolType.Oracle -> combine ("(" + value.Sql + " + " + amount.Sql + ")") value amount
                | SqlAgentToolType.Sqlite -> combine ("DATETIME(" + value.Sql + ", PRINTF('%+d day', " + amount.Sql + "))") value amount
                | SqlAgentToolType.Firebird -> combine ("DATEADD(" + unit + ", " + amount.Sql + ", " + value.Sql + ")") amount value
                | _ -> raise (SqlCompilationException("Unsupported DATEADD provider."))
            elif loweringKind = SqlCanonicalNativeLoweringKind.DateDiff then
                requireArguments functionCall 3
                let unit = literalKeyword functionCall.Arguments[0] "DATEDIFF unit"
                match SqlDateMathCapabilityRules.TargetValidationError(unit, provider, "CORE_DATE_DIFF") with
                | null -> ()
                | capabilityError -> raise (SqlCompilationException(capabilityError))
                let startValue = render provider renderSubquery functionCall.Arguments[1]
                let endValue = render provider renderSubquery functionCall.Arguments[2]
                match provider with
                | SqlAgentToolType.MsSqlServer -> combine ("DATEDIFF(" + unit + ", " + startValue.Sql + ", " + endValue.Sql + ")") startValue endValue
                | SqlAgentToolType.MySQL -> combine ("TIMESTAMPDIFF(" + unit + ", " + startValue.Sql + ", " + endValue.Sql + ")") startValue endValue
                | SqlAgentToolType.Postgres -> combine ("(CAST(" + endValue.Sql + " AS date) - CAST(" + startValue.Sql + " AS date))") endValue startValue
                | SqlAgentToolType.Oracle -> combine ("(CAST(" + endValue.Sql + " AS DATE) - CAST(" + startValue.Sql + " AS DATE))") endValue startValue
                | SqlAgentToolType.Sqlite -> combine ("(JULIANDAY(" + endValue.Sql + ") - JULIANDAY(" + startValue.Sql + "))") endValue startValue
                | SqlAgentToolType.Firebird -> combine ("DATEDIFF(" + unit + " FROM " + startValue.Sql + " TO " + endValue.Sql + ")") startValue endValue
                | _ -> raise (SqlCompilationException("Unsupported DATEDIFF provider."))
            elif loweringKind = SqlCanonicalNativeLoweringKind.DatePart then
                requireArguments functionCall 2
                let part = literalKeyword functionCall.Arguments[0] "date part"
                match SqlDatePartCapabilityRules.TargetValidationError(part, provider) with
                | null -> ()
                | capabilityError -> raise (SqlCompilationException(capabilityError))
                let value = render provider renderSubquery functionCall.Arguments[1]
                let sql =
                    match provider with
                    | SqlAgentToolType.MsSqlServer | SqlAgentToolType.MySQL -> part + "(" + value.Sql + ")"
                    | SqlAgentToolType.Postgres | SqlAgentToolType.Oracle -> "EXTRACT(" + part + " FROM " + value.Sql + ")"
                    | SqlAgentToolType.Firebird -> "EXTRACT(" + part + " FROM CAST(" + value.Sql + " AS DATE))"
                    | SqlAgentToolType.Sqlite ->
                        match part with
                        | "YEAR" -> "CAST(STRFTIME('%Y', " + value.Sql + ") AS INTEGER)"
                        | "MONTH" -> "CAST(STRFTIME('%m', " + value.Sql + ") AS INTEGER)"
                        | "DAY" -> "CAST(STRFTIME('%d', " + value.Sql + ") AS INTEGER)"
                        | _ -> raise (SqlCompilationException("SQLite does not support date part " + part + "."))
                    | _ -> raise (SqlCompilationException("Unsupported date-part provider."))
                NativeSqlFragment(sql, value.Bindings)
            elif loweringKind = SqlCanonicalNativeLoweringKind.DateFormat then
                FunctionalTemporalCanonicalRenderer.renderDateFormat provider functionCall (fun argument -> render provider renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.DateParse then
                FunctionalTemporalCanonicalRenderer.renderDateParse provider functionCall (fun argument -> render provider renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.JsonExtract then
                FunctionalStructuredTextCanonicalRenderer.renderJsonExtract provider functionCall (fun argument -> render provider renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.JsonSet then
                FunctionalStructuredTextCanonicalRenderer.renderJsonSet provider functionCall (fun argument -> render provider renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.RegexMatch then
                FunctionalStructuredTextCanonicalRenderer.renderRegexMatch provider functionCall (fun argument -> render provider renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.StringAggregate then
                FunctionalStringAggregateRenderer.render provider functionCall (fun argument -> render provider renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.CurrentDate then
                requireCurrentTemporalShape functionCall
                NativeSqlFragment((if provider = SqlAgentToolType.MsSqlServer then "CAST(CURRENT_TIMESTAMP AS date)" else "CURRENT_DATE"), emptyBindings)
            elif loweringKind = SqlCanonicalNativeLoweringKind.CurrentTime then
                requireCurrentTemporalShape functionCall
                if provider = SqlAgentToolType.Oracle then raise (SqlCompilationException("CURRENT_TIME is not supported by Oracle."))
                NativeSqlFragment((if provider = SqlAgentToolType.MsSqlServer then "CAST(CURRENT_TIMESTAMP AS time)" else "CURRENT_TIME"), emptyBindings)
            elif loweringKind = SqlCanonicalNativeLoweringKind.CurrentTimestamp then
                requireCurrentTemporalShape functionCall
                NativeSqlFragment("CURRENT_TIMESTAMP", emptyBindings)
            elif loweringKind <> SqlCanonicalNativeLoweringKind.Ordinary then
                raise (SqlCompilationException(
                    "Unsupported canonical native lowering kind '" + string loweringKind +
                    "' for function '" + name + "'."))
            else
                if not (safeFunctionName.IsMatch(name)) then raise (SqlCompilationException("Unsafe function identifier '" + name + "'."))
                if name.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase) then
                    raise (SqlCompilationException("Canonical function '" + name + "' has no native lowering implementation; compilation was rejected."))
                let args = functionCall.Arguments |> Seq.map (render provider renderSubquery) |> Seq.toArray
                let renderedArgs = args |> Array.map (fun argument -> argument.Sql)
                if provider = SqlAgentToolType.Postgres && name.Equals("ROUND", StringComparison.OrdinalIgnoreCase) && args.Length = 2 then
                    renderedArgs[0] <- "CAST(" + renderedArgs[0] + " AS numeric)"
                let argumentSql =
                    let joined = String.Join(", ", renderedArgs)
                    if functionCall.IsDistinct then "DISTINCT " + joined else joined
                let bindings = args |> Array.fold (fun (current: ImmutableArray<obj | null>) (argument: NativeSqlFragment) -> current.AddRange(argument.Bindings)) emptyBindings
                NativeSqlFragment(name + "(" + argumentSql + ")", bindings)

        | :? FilterExpr as filter ->
            match provider with
            | SqlAgentToolType.Postgres | SqlAgentToolType.Sqlite | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> ()
            | value -> raise (SqlCompilationException("FILTER lowering is not supported by " + string value + "."))
            let renderedExpression = render provider renderSubquery filter.Expression
            let predicate = renderPredicate provider renderSubquery filter.Predicate
            NativeSqlFragment(renderedExpression.Sql + " FILTER (WHERE " + predicate.Sql + ")", renderedExpression.Bindings.AddRange(predicate.Bindings))

        | :? WindowedExpr as windowed ->
            match SqlWindowCapabilityRules.WindowValidationError(windowed, provider) with
            | null -> ()
            | capabilityError -> raise (SqlCompilationException(capabilityError))
            let renderedExpression = render provider renderSubquery windowed.Expression
            let parts = ResizeArray<string>()
            let mutable bindings = renderedExpression.Bindings
            if not windowed.Window.PartitionBy.IsDefaultOrEmpty then
                let partition = windowed.Window.PartitionBy |> Seq.map (render provider renderSubquery) |> Seq.toArray
                parts.Add("PARTITION BY " + String.Join(", ", partition |> Array.map (fun item -> item.Sql)))
                for item in partition do bindings <- bindings.AddRange(item.Bindings)
            if not windowed.Window.OrderBy.IsDefaultOrEmpty then
                let orderParts = ResizeArray<string>()
                for item in windowed.Window.OrderBy do
                    let renderedItem = render provider renderSubquery item.Expression
                    let nullOrdering =
                        match item.NullOrdering with
                        | NullOrderingKind.Default -> String.Empty
                        | NullOrderingKind.First -> " NULLS FIRST"
                        | NullOrderingKind.Last -> " NULLS LAST"
                        | value -> raise (SqlCompilationException("Unsupported NULL ordering '" + string value + "' in window."))
                    orderParts.Add(renderedItem.Sql + (if item.Descending then " DESC" else " ASC") + nullOrdering)
                    bindings <- bindings.AddRange(renderedItem.Bindings)
                parts.Add("ORDER BY " + String.Join(", ", orderParts))
            match windowed.Window.Frame with
            | null -> ()
            | frame -> parts.Add(renderWindowFrame frame)
            NativeSqlFragment(renderedExpression.Sql + " OVER (" + String.Join(" ", parts) + ")", bindings)

        | :? CastExpr as castExpr ->
            if not (safeCastType.IsMatch(castExpr.TypeName)) then raise (SqlCompilationException("Unsafe CAST target type '" + castExpr.TypeName + "'."))
            let rendered = render provider renderSubquery castExpr.Expression
            NativeSqlFragment("CAST(" + rendered.Sql + " AS " + castExpr.TypeName + ")", rendered.Bindings)

        | :? SimpleCaseExpr as simpleCase ->
            if simpleCase.Branches.IsDefaultOrEmpty then raise (SqlCompilationException("Simple CASE requires at least one WHEN branch."))
            let first = requireSimpleCaseComparison simpleCase.Branches[0]
            let operand = render provider renderSubquery first.Left
            let mutable bindings = operand.Bindings
            let parts = ResizeArray<string>()
            for branch in simpleCase.Branches do
                let comparison = requireSimpleCaseComparison branch
                let branchOperand = render provider renderSubquery comparison.Left
                if not (equivalentFragment operand branchOperand) then raise (SqlCompilationException("Simple CASE branches must preserve one canonical operand before native lowering."))
                let matched = render provider renderSubquery comparison.Right
                let value = render provider renderSubquery branch.Value
                parts.Add("WHEN " + matched.Sql + " THEN " + value.Sql)
                bindings <- bindings.AddRange(matched.Bindings).AddRange(value.Bindings)
            match simpleCase.ElseExpression with
            | null -> ()
            | otherwiseExpression ->
                let otherwise = render provider renderSubquery otherwiseExpression
                parts.Add("ELSE " + otherwise.Sql)
                bindings <- bindings.AddRange(otherwise.Bindings)
            NativeSqlFragment("CASE " + operand.Sql + " " + String.Join(" ", parts) + " END", bindings)

        | :? CaseExpr as caseExpr ->
            if caseExpr.Branches.IsDefaultOrEmpty then raise (SqlCompilationException("Searched CASE requires at least one WHEN branch."))
            let mutable bindings = emptyBindings
            let parts = ResizeArray<string>()
            for branch in caseExpr.Branches do
                let condition = renderPredicate provider renderSubquery branch.Condition
                let value = render provider renderSubquery branch.Value
                parts.Add("WHEN " + condition.Sql + " THEN " + value.Sql)
                bindings <- bindings.AddRange(condition.Bindings).AddRange(value.Bindings)
            match caseExpr.ElseExpression with
            | null -> ()
            | otherwiseExpression ->
                let otherwise = render provider renderSubquery otherwiseExpression
                parts.Add("ELSE " + otherwise.Sql)
                bindings <- bindings.AddRange(otherwise.Bindings)
            NativeSqlFragment("CASE " + String.Join(" ", parts) + " END", bindings)

        | :? InExpr as inExpr ->
            if inExpr.Items.IsDefaultOrEmpty then raise (SqlCompilationException("IN requires at least one item."))
            let value = render provider renderSubquery inExpr.Value
            let items = inExpr.Items |> Seq.map (render provider renderSubquery) |> Seq.toArray
            let bindings = items |> Array.fold (fun (current: ImmutableArray<obj | null>) (item: NativeSqlFragment) -> current.AddRange(item.Bindings)) value.Bindings
            NativeSqlFragment("(" + value.Sql + " " + (if inExpr.IsNegated then "NOT IN" else "IN") + " (" + String.Join(", ", items |> Array.map (fun item -> item.Sql)) + "))", bindings)

        | :? BetweenExpr as between ->
            let value = render provider renderSubquery between.Value
            let lower = render provider renderSubquery between.Lower
            let upper = render provider renderSubquery between.Upper
            NativeSqlFragment("(" + value.Sql + " " + (if between.IsNegated then "NOT BETWEEN" else "BETWEEN") + " " + lower.Sql + " AND " + upper.Sql + ")", value.Bindings.AddRange(lower.Bindings).AddRange(upper.Bindings))

        | :? IsNullExpr as isNull ->
            let value = render provider renderSubquery isNull.Value
            NativeSqlFragment("(" + value.Sql + " IS " + (if isNull.IsNegated then "NOT " else String.Empty) + "NULL)", value.Bindings)

        | :? SubqueryExpr as subquery ->
            validateScalarSubqueryProjection subquery.Query
            let rendered = renderSubquery.Invoke(subquery.Query)
            NativeSqlFragment("(" + rendered.Sql + ")", rendered.Bindings)

        | :? ExistsExpr as exists ->
            let rendered = renderSubquery.Invoke(exists.Query)
            NativeSqlFragment((if exists.IsNegated then "NOT " else String.Empty) + "EXISTS (" + rendered.Sql + ")", rendered.Bindings)

        | _ ->
            raise (SqlCompilationException(
                "Unsupported expression during F# native lowering: " + expression.GetType().Name))

    and renderPredicate
        (provider: SqlAgentToolType)
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (expression: SqlExpr) =

        if provider = SqlAgentToolType.Oracle || provider = SqlAgentToolType.MsSqlServer then
            match expression with
            | :? LiteralExpr as literal ->
                match literal.Value with
                | :? bool as value -> NativeSqlFragment((if value then "(1 = 1)" else "(1 = 0)"), emptyBindings)
                | _ -> render provider renderSubquery expression
            | :? UnaryExpr as unary when unary.Operator = "NOT" ->
                let operand = renderPredicate provider renderSubquery unary.Operand
                NativeSqlFragment("NOT (" + operand.Sql + ")", operand.Bindings)
            | :? BinaryExpr as binary when binary.Operator = "AND" || binary.Operator = "OR" ->
                let left = renderPredicate provider renderSubquery binary.Left
                let right = renderPredicate provider renderSubquery binary.Right
                combine ("(" + left.Sql + " " + binary.Operator + " " + right.Sql + ")") left right
            | :? CaseExpr as caseExpr when CoreBooleanProjectionRules.IsDefinitelyBoolean(caseExpr, provider) ->
                if not (CoreBooleanProjectionRules.HasOnlyLiteralBooleanCaseResults(caseExpr)) then
                    raise (SqlCompilationException(
                        "Boolean CASE predicates for " + string provider +
                        " currently require every THEN/ELSE result to be a literal TRUE, FALSE, or NULL " +
                        "so three-valued logic is preserved without duplicating predicate evaluation."))
                match caseExpr with
                | :? SimpleCaseExpr as simpleCase -> renderBooleanSimpleCasePredicate provider renderSubquery simpleCase
                | _ -> renderBooleanCasePredicate provider renderSubquery caseExpr
            | _ -> render provider renderSubquery expression
        else
            render provider renderSubquery expression

    and private renderBooleanTruthValue (expression: SqlExpr) =
        match expression with
        | :? LiteralExpr as literal ->
            match literal.Value with
            | null -> NativeSqlFragment("NULL", emptyBindings)
            | :? bool as value -> NativeSqlFragment((if value then "1" else "0"), emptyBindings)
            | _ -> raise (SqlCompilationException(
                "Boolean CASE predicate lowering requires literal TRUE, FALSE, or NULL results."))
        | _ -> raise (SqlCompilationException(
            "Boolean CASE predicate lowering requires literal TRUE, FALSE, or NULL results."))

    and private renderBooleanSimpleCasePredicate
        provider
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (caseExpr: SimpleCaseExpr) =

        if caseExpr.Branches.IsDefaultOrEmpty then
            raise (SqlCompilationException("Simple CASE requires at least one WHEN branch."))
        let first = requireSimpleCaseComparison caseExpr.Branches[0]
        let operand = render provider renderSubquery first.Left
        let mutable bindings = operand.Bindings
        let parts = ResizeArray<string>()
        for branch in caseExpr.Branches do
            let comparison = requireSimpleCaseComparison branch
            let branchOperand = render provider renderSubquery comparison.Left
            if not (equivalentFragment operand branchOperand) then
                raise (SqlCompilationException(
                    "Simple CASE branches must preserve one canonical operand before native lowering."))
            let matched = render provider renderSubquery comparison.Right
            let value = renderBooleanTruthValue branch.Value
            parts.Add("WHEN " + matched.Sql + " THEN " + value.Sql)
            bindings <- bindings.AddRange(matched.Bindings).AddRange(value.Bindings)
        match caseExpr.ElseExpression with
        | null -> ()
        | otherwiseExpression ->
            let otherwise = renderBooleanTruthValue otherwiseExpression
            parts.Add("ELSE " + otherwise.Sql)
            bindings <- bindings.AddRange(otherwise.Bindings)
        NativeSqlFragment("(CASE " + operand.Sql + " " + String.Join(" ", parts) + " END = 1)", bindings)

    and private renderBooleanCasePredicate
        provider
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (caseExpr: CaseExpr) =

        if caseExpr.Branches.IsDefaultOrEmpty then
            raise (SqlCompilationException("Searched CASE requires at least one WHEN branch."))
        let mutable bindings = emptyBindings
        let parts = ResizeArray<string>()
        for branch in caseExpr.Branches do
            let condition = renderPredicate provider renderSubquery branch.Condition
            let value = renderBooleanTruthValue branch.Value
            parts.Add("WHEN " + condition.Sql + " THEN " + value.Sql)
            bindings <- bindings.AddRange(condition.Bindings).AddRange(value.Bindings)
        match caseExpr.ElseExpression with
        | null -> ()
        | otherwiseExpression ->
            let otherwise = renderBooleanTruthValue otherwiseExpression
            parts.Add("ELSE " + otherwise.Sql)
            bindings <- bindings.AddRange(otherwise.Bindings)
        NativeSqlFragment("(CASE " + String.Join(" ", parts) + " END = 1)", bindings)
