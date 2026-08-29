namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open System.Text.RegularExpressions
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// F# ownership boundary for structural native-expression lowering.
/// Provider-sensitive literal/interval leaves and the specialized Oracle/SQL Server boolean-CASE
/// predicate bridge remain in the legacy renderer while structural recursion and canonical
/// function lowering are owned here.
module internal FunctionalNativeExpressionRenderer =

    let private emptyBindings = ImmutableArray<obj | null>.Empty

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

    let private renderIdentifier (renderer: NativeSqlRenderer) (identifier: SqlIdentifier) =
        NativeSqlFragment(CoreIdentifierSqlRenderer.Render(identifier, renderer.Provider, allowWildcard = true), emptyBindings)

    let rec render
        (renderer: NativeSqlRenderer)
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (expression: SqlExpr) =

        match expression with
        | :? BoundColumnExpr as column -> renderIdentifier renderer column.Name
        | :? ColumnExpr as column -> renderIdentifier renderer column.Name

        | :? UnaryExpr as unary ->
            if unary.Operator <> "NOT" then
                raise (SqlCompilationException("Unsupported unary operator '" + unary.Operator + "'."))
            let operand = render renderer renderSubquery unary.Operand
            NativeSqlFragment("NOT (" + operand.Sql + ")", operand.Bindings)

        | :? BinaryExpr as binary ->
            if (binary.Operator = "IN" || binary.Operator = "NOT IN") && not (binary.Right :? SubqueryExpr) then
                raise (SqlCompilationException("Canonical binary IN/NOT IN requires a scalar subquery RHS; expression lists must use InExpr."))
            let left = render renderer renderSubquery binary.Left
            let right = render renderer renderSubquery binary.Right
            let likeEscape = CoreLikeEscapeSqlRenderer.RenderSuffix(binary, renderer.Provider)
            if binary.Operator = "%" && SqlModuloCapabilityRules.UsesFunctionSyntax(renderer.Provider) then
                combine ("MOD(" + left.Sql + ", " + right.Sql + ")") left right
            elif binary.Operator = "||" && SqlConcatCapabilityRules.UsesConcatFunctionForCanonicalPipes(renderer.Provider) then
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
                let haystack: NativeSqlFragment = render renderer renderSubquery functionCall.Arguments[0]
                let needle: NativeSqlFragment = render renderer renderSubquery functionCall.Arguments[1]
                match renderer.Provider with
                | SqlAgentToolType.MsSqlServer -> combine ("CHARINDEX(" + needle.Sql + ", " + haystack.Sql + ")") needle haystack
                | SqlAgentToolType.Postgres -> combine ("STRPOS(" + haystack.Sql + ", " + needle.Sql + ")") haystack needle
                | SqlAgentToolType.MySQL -> combine ("LOCATE(" + needle.Sql + ", " + haystack.Sql + ")") needle haystack
                | SqlAgentToolType.Sqlite | SqlAgentToolType.Oracle -> combine ("INSTR(" + haystack.Sql + ", " + needle.Sql + ")") haystack needle
                | SqlAgentToolType.Firebird -> combine ("POSITION(" + needle.Sql + ", " + haystack.Sql + ")") needle haystack
                | _ -> raise (SqlCompilationException("Unsupported position provider."))
            elif loweringKind = SqlCanonicalNativeLoweringKind.DateAdd then
                requireArguments functionCall 3
                let unit = literalKeyword functionCall.Arguments[0] "DATEADD unit"
                match SqlDateMathCapabilityRules.TargetValidationError(unit, renderer.Provider, "CORE_DATE_ADD") with
                | null -> ()
                | capabilityError -> raise (SqlCompilationException(capabilityError))
                let amount: NativeSqlFragment = render renderer renderSubquery functionCall.Arguments[1]
                let value: NativeSqlFragment = render renderer renderSubquery functionCall.Arguments[2]
                match renderer.Provider with
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
                match SqlDateMathCapabilityRules.TargetValidationError(unit, renderer.Provider, "CORE_DATE_DIFF") with
                | null -> ()
                | capabilityError -> raise (SqlCompilationException(capabilityError))
                let startValue: NativeSqlFragment = render renderer renderSubquery functionCall.Arguments[1]
                let endValue: NativeSqlFragment = render renderer renderSubquery functionCall.Arguments[2]
                match renderer.Provider with
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
                match SqlDatePartCapabilityRules.TargetValidationError(part, renderer.Provider) with
                | null -> ()
                | capabilityError -> raise (SqlCompilationException(capabilityError))
                let value: NativeSqlFragment = render renderer renderSubquery functionCall.Arguments[1]
                let sql =
                    match renderer.Provider with
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
                FunctionalTemporalCanonicalRenderer.renderDateFormat renderer.Provider functionCall (fun argument -> render renderer renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.DateParse then
                FunctionalTemporalCanonicalRenderer.renderDateParse renderer.Provider functionCall (fun argument -> render renderer renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.JsonExtract then
                FunctionalStructuredTextCanonicalRenderer.renderJsonExtract renderer.Provider functionCall (fun argument -> render renderer renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.JsonSet then
                FunctionalStructuredTextCanonicalRenderer.renderJsonSet renderer.Provider functionCall (fun argument -> render renderer renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.RegexMatch then
                FunctionalStructuredTextCanonicalRenderer.renderRegexMatch renderer.Provider functionCall (fun argument -> render renderer renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.StringAggregate then
                FunctionalStringAggregateRenderer.render renderer.Provider functionCall (fun argument -> render renderer renderSubquery argument)
            elif loweringKind = SqlCanonicalNativeLoweringKind.CurrentDate then
                requireCurrentTemporalShape functionCall
                NativeSqlFragment((if renderer.Provider = SqlAgentToolType.MsSqlServer then "CAST(CURRENT_TIMESTAMP AS date)" else "CURRENT_DATE"), emptyBindings)
            elif loweringKind = SqlCanonicalNativeLoweringKind.CurrentTime then
                requireCurrentTemporalShape functionCall
                if renderer.Provider = SqlAgentToolType.Oracle then raise (SqlCompilationException("CURRENT_TIME is not supported by Oracle."))
                NativeSqlFragment((if renderer.Provider = SqlAgentToolType.MsSqlServer then "CAST(CURRENT_TIMESTAMP AS time)" else "CURRENT_TIME"), emptyBindings)
            elif loweringKind = SqlCanonicalNativeLoweringKind.CurrentTimestamp then
                requireCurrentTemporalShape functionCall
                NativeSqlFragment("CURRENT_TIMESTAMP", emptyBindings)
            elif loweringKind <> SqlCanonicalNativeLoweringKind.Ordinary then
                renderer.RenderExpressionForFunctional(expression, renderSubquery)
            else
                if not (safeFunctionName.IsMatch(name)) then raise (SqlCompilationException("Unsafe function identifier '" + name + "'."))
                if name.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase) then
                    raise (SqlCompilationException("Canonical function '" + name + "' has no native lowering implementation; compilation was rejected."))
                let args = functionCall.Arguments |> Seq.map (render renderer renderSubquery) |> Seq.toArray
                let renderedArgs = args |> Array.map (fun argument -> argument.Sql)
                if renderer.Provider = SqlAgentToolType.Postgres && name.Equals("ROUND", StringComparison.OrdinalIgnoreCase) && args.Length = 2 then
                    renderedArgs[0] <- "CAST(" + renderedArgs[0] + " AS numeric)"
                let argumentSql =
                    let joined = String.Join(", ", renderedArgs)
                    if functionCall.IsDistinct then "DISTINCT " + joined else joined
                let bindings = args |> Array.fold (fun (current: ImmutableArray<obj | null>) (argument: NativeSqlFragment) -> current.AddRange(argument.Bindings)) emptyBindings
                NativeSqlFragment(name + "(" + argumentSql + ")", bindings)

        | :? FilterExpr as filter ->
            match renderer.Provider with
            | SqlAgentToolType.Postgres | SqlAgentToolType.Sqlite | SqlAgentToolType.Oracle | SqlAgentToolType.Firebird -> ()
            | provider -> raise (SqlCompilationException("FILTER lowering is not supported by " + string provider + "."))
            let renderedExpression: NativeSqlFragment = render renderer renderSubquery filter.Expression
            let predicate: NativeSqlFragment = renderPredicate renderer renderSubquery filter.Predicate
            NativeSqlFragment(renderedExpression.Sql + " FILTER (WHERE " + predicate.Sql + ")", renderedExpression.Bindings.AddRange(predicate.Bindings))

        | :? WindowedExpr as windowed ->
            match SqlWindowCapabilityRules.WindowValidationError(windowed, renderer.Provider) with
            | null -> ()
            | capabilityError -> raise (SqlCompilationException(capabilityError))
            let renderedExpression: NativeSqlFragment = render renderer renderSubquery windowed.Expression
            let parts = ResizeArray<string>()
            let mutable bindings = renderedExpression.Bindings
            if not windowed.Window.PartitionBy.IsDefaultOrEmpty then
                let partition = windowed.Window.PartitionBy |> Seq.map (render renderer renderSubquery) |> Seq.toArray
                parts.Add("PARTITION BY " + String.Join(", ", partition |> Array.map (fun item -> item.Sql)))
                for item in partition do bindings <- bindings.AddRange(item.Bindings)
            if not windowed.Window.OrderBy.IsDefaultOrEmpty then
                let orderParts = ResizeArray<string>()
                for item in windowed.Window.OrderBy do
                    let renderedItem: NativeSqlFragment = render renderer renderSubquery item.Expression
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
            let rendered = render renderer renderSubquery castExpr.Expression
            NativeSqlFragment("CAST(" + rendered.Sql + " AS " + castExpr.TypeName + ")", rendered.Bindings)

        | :? SimpleCaseExpr as simpleCase ->
            if simpleCase.Branches.IsDefaultOrEmpty then raise (SqlCompilationException("Simple CASE requires at least one WHEN branch."))
            let first = requireSimpleCaseComparison simpleCase.Branches[0]
            let operand: NativeSqlFragment = render renderer renderSubquery first.Left
            let mutable bindings = operand.Bindings
            let parts = ResizeArray<string>()
            for branch in simpleCase.Branches do
                let comparison = requireSimpleCaseComparison branch
                let branchOperand: NativeSqlFragment = render renderer renderSubquery comparison.Left
                if not (equivalentFragment operand branchOperand) then raise (SqlCompilationException("Simple CASE branches must preserve one canonical operand before native lowering."))
                let matched: NativeSqlFragment = render renderer renderSubquery comparison.Right
                let value: NativeSqlFragment = render renderer renderSubquery branch.Value
                parts.Add("WHEN " + matched.Sql + " THEN " + value.Sql)
                bindings <- bindings.AddRange(matched.Bindings).AddRange(value.Bindings)
            match simpleCase.ElseExpression with
            | null -> ()
            | otherwiseExpression ->
                let otherwise: NativeSqlFragment = render renderer renderSubquery otherwiseExpression
                parts.Add("ELSE " + otherwise.Sql)
                bindings <- bindings.AddRange(otherwise.Bindings)
            NativeSqlFragment("CASE " + operand.Sql + " " + String.Join(" ", parts) + " END", bindings)

        | :? CaseExpr as caseExpr ->
            if caseExpr.Branches.IsDefaultOrEmpty then raise (SqlCompilationException("Searched CASE requires at least one WHEN branch."))
            let mutable bindings = emptyBindings
            let parts = ResizeArray<string>()
            for branch in caseExpr.Branches do
                let condition: NativeSqlFragment = renderPredicate renderer renderSubquery branch.Condition
                let value: NativeSqlFragment = render renderer renderSubquery branch.Value
                parts.Add("WHEN " + condition.Sql + " THEN " + value.Sql)
                bindings <- bindings.AddRange(condition.Bindings).AddRange(value.Bindings)
            match caseExpr.ElseExpression with
            | null -> ()
            | otherwiseExpression ->
                let otherwise: NativeSqlFragment = render renderer renderSubquery otherwiseExpression
                parts.Add("ELSE " + otherwise.Sql)
                bindings <- bindings.AddRange(otherwise.Bindings)
            NativeSqlFragment("CASE " + String.Join(" ", parts) + " END", bindings)

        | :? InExpr as inExpr ->
            if inExpr.Items.IsDefaultOrEmpty then raise (SqlCompilationException("IN requires at least one item."))
            let value = render renderer renderSubquery inExpr.Value
            let items = inExpr.Items |> Seq.map (render renderer renderSubquery) |> Seq.toArray
            let bindings = items |> Array.fold (fun (current: ImmutableArray<obj | null>) (item: NativeSqlFragment) -> current.AddRange(item.Bindings)) value.Bindings
            NativeSqlFragment("(" + value.Sql + " " + (if inExpr.IsNegated then "NOT IN" else "IN") + " (" + String.Join(", ", items |> Array.map (fun item -> item.Sql)) + "))", bindings)

        | :? BetweenExpr as between ->
            let value = render renderer renderSubquery between.Value
            let lower = render renderer renderSubquery between.Lower
            let upper = render renderer renderSubquery between.Upper
            NativeSqlFragment("(" + value.Sql + " " + (if between.IsNegated then "NOT BETWEEN" else "BETWEEN") + " " + lower.Sql + " AND " + upper.Sql + ")", value.Bindings.AddRange(lower.Bindings).AddRange(upper.Bindings))

        | :? IsNullExpr as isNull ->
            let value = render renderer renderSubquery isNull.Value
            NativeSqlFragment("(" + value.Sql + " IS " + (if isNull.IsNegated then "NOT " else String.Empty) + "NULL)", value.Bindings)

        | :? SubqueryExpr as subquery ->
            validateScalarSubqueryProjection subquery.Query
            let rendered = renderSubquery.Invoke(subquery.Query)
            NativeSqlFragment("(" + rendered.Sql + ")", rendered.Bindings)

        | :? ExistsExpr as exists ->
            let rendered = renderSubquery.Invoke(exists.Query)
            NativeSqlFragment((if exists.IsNegated then "NOT " else String.Empty) + "EXISTS (" + rendered.Sql + ")", rendered.Bindings)

        | _ -> renderer.RenderExpressionForFunctional(expression, renderSubquery)

    and renderPredicate
        (renderer: NativeSqlRenderer)
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (expression: SqlExpr) =

        if renderer.Provider = SqlAgentToolType.Oracle || renderer.Provider = SqlAgentToolType.MsSqlServer then
            match expression with
            | :? LiteralExpr as literal ->
                match literal.Value with
                | :? bool as value -> NativeSqlFragment((if value then "(1 = 1)" else "(1 = 0)"), emptyBindings)
                | _ -> render renderer renderSubquery expression
            | :? UnaryExpr as unary when unary.Operator = "NOT" ->
                let operand = renderPredicate renderer renderSubquery unary.Operand
                NativeSqlFragment("NOT (" + operand.Sql + ")", operand.Bindings)
            | :? BinaryExpr as binary when binary.Operator = "AND" || binary.Operator = "OR" ->
                let left = renderPredicate renderer renderSubquery binary.Left
                let right = renderPredicate renderer renderSubquery binary.Right
                combine ("(" + left.Sql + " " + binary.Operator + " " + right.Sql + ")") left right
            | :? CaseExpr -> renderer.RenderPredicateForFunctional(expression, renderSubquery)
            | _ -> render renderer renderSubquery expression
        else
            render renderer renderSubquery expression
