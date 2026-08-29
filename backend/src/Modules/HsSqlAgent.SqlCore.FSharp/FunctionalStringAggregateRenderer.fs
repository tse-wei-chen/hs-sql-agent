namespace HsSqlAgent.SqlCore.Core.Lowering

open System
open System.Collections.Immutable
open System.Text
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums

/// F# ownership boundary for canonical portable string aggregation.
module internal FunctionalStringAggregateRenderer =

    let private emptyBindings = ImmutableArray<obj | null>.Empty

    let private requireArguments (functionCall: FunctionCallExpr) count =
        if functionCall.Arguments.Length <> count then
            let name = functionCall.Name.Parts |> Seq.map (fun part -> part.Value) |> String.concat "."
            raise (SqlCompilationException(
                "Canonical function '" + name + "' requires " + string count + " argument(s)."))

    let private stringLiteralValue (expression: SqlExpr) label =
        match expression with
        | :? LiteralExpr as literal ->
            match literal.Value with
            | :? string as value -> value
            | _ -> raise (SqlCompilationException(label + " must be a string literal."))
        | _ -> raise (SqlCompilationException(label + " must be a string literal."))

    let private sqlStringLiteral (expression: SqlExpr) label provider =
        let value = stringLiteralValue expression label
        if provider = SqlAgentToolType.MySQL
           && (value |> Seq.exists (fun character -> character = '\\' || Char.IsControl(character))) then
            "0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(value))
        else
            "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"

    let private renderOrderByClause
        (orderBy: ImmutableArray<OrderByItem>)
        (renderExpression: SqlExpr -> NativeSqlFragment) =
        let parts = ResizeArray<string>()
        let mutable bindings = emptyBindings
        for item in orderBy do
            let rendered: NativeSqlFragment = renderExpression item.Expression
            let nullOrdering =
                match item.NullOrdering with
                | NullOrderingKind.Default -> String.Empty
                | NullOrderingKind.First -> " NULLS FIRST"
                | NullOrderingKind.Last -> " NULLS LAST"
                | value ->
                    raise (SqlCompilationException(
                        "Unsupported NULL ordering '" + string value + "' in aggregate."))
            parts.Add(rendered.Sql + (if item.Descending then " DESC" else " ASC") + nullOrdering)
            bindings <- bindings.AddRange(rendered.Bindings)
        NativeSqlFragment("ORDER BY " + String.Join(", ", parts), bindings)

    let render
        (provider: SqlAgentToolType)
        (functionCall: FunctionCallExpr)
        (renderExpression: SqlExpr -> NativeSqlFragment) =

        requireArguments functionCall 2
        if functionCall.IsDistinct then
            raise (SqlCompilationException(
                "Canonical CORE_STRING_AGG DISTINCT semantics are not enabled."))

        let value: NativeSqlFragment = renderExpression functionCall.Arguments[0]
        let separator =
            if provider = SqlAgentToolType.Postgres then
                NativeSqlFragment(
                    NativeSqlParameterizer.Placeholder,
                    ImmutableArray.Create<obj | null>(box (stringLiteralValue functionCall.Arguments[1] "string aggregate separator")))
            else
                NativeSqlFragment(
                    sqlStringLiteral functionCall.Arguments[1] "string aggregate separator" provider,
                    emptyBindings)

        if not functionCall.AggregateOrderBy.IsDefaultOrEmpty then
            let ordering = renderOrderByClause functionCall.AggregateOrderBy renderExpression
            let sql =
                match provider with
                | SqlAgentToolType.Postgres ->
                    "STRING_AGG(" + value.Sql + ", " + separator.Sql + " " + ordering.Sql + ")"
                | SqlAgentToolType.Sqlite ->
                    "GROUP_CONCAT(" + value.Sql + ", " + separator.Sql + " " + ordering.Sql + ")"
                | SqlAgentToolType.MsSqlServer ->
                    "STRING_AGG(" + value.Sql + ", " + separator.Sql + ") WITHIN GROUP (" + ordering.Sql + ")"
                | SqlAgentToolType.Oracle ->
                    "LISTAGG(" + value.Sql + ", " + separator.Sql + ") WITHIN GROUP (" + ordering.Sql + ")"
                | SqlAgentToolType.MySQL ->
                    "GROUP_CONCAT(" + value.Sql + " " + ordering.Sql + " SEPARATOR " + separator.Sql + ")"
                | _ ->
                    raise (SqlCompilationException(
                        "Aggregate-local ORDER BY lowering is not supported by " + string provider + "."))
            NativeSqlFragment(
                sql,
                value.Bindings.AddRange(separator.Bindings).AddRange(ordering.Bindings))
        else
            let sql =
                match provider with
                | SqlAgentToolType.MsSqlServer
                | SqlAgentToolType.Postgres ->
                    "STRING_AGG(" + value.Sql + ", " + separator.Sql + ")"
                | SqlAgentToolType.MySQL ->
                    "GROUP_CONCAT(" + value.Sql + " SEPARATOR " + separator.Sql + ")"
                | SqlAgentToolType.Sqlite ->
                    "GROUP_CONCAT(" + value.Sql + ", " + separator.Sql + ")"
                | SqlAgentToolType.Oracle ->
                    "LISTAGG(" + value.Sql + ", " + separator.Sql + ")"
                | SqlAgentToolType.Firebird ->
                    "LIST(" + value.Sql + ", " + separator.Sql + ")"
                | _ -> raise (SqlCompilationException("Unsupported string aggregate provider."))
            NativeSqlFragment(sql, value.Bindings.AddRange(separator.Bindings))
