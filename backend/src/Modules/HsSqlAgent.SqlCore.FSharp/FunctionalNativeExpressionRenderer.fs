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
/// Provider-sensitive literal/interval/canonical-function/case/window/filter leaves remain in the
/// legacy renderer while structural recursion, casts, and ordinary functions are owned here.
module internal FunctionalNativeExpressionRenderer =

    let private emptyBindings = ImmutableArray<obj | null>.Empty

    let private safeFunctionName =
        Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)

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

    let private renderIdentifier
        (renderer: NativeSqlRenderer)
        (identifier: SqlIdentifier) =
        NativeSqlFragment(
            CoreIdentifierSqlRenderer.Render(identifier, renderer.Provider, allowWildcard = true),
            emptyBindings)

    let rec render
        (renderer: NativeSqlRenderer)
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (expression: SqlExpr) =

        match expression with
        | :? BoundColumnExpr as column -> renderIdentifier renderer column.Name
        | :? ColumnExpr as column -> renderIdentifier renderer column.Name

        | :? UnaryExpr as unary ->
            if unary.Operator <> "NOT" then
                raise (SqlCompilationException(
                    "Unsupported unary operator '" + unary.Operator + "'."))
            let operand = render renderer renderSubquery unary.Operand
            NativeSqlFragment("NOT (" + operand.Sql + ")", operand.Bindings)

        | :? BinaryExpr as binary ->
            if (binary.Operator = "IN" || binary.Operator = "NOT IN")
               && not (binary.Right :? SubqueryExpr) then
                raise (SqlCompilationException(
                    "Canonical binary IN/NOT IN requires a scalar subquery RHS; expression lists must use InExpr."))

            let left = render renderer renderSubquery binary.Left
            let right = render renderer renderSubquery binary.Right
            let likeEscape = CoreLikeEscapeSqlRenderer.RenderSuffix(binary, renderer.Provider)

            if binary.Operator = "%"
               && SqlModuloCapabilityRules.UsesFunctionSyntax(renderer.Provider) then
                combine ("MOD(" + left.Sql + ", " + right.Sql + ")") left right
            elif binary.Operator = "||"
                 && SqlConcatCapabilityRules.UsesConcatFunctionForCanonicalPipes(renderer.Provider) then
                combine ("CONCAT(" + left.Sql + ", " + right.Sql + ")") left right
            else
                let operatorText =
                    match binary.Operator with
                    | "+" | "-" | "*" | "/" | "%" | "||"
                    | "=" | "<>" | "!=" | ">" | "<" | ">=" | "<="
                    | "LIKE" | "ILIKE" | "AND" | "OR" | "IN" | "NOT IN" -> binary.Operator
                    | value ->
                        raise (SqlCompilationException(
                            "Unsupported binary operator '" + value + "'."))
                combine
                    ("(" + left.Sql + " " + operatorText + " " + right.Sql + likeEscape + ")")
                    left
                    right

        | :? FunctionCallExpr as functionCall ->
            let name = identifierText functionCall.Name
            let canonical = SqlCanonicalFunctionRegistry.Find(name.ToUpperInvariant())
            let loweringKind =
                match canonical with
                | null -> SqlCanonicalNativeLoweringKind.Ordinary
                | value -> value.NativeLoweringKind

            if loweringKind <> SqlCanonicalNativeLoweringKind.Ordinary then
                renderer.RenderExpressionForFunctional(expression, renderSubquery)
            else
                if not (safeFunctionName.IsMatch(name)) then
                    raise (SqlCompilationException(
                        "Unsafe function identifier '" + name + "'."))

                if name.StartsWith("CORE_", StringComparison.OrdinalIgnoreCase) then
                    raise (SqlCompilationException(
                        "Canonical function '" + name +
                        "' has no native lowering implementation; compilation was rejected."))

                let args =
                    functionCall.Arguments
                    |> Seq.map (render renderer renderSubquery)
                    |> Seq.toArray
                let renderedArgs = args |> Array.map (fun argument -> argument.Sql)

                if renderer.Provider = SqlAgentToolType.Postgres
                   && name.Equals("ROUND", StringComparison.OrdinalIgnoreCase)
                   && args.Length = 2 then
                    renderedArgs[0] <- "CAST(" + renderedArgs[0] + " AS numeric)"

                let argumentSql =
                    let joined = String.Join(", ", renderedArgs)
                    if functionCall.IsDistinct then "DISTINCT " + joined else joined

                let bindings =
                    args
                    |> Array.fold
                        (fun (current: ImmutableArray<obj | null>) (argument: NativeSqlFragment) ->
                            current.AddRange(argument.Bindings))
                        emptyBindings

                NativeSqlFragment(name + "(" + argumentSql + ")", bindings)

        | :? CastExpr as castExpr ->
            if not (safeCastType.IsMatch(castExpr.TypeName)) then
                raise (SqlCompilationException(
                    "Unsafe CAST target type '" + castExpr.TypeName + "'."))
            let rendered = render renderer renderSubquery castExpr.Expression
            NativeSqlFragment(
                "CAST(" + rendered.Sql + " AS " + castExpr.TypeName + ")",
                rendered.Bindings)

        | :? InExpr as inExpr ->
            if inExpr.Items.IsDefaultOrEmpty then
                raise (SqlCompilationException("IN requires at least one item."))

            let value = render renderer renderSubquery inExpr.Value
            let items =
                inExpr.Items
                |> Seq.map (render renderer renderSubquery)
                |> Seq.toArray
            let bindings =
                items
                |> Array.fold
                    (fun (current: ImmutableArray<obj | null>) (item: NativeSqlFragment) ->
                        current.AddRange(item.Bindings))
                    value.Bindings
            NativeSqlFragment(
                "(" + value.Sql + " " + (if inExpr.IsNegated then "NOT IN" else "IN") + " (" +
                String.Join(", ", items |> Array.map (fun item -> item.Sql)) + "))",
                bindings)

        | :? BetweenExpr as between ->
            let value = render renderer renderSubquery between.Value
            let lower = render renderer renderSubquery between.Lower
            let upper = render renderer renderSubquery between.Upper
            NativeSqlFragment(
                "(" + value.Sql + " " + (if between.IsNegated then "NOT BETWEEN" else "BETWEEN") +
                " " + lower.Sql + " AND " + upper.Sql + ")",
                value.Bindings.AddRange(lower.Bindings).AddRange(upper.Bindings))

        | :? IsNullExpr as isNull ->
            let value = render renderer renderSubquery isNull.Value
            NativeSqlFragment(
                "(" + value.Sql + " IS " + (if isNull.IsNegated then "NOT " else String.Empty) + "NULL)",
                value.Bindings)

        | :? SubqueryExpr as subquery ->
            validateScalarSubqueryProjection subquery.Query
            let rendered = renderSubquery.Invoke(subquery.Query)
            NativeSqlFragment("(" + rendered.Sql + ")", rendered.Bindings)

        | :? ExistsExpr as exists ->
            let rendered = renderSubquery.Invoke(exists.Query)
            NativeSqlFragment(
                (if exists.IsNegated then "NOT " else String.Empty) + "EXISTS (" + rendered.Sql + ")",
                rendered.Bindings)

        | _ -> renderer.RenderExpressionForFunctional(expression, renderSubquery)

    let rec renderPredicate
        (renderer: NativeSqlRenderer)
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (expression: SqlExpr) =

        if renderer.Provider = SqlAgentToolType.Oracle
           || renderer.Provider = SqlAgentToolType.MsSqlServer then
            match expression with
            | :? LiteralExpr as literal ->
                match literal.Value with
                | :? bool as value ->
                    NativeSqlFragment((if value then "(1 = 1)" else "(1 = 0)"), emptyBindings)
                | _ -> render renderer renderSubquery expression

            | :? UnaryExpr as unary when unary.Operator = "NOT" ->
                let operand = renderPredicate renderer renderSubquery unary.Operand
                NativeSqlFragment("NOT (" + operand.Sql + ")", operand.Bindings)

            | :? BinaryExpr as binary when binary.Operator = "AND" || binary.Operator = "OR" ->
                let left = renderPredicate renderer renderSubquery binary.Left
                let right = renderPredicate renderer renderSubquery binary.Right
                combine ("(" + left.Sql + " " + binary.Operator + " " + right.Sql + ")") left right

            | :? CaseExpr ->
                renderer.RenderPredicateForFunctional(expression, renderSubquery)

            | _ -> render renderer renderSubquery expression
        else
            render renderer renderSubquery expression
