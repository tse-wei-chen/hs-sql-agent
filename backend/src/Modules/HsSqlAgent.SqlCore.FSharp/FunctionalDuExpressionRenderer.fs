namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open System.Globalization
open HsSqlAgent.SqlCore.Core.Analysis
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

/// Native expression entry point backed by the closed F# expression shape.
/// Complex cases still delegate to the proven renderer while leaf/unary/binary
/// lowering is owned here and recursively stays inside the DU path.
module internal FunctionalDuExpressionRenderer =

    let private emptyBindings = ImmutableArray<obj | null>.Empty
    let private parameterPlaceholder = NativeSqlParameterizer.Placeholder

    let private combine sql (left: NativeSqlFragment) (right: NativeSqlFragment) =
        NativeSqlFragment(sql, left.Bindings.AddRange(right.Bindings))

    let private bind (value: obj | null) =
        NativeSqlFragment(parameterPlaceholder, ImmutableArray.Create<obj | null>(value))

    let private castBinding (castType: string) (value: obj | null) =
        NativeSqlFragment(
            "CAST(" + parameterPlaceholder + " AS " + castType + ")",
            ImmutableArray.Create<obj | null>(value))

    let private renderIdentifier provider (identifier: SqlIdentifier) =
        NativeSqlFragment(
            CoreIdentifierSqlRenderer.Render(identifier, provider, allowWildcard = true),
            emptyBindings)

    let private formatFirebirdOffsetTimestamp (value: DateTimeOffset) =
        value.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture)

    let private renderFirebirdString (value: string) =
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

    let rec render
        (provider: SqlAgentToolType)
        (renderSubquery: Func<SqlStatement, NativeSqlFragment>)
        (expression: SqlExpr) =

        match FunctionalExpressionShape.ofSqlExpr expression with
        | FunctionalExpressionShape.BoundColumn column ->
            renderIdentifier provider column.Name
        | FunctionalExpressionShape.Column column ->
            renderIdentifier provider column.Name
        | FunctionalExpressionShape.Literal literal ->
            renderLiteral provider literal
        | FunctionalExpressionShape.Interval interval ->
            renderInterval provider interval
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
