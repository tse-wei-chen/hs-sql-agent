namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Analysis
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Native expression entry point backed by the closed F# expression shape.
/// Complex cases still delegate to the proven renderer while leaf/unary/binary
/// lowering is owned here and recursively stays inside the DU path.
module internal FunctionalDuExpressionRenderer =

    let private emptyBindings = ImmutableArray<obj | null>.Empty

    let private combine sql (left: NativeSqlFragment) (right: NativeSqlFragment) =
        NativeSqlFragment(sql, left.Bindings.AddRange(right.Bindings))

    let private renderIdentifier provider (identifier: SqlIdentifier) =
        NativeSqlFragment(
            CoreIdentifierSqlRenderer.Render(identifier, provider, allowWildcard = true),
            emptyBindings)

    let rec render
        (provider: SqlAgentToolType)
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (expression: SqlExpr) =

        match FunctionalExpressionShape.ofSqlExpr expression with
        | FunctionalExpressionShape.BoundColumn column ->
            renderIdentifier provider column.Name
        | FunctionalExpressionShape.Column column ->
            renderIdentifier provider column.Name
        | FunctionalExpressionShape.Literal _
        | FunctionalExpressionShape.Interval _ ->
            FunctionalNativeExpressionRenderer.render provider renderSubquery expression
        | FunctionalExpressionShape.Unary unary ->
            if unary.Operator <> "NOT" then
                raise (SqlCompilationException("Unsupported unary operator '" + unary.Operator + "'."))
            let operand = render provider renderSubquery unary.Operand
            NativeSqlFragment("NOT (" + operand.Sql + ")", operand.Bindings)
        | FunctionalExpressionShape.Binary binary ->
            if (binary.Operator = "IN" || binary.Operator = "NOT IN")
               && not (binary.Right :? SubqueryExpr) then
                raise (SqlCompilationException(
                    "Canonical binary IN/NOT IN requires a scalar subquery RHS; expression lists must use InExpr."))

            let left = render provider renderSubquery binary.Left
            let right = render provider renderSubquery binary.Right
            let likeEscape = CoreLikeEscapeSqlRenderer.RenderSuffix(binary, provider)

            if binary.Operator = "%" && SqlModuloCapabilityRules.UsesFunctionSyntax(provider) then
                combine ("MOD(" + left.Sql + ", " + right.Sql + ")") left right
            elif binary.Operator = "||"
                 && SqlConcatCapabilityRules.UsesConcatFunctionForCanonicalPipes(provider) then
                combine ("CONCAT(" + left.Sql + ", " + right.Sql + ")") left right
            else
                let operatorText =
                    match binary.Operator with
                    | "+" | "-" | "*" | "/" | "%" | "||"
                    | "=" | "<>" | "!=" | ">" | "<" | ">=" | "<="
                    | "LIKE" | "ILIKE" | "AND" | "OR" | "IN" | "NOT IN" -> binary.Operator
                    | value ->
                        raise (SqlCompilationException("Unsupported binary operator '" + value + "'."))
                combine
                    ("(" + left.Sql + " " + operatorText + " " + right.Sql + likeEscape + ")")
                    left
                    right
        | FunctionalExpressionShape.FunctionCall _
        | FunctionalExpressionShape.Filter _
        | FunctionalExpressionShape.Windowed _
        | FunctionalExpressionShape.Cast _
        | FunctionalExpressionShape.SimpleCase _
        | FunctionalExpressionShape.SearchedCase _
        | FunctionalExpressionShape.InList _
        | FunctionalExpressionShape.Between _
        | FunctionalExpressionShape.IsNull _
        | FunctionalExpressionShape.ScalarSubquery _
        | FunctionalExpressionShape.Exists _ ->
            FunctionalNativeExpressionRenderer.render provider renderSubquery expression
        | FunctionalExpressionShape.Unsupported unsupported ->
            raise (SqlCompilationException(
                "Unsupported expression during F# DU lowering: " + unsupported.GetType().Name))
