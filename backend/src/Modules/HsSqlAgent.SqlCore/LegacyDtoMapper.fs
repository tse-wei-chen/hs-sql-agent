
namespace HsSqlAgent.SqlCore.Core.Mapping

open System
open System.Collections.Generic
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models

[<AbstractClass; Sealed>]
type QueryDefinitionCoreMapper private () =
    static let unknown = SourceSpan.Unknown

    static member private Immutable<'T>(items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    static member private Identifier(value: string) =
        if String.IsNullOrWhiteSpace(value) then
            raise (InvalidOperationException("SQL identifier cannot be empty."))
        let parts =
            value.Split('.', StringSplitOptions.TrimEntries)
            |> Array.map (fun part ->
                if String.IsNullOrWhiteSpace(part) then
                    raise (InvalidOperationException("Invalid SQL identifier '" + value + "'."))
                IdentifierPart(part, false, unknown))
        SqlIdentifier(QueryDefinitionCoreMapper.Immutable(parts), unknown)

    static member private NormalizeAlias(alias: string | null) : IdentifierPart | null =
        match alias with
        | null -> null
        | value when String.IsNullOrWhiteSpace(value) -> null
        | value -> IdentifierPart(value.Trim(), false, unknown)

    static member private RequireAlias(alias: string | null, errorMessage: string) =
        match alias with
        | null -> raise (InvalidOperationException(errorMessage))
        | value when String.IsNullOrWhiteSpace(value) -> raise (InvalidOperationException(errorMessage))
        | value -> value.Trim()

    static member private MapOperator(op: ArithmeticOperator) =
        match op with
        | ArithmeticOperator.Add -> "+"
        | ArithmeticOperator.Subtract -> "-"
        | ArithmeticOperator.Multiply -> "*"
        | ArithmeticOperator.Divide -> "/"
        | ArithmeticOperator.Modulo -> "%"
        | ArithmeticOperator.Concat -> "||"
        | ArithmeticOperator.Equal -> "="
        | ArithmeticOperator.NotEqual -> "<>"
        | ArithmeticOperator.GreaterThan -> ">"
        | ArithmeticOperator.LessThan -> "<"
        | ArithmeticOperator.GreaterThanOrEqual -> ">="
        | ArithmeticOperator.LessThanOrEqual -> "<="
        | ArithmeticOperator.And -> "AND"
        | ArithmeticOperator.Or -> "OR"
        | value -> raise (ArgumentOutOfRangeException("op", value, "Unknown expression operator."))

    static member private NormalizeComparisonOperator(op: string) =
        let normalized =
            String.Join(
                " ",
                (if Object.ReferenceEquals(op, null) then "=" else op)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries))
                .ToUpperInvariant()
        match normalized with
        | "=" | "<>" | "!=" | ">" | "<" | ">=" | "<="
        | "LIKE" | "ILIKE" | "IN" | "NOT IN" | "BETWEEN" | "NOT BETWEEN"
        | "IS" | "IS NOT" | "EXISTS" | "NOT EXISTS" -> normalized
        | "ISNULL" -> "IS"
        | "ISNOTNULL" -> "IS NOT"
        | "NOTIN" -> "NOT IN"
        | "NOTBETWEEN" -> "NOT BETWEEN"
        | "NOTEXISTS" -> "NOT EXISTS"
        | _ -> raise (InvalidOperationException("Unsupported comparison operator '" + string op + "'."))

    static member private MapSetOperation(value: CombineType) =
        match value with
        | CombineType.Union -> SetOperationKind.Union
        | CombineType.UnionAll -> SetOperationKind.UnionAll
        | CombineType.Intersect -> SetOperationKind.Intersect
        | CombineType.Except -> SetOperationKind.Except
        | _ -> raise (ArgumentOutOfRangeException("value", value, "Unknown set operation."))

    static member Map(definition: QueryDefinition) : SqlStatement =
        ArgumentNullException.ThrowIfNull(definition)
        match definition.CombineConditions with
        | null ->
            QueryDefinitionCoreMapper.MapSelectStatement(definition, true) :> SqlStatement
        | conditions when conditions.Count = 0 ->
            QueryDefinitionCoreMapper.MapSelectStatement(definition, true) :> SqlStatement
        | conditions ->
            let head = QueryDefinitionCoreMapper.MapSelectStatement(definition, false)
            let operations =
                conditions
                |> Seq.map (fun c ->
                    SetOperation(
                        QueryDefinitionCoreMapper.MapSetOperation(c.Type),
                        QueryDefinitionCoreMapper.Map(c.Query),
                        unknown))
                |> QueryDefinitionCoreMapper.Immutable
            QueryStatement(
                head,
                operations,
                QueryDefinitionCoreMapper.MapOrderBy(definition.OrderByColumns),
                definition.Limit,
                definition.Offset,
                unknown) :> SqlStatement

    static member private MapSelectStatement(definition: QueryDefinition, includeTail: bool) =
        let ctes =
            match definition.CteConditions with
            | null -> ImmutableArray<CteDefinition>.Empty
            | conditions ->
                conditions
                |> Seq.map (fun c ->
                    CteDefinition(
                        QueryDefinitionCoreMapper.Identifier(c.CteAliasName),
                        ImmutableArray<SqlIdentifier>.Empty,
                        QueryDefinitionCoreMapper.Map(c.Query),
                        unknown))
                |> QueryDefinitionCoreMapper.Immutable
        let joins =
            match definition.Joins with
            | null -> ImmutableArray<JoinSource>.Empty
            | values -> values |> Seq.map QueryDefinitionCoreMapper.MapJoin |> QueryDefinitionCoreMapper.Immutable
        let projection =
            match definition.SelectColumns with
            | null -> ImmutableArray<SelectItem>.Empty
            | values -> values |> Seq.map QueryDefinitionCoreMapper.MapSelectItem |> QueryDefinitionCoreMapper.Immutable
        let groupBy =
            match definition.GroupByConditions with
            | null -> ImmutableArray<SqlExpr>.Empty
            | values -> values |> Seq.map QueryDefinitionCoreMapper.MapGroupBy |> QueryDefinitionCoreMapper.Immutable

        SelectStatement(
            ctes,
            definition.Distinct,
            projection,
            QueryDefinitionCoreMapper.MapSource(definition),
            joins,
            QueryDefinitionCoreMapper.MapWhereList(definition.WhereColumnsAndValues),
            groupBy,
            QueryDefinitionCoreMapper.MapHavingList(definition.HavingConditions),
            (if includeTail then QueryDefinitionCoreMapper.MapOrderBy(definition.OrderByColumns) else ImmutableArray<OrderByItem>.Empty),
            (if includeTail then definition.Limit else Nullable()),
            (if includeTail then definition.Offset else Nullable()),
            unknown)

    static member private MapSource(definition: QueryDefinition) : TableSource | null =
        match definition.FromQuery with
        | fromQuery when not (Object.ReferenceEquals(fromQuery, null)) ->
            let alias =
                match definition.Alias with
                | null -> fromQuery.Alias
                | value when String.IsNullOrWhiteSpace(value) -> fromQuery.Alias
                | value -> value
            let normalizedAlias =
                QueryDefinitionCoreMapper.RequireAlias(
                    alias,
                    "A derived table must have an explicit alias in the Core AST.")
            DerivedTableSource(QueryDefinitionCoreMapper.Map(fromQuery), normalizedAlias, unknown) :> TableSource
        | _ when String.IsNullOrWhiteSpace(definition.TableName) ->
            null
        | _ ->
            NamedTableSource(
                QueryDefinitionCoreMapper.Identifier(definition.TableName),
                QueryDefinitionCoreMapper.NormalizeAlias(definition.Alias),
                unknown) :> TableSource

    static member private MapJoin(join: JoinCondition) =
        let source : TableSource =
            match join.SubQuery with
            | subQuery when not (Object.ReferenceEquals(subQuery, null)) ->
                let alias =
                    QueryDefinitionCoreMapper.RequireAlias(
                        join.Alias,
                        "A joined derived table must have an explicit alias in the Core AST.")
                DerivedTableSource(QueryDefinitionCoreMapper.Map(subQuery), alias, unknown) :> TableSource
            | _ ->
                if String.IsNullOrWhiteSpace(join.Table) then
                    raise (InvalidOperationException("JOIN must specify either a table or a subquery."))
                NamedTableSource(
                    QueryDefinitionCoreMapper.Identifier(join.Table),
                    QueryDefinitionCoreMapper.NormalizeAlias(join.Alias),
                    unknown) :> TableSource

        let predicate = QueryDefinitionCoreMapper.MapWhereList(join.OnConditions)
        if join.Type <> JoinType.Cross && isNull predicate then
            raise (InvalidOperationException(string join.Type + " JOIN requires an ON predicate."))
        if join.Type = JoinType.Cross && not (isNull predicate) then
            raise (InvalidOperationException("CROSS JOIN must not carry an ON predicate."))
        JoinSource(join.Type.ToString().ToUpperInvariant(), source, predicate, unknown)

    static member private MapSelectItem(condition: SelectCondition) =
        SelectItem(
            QueryDefinitionCoreMapper.MapExpr(condition),
            QueryDefinitionCoreMapper.NormalizeAlias(condition.Alias),
            unknown)

    static member private MapFunction(
        name: string,
        arguments: IEnumerable<SelectCondition> | null,
        distinct: bool,
        filter: IReadOnlyCollection<WhereCondition> | null,
        window: WindowDefinition | null) =
        let args =
            match arguments with
            | null -> ImmutableArray<SqlExpr>.Empty
            | values -> values |> Seq.map QueryDefinitionCoreMapper.MapExpr |> QueryDefinitionCoreMapper.Immutable
        let mutable result : SqlExpr =
            FunctionCallExpr(
                QueryDefinitionCoreMapper.Identifier(name),
                args,
                distinct,
                unknown) :> SqlExpr
        match filter with
        | null -> ()
        | values when values.Count = 0 -> ()
        | values ->
            match QueryDefinitionCoreMapper.MapWhereList(List<WhereCondition>(values)) with
            | null -> raise (InvalidOperationException("Function FILTER for '" + name + "' cannot be empty."))
            | predicate -> result <- FilterExpr(result, predicate, unknown) :> SqlExpr
        match window with
        | null -> ()
        | value -> result <- WindowedExpr(result, QueryDefinitionCoreMapper.MapWindow(value), unknown) :> SqlExpr
        result

    static member private MapExpr(condition: SelectCondition) : SqlExpr =
        match condition with
        | :? FieldSelectCondition as field ->
            ColumnExpr(QueryDefinitionCoreMapper.Identifier(field.FieldName), unknown) :> SqlExpr
        | :? ConstantSelectCondition as constant ->
            LiteralExpr(constant.Constant, unknown) :> SqlExpr
        | :? OperationSelectCondition as operation ->
            BinaryExpr(
                QueryDefinitionCoreMapper.MapExpr(operation.Left),
                QueryDefinitionCoreMapper.MapOperator(operation.Operator),
                QueryDefinitionCoreMapper.MapExpr(operation.Right),
                unknown) :> SqlExpr
        | :? FunctionSelectCondition as fn ->
            QueryDefinitionCoreMapper.MapFunction(
                fn.FunctionName,
                fn.Arguments,
                fn.IsDistinct,
                fn.FilterWhereConditions,
                fn.Window)
        | :? CastSelectCondition as cast ->
            CastExpr(QueryDefinitionCoreMapper.MapExpr(cast.Expression), cast.TypeName, unknown) :> SqlExpr
        | :? IntervalSelectCondition as interval ->
            IntervalExpr(interval.Literal, unknown) :> SqlExpr
        | :? CaseWhenSelectCondition as caseExpr ->
            let branches =
                caseExpr.CaseWhen
                |> Seq.map (fun c ->
                    CaseBranch(
                        QueryDefinitionCoreMapper.MapWhere(c.Condition),
                        LiteralExpr(c.Value, unknown)))
                |> QueryDefinitionCoreMapper.Immutable
            let elseExpression : SqlExpr | null =
                match caseExpr.ElseValue with
                | null -> null
                | value -> LiteralExpr(value, unknown) :> SqlExpr
            CaseExpr(branches, elseExpression, unknown) :> SqlExpr
        | :? SubQuerySelectCondition as subquery ->
            SubqueryExpr(QueryDefinitionCoreMapper.Map(QueryDefinitionCoreMapper.ToDefinition(subquery)), unknown) :> SqlExpr
        | :? TemplateSqlTokenSelectCondition as token ->
            QueryDefinitionCoreMapper.MapTemplateToken(token)
        | value ->
            raise (InvalidOperationException("Unsupported SELECT expression for Core AST mapping: " + value.GetType().Name))

    static member private MapTemplateToken(token: TemplateSqlTokenSelectCondition) =
        let value = token.Token.Replace("_", String.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant()
        match value with
        | "CURRENTDATE" -> QueryDefinitionCoreMapper.MapFunction("CURRENT_DATE", null, false, null, null)
        | "CURRENTTIME" -> QueryDefinitionCoreMapper.MapFunction("CURRENT_TIME", null, false, null, null)
        | "CURRENTTIMESTAMP" -> QueryDefinitionCoreMapper.MapFunction("CURRENT_TIMESTAMP", null, false, null, null)
        | "SYSDATE" -> QueryDefinitionCoreMapper.MapFunction("SYSDATE", null, false, null, null)
        | "DAY" | "WEEK" | "MONTH" | "QUARTER" | "YEAR" | "HOUR" | "MINUTE" | "SECOND" ->
            ColumnExpr(QueryDefinitionCoreMapper.Identifier(value), unknown) :> SqlExpr
        | _ -> raise (SqlCompilationException("Unsupported SQL template token '" + token.Token + "'."))

    static member private MapWindow(window: WindowDefinition) =
        let partition =
            match window.PartitionBy with
            | null -> ImmutableArray<SqlExpr>.Empty
            | values -> values |> Seq.map QueryDefinitionCoreMapper.MapGroupBy |> QueryDefinitionCoreMapper.Immutable
        let frame : WindowFrame | null =
            match window.Frame with
            | null -> null
            | value -> QueryDefinitionCoreMapper.MapWindowFrame(value)
        WindowSpec(
            partition,
            QueryDefinitionCoreMapper.MapOrderBy(window.OrderBy),
            frame,
            unknown)

    static member private MapWindowFrame(frame: WindowFrameDefinition) =
        let unitKind =
            match frame.Unit with
            | WindowFrameUnit.Rows -> WindowFrameUnitKind.Rows
            | WindowFrameUnit.Range -> WindowFrameUnitKind.Range
            | value -> raise (ArgumentOutOfRangeException("frame.Unit", value, "Unknown window frame unit."))
        let endBound : WindowFrameBoundCore | null =
            match frame.End with
            | null -> null
            | value -> QueryDefinitionCoreMapper.MapWindowBound(value)
        WindowFrame(
            unitKind,
            QueryDefinitionCoreMapper.MapWindowBound(frame.Start),
            endBound,
            unknown)

    static member private MapWindowBound(bound: WindowFrameBound) =
        let kind =
            match bound.Kind with
            | WindowFrameBoundKind.UnboundedPreceding -> WindowFrameBoundKindCore.UnboundedPreceding
            | WindowFrameBoundKind.Preceding -> WindowFrameBoundKindCore.Preceding
            | WindowFrameBoundKind.CurrentRow -> WindowFrameBoundKindCore.CurrentRow
            | WindowFrameBoundKind.Following -> WindowFrameBoundKindCore.Following
            | WindowFrameBoundKind.UnboundedFollowing -> WindowFrameBoundKindCore.UnboundedFollowing
            | value -> raise (ArgumentOutOfRangeException("bound.Kind", value, "Unknown window frame bound."))
        if (kind = WindowFrameBoundKindCore.Preceding || kind = WindowFrameBoundKindCore.Following)
           && (not bound.Offset.HasValue || bound.Offset.Value < 0) then
            raise (InvalidOperationException("Window frame bound '" + string bound.Kind + "' requires a non-negative offset."))
        if kind <> WindowFrameBoundKindCore.Preceding && kind <> WindowFrameBoundKindCore.Following
           && bound.Offset.HasValue then
            raise (InvalidOperationException("Window frame bound '" + string bound.Kind + "' must not carry an offset."))
        WindowFrameBoundCore(kind, bound.Offset, unknown)

    static member private MapWhereList(conditions: IReadOnlyList<WhereCondition> | null) : SqlExpr | null =
        match conditions with
        | null -> null
        | values when values.Count = 0 -> null
        | values ->
            let mutable result : SqlExpr | null = null
            for condition in values do
                let current = QueryDefinitionCoreMapper.MapWhere(condition)
                result <-
                    match result with
                    | null -> current
                    | previous -> BinaryExpr(previous, (if condition.IsOr then "OR" else "AND"), current, unknown) :> SqlExpr
            result

    static member private MapWhere(condition: WhereCondition) : SqlExpr =
        let mutable result : SqlExpr =
            match condition with
            | :? BasicWhereCondition as basic -> QueryDefinitionCoreMapper.MapBasicWhere(basic)
            | :? ColumnCompareWhereCondition as compare ->
                BinaryExpr(
                    ColumnExpr(QueryDefinitionCoreMapper.Identifier(compare.LeftFieldName), unknown),
                    QueryDefinitionCoreMapper.NormalizeComparisonOperator(compare.Operator),
                    ColumnExpr(QueryDefinitionCoreMapper.Identifier(compare.RightFieldName), unknown),
                    unknown) :> SqlExpr
            | :? ExpressionWhereCondition as expression ->
                QueryDefinitionCoreMapper.MapExpressionPredicate(
                    expression.LeftExpression,
                    expression.Operator,
                    expression.RightExpression)
            | :? GroupWhereCondition as group ->
                match QueryDefinitionCoreMapper.MapWhereList(group.Groups) with
                | null -> raise (InvalidOperationException("Empty WHERE groups are not valid Core predicates."))
                | mapped -> mapped
            | :? SubQueryWhereCondition as subquery ->
                QueryDefinitionCoreMapper.MapSubQueryWhere(subquery)
            | value ->
                raise (InvalidOperationException("Unsupported WHERE node for Core AST mapping: " + value.GetType().Name))
        if condition.IsNot then result <- UnaryExpr("NOT", result, unknown) :> SqlExpr
        result

    static member private MapExpressionPredicate(left: SelectCondition, opText: string, right: SelectCondition | null) =
        let op = QueryDefinitionCoreMapper.NormalizeComparisonOperator(opText)
        let leftExpr = QueryDefinitionCoreMapper.MapExpr(left)
        match right with
        | null ->
            if op = "IS" || op = "IS NOT" then IsNullExpr(leftExpr, (op = "IS NOT"), unknown) :> SqlExpr
            else raise (InvalidOperationException("Predicate operator '" + op + "' requires a right-hand expression."))
        | rightExpression ->
            BinaryExpr(leftExpr, op, QueryDefinitionCoreMapper.MapExpr(rightExpression), unknown) :> SqlExpr

    static member private MapBasicWhere(basic: BasicWhereCondition) =
        if String.IsNullOrWhiteSpace(basic.FieldName) then
            raise (InvalidOperationException("WHERE field name cannot be empty."))
        let field = ColumnExpr(QueryDefinitionCoreMapper.Identifier(basic.FieldName), unknown) :> SqlExpr
        let op = QueryDefinitionCoreMapper.NormalizeComparisonOperator(basic.Operator)
        if op = "IN" || op = "NOT IN" then
            if basic.Values.Count = 0 then raise (InvalidOperationException(op + " requires at least one value."))
            InExpr(
                field,
                basic.Values |> Seq.map (fun value -> LiteralExpr(value, unknown) :> SqlExpr) |> QueryDefinitionCoreMapper.Immutable,
                (op = "NOT IN"),
                unknown) :> SqlExpr
        elif op = "BETWEEN" || op = "NOT BETWEEN" then
            let values =
                match basic.Value with
                | :? IEnumerable<obj> as values -> values |> Seq.truncate 3 |> Seq.toArray
                | _ -> [||]
            if values.Length <> 2 then raise (InvalidOperationException(op + " requires exactly two values."))
            BetweenExpr(field, LiteralExpr(values[0], unknown), LiteralExpr(values[1], unknown), (op = "NOT BETWEEN"), unknown) :> SqlExpr
        elif (op = "IS" || op = "IS NOT") && isNull basic.Value then
            IsNullExpr(field, (op = "IS NOT"), unknown) :> SqlExpr
        elif op = "IS" || op = "IS NOT" then
            raise (InvalidOperationException(op + " currently supports NULL only in the Core AST."))
        else
            BinaryExpr(field, op, LiteralExpr(basic.Value, unknown), unknown) :> SqlExpr

    static member private MapSubQueryWhere(subquery: SubQueryWhereCondition) =
        let op = QueryDefinitionCoreMapper.NormalizeComparisonOperator(subquery.Operator)
        let mapped = QueryDefinitionCoreMapper.Map(subquery.SubQuery)
        if op = "EXISTS" || op = "NOT EXISTS" then
            ExistsExpr(mapped, (op = "NOT EXISTS"), unknown) :> SqlExpr
        elif op <> "IN" && op <> "NOT IN" then
            raise (InvalidOperationException("Unsupported subquery predicate operator '" + subquery.Operator + "'."))
        else
            match subquery.FieldName with
            | null ->
                raise (InvalidOperationException(op + " subquery predicate requires a field name."))
            | fieldName when String.IsNullOrWhiteSpace(fieldName) ->
                raise (InvalidOperationException(op + " subquery predicate requires a field name."))
            | fieldName ->
                BinaryExpr(
                    ColumnExpr(QueryDefinitionCoreMapper.Identifier(fieldName), unknown),
                    op,
                    SubqueryExpr(mapped, unknown),
                    unknown) :> SqlExpr

    static member private MapGroupBy(condition: GroupByCondition) =
        match condition with
        | :? FieldGroupByCondition as field ->
            ColumnExpr(QueryDefinitionCoreMapper.Identifier(field.FieldName), unknown) :> SqlExpr
        | :? FunctionGroupByCondition as fn ->
            QueryDefinitionCoreMapper.MapFunction(fn.FunctionName, fn.Arguments, fn.IsDistinct, fn.FilterWhereConditions, null)
        | value ->
            raise (InvalidOperationException("Unsupported GROUP BY node for Core AST mapping: " + value.GetType().Name))

    static member private MapHavingList(conditions: IReadOnlyList<HavingCondition> | null) : SqlExpr | null =
        match conditions with
        | null -> null
        | values when values.Count = 0 -> null
        | values ->
            let mutable result : SqlExpr | null = null
            for condition in values do
                let current = QueryDefinitionCoreMapper.MapHaving(condition)
                result <-
                    match result with
                    | null -> current
                    | previous -> BinaryExpr(previous, (if condition.IsOr then "OR" else "AND"), current, unknown) :> SqlExpr
            result

    static member private MapHaving(condition: HavingCondition) =
        let mutable result : SqlExpr =
            match condition with
            | :? BasicHavingCondition as basic ->
                let left = ColumnExpr(QueryDefinitionCoreMapper.Identifier(basic.FieldName), unknown) :> SqlExpr
                let op = QueryDefinitionCoreMapper.NormalizeComparisonOperator(basic.Operator)
                if (op = "IS" || op = "IS NOT") && isNull basic.Value then IsNullExpr(left, (op = "IS NOT"), unknown) :> SqlExpr
                elif op = "IS" || op = "IS NOT" then raise (InvalidOperationException(op + " currently supports NULL only in the Core AST."))
                else BinaryExpr(left, op, LiteralExpr(basic.Value, unknown), unknown) :> SqlExpr
            | :? FunctionHavingCondition as fn ->
                let left = QueryDefinitionCoreMapper.MapFunction(
                    fn.LeftFunction.FunctionName,
                    fn.LeftFunction.Arguments,
                    fn.LeftFunction.IsDistinct,
                    fn.LeftFunction.FilterWhereConditions,
                    fn.LeftFunction.Window)
                let op = QueryDefinitionCoreMapper.NormalizeComparisonOperator(fn.Operator)
                if (op = "IS" || op = "IS NOT") && isNull fn.Value then IsNullExpr(left, (op = "IS NOT"), unknown) :> SqlExpr
                elif op = "IS" || op = "IS NOT" then raise (InvalidOperationException(op + " currently supports NULL only in the Core AST."))
                else BinaryExpr(left, op, LiteralExpr(fn.Value, unknown), unknown) :> SqlExpr
            | :? ExpressionHavingCondition as expression ->
                QueryDefinitionCoreMapper.MapExpressionPredicate(expression.LeftExpression, expression.Operator, expression.RightExpression)
            | :? GroupHavingCondition as group ->
                match QueryDefinitionCoreMapper.MapHavingList(group.Groups) with
                | null -> raise (InvalidOperationException("Empty HAVING groups are not valid Core predicates."))
                | mapped -> mapped
            | value ->
                raise (InvalidOperationException("Unsupported HAVING node for Core AST mapping: " + value.GetType().Name))
        if condition.IsNot then result <- UnaryExpr("NOT", result, unknown) :> SqlExpr
        result

    static member private MapOrderBy(conditions: IEnumerable<OrderByCondition> | null) =
        match conditions with
        | null -> ImmutableArray<OrderByItem>.Empty
        | values ->
            values
            |> Seq.map (fun condition ->
                let expression : SqlExpr =
                    match condition with
                    | :? FieldOrderByCondition as field ->
                        ColumnExpr(QueryDefinitionCoreMapper.Identifier(field.FieldName), unknown) :> SqlExpr
                    | :? FunctionOrderByCondition as fn ->
                        QueryDefinitionCoreMapper.MapFunction(fn.FunctionName, fn.Arguments, fn.IsDistinct, fn.FilterWhereConditions, null)
                    | value ->
                        raise (InvalidOperationException("Unsupported ORDER BY node for Core AST mapping: " + value.GetType().Name))
                let nullOrdering =
                    match condition.NullOrdering with
                    | HsSqlAgent.SqlCore.Enums.NullOrdering.Default -> NullOrderingKind.Default
                    | HsSqlAgent.SqlCore.Enums.NullOrdering.First -> NullOrderingKind.First
                    | HsSqlAgent.SqlCore.Enums.NullOrdering.Last -> NullOrderingKind.Last
                    | value -> raise (ArgumentOutOfRangeException("condition.NullOrdering", value, "Unknown null ordering."))
                OrderByItem(expression, (condition.Direction = SortDirection.Desc), nullOrdering, unknown))
            |> QueryDefinitionCoreMapper.Immutable

    static member private ToDefinition(source: SubQuerySelectCondition) =
        let definition = QueryDefinition()
        definition.TableName <- source.TableName
        definition.FromQuery <- source.FromQuery
        definition.Alias <- source.Alias
        definition.Distinct <- source.Distinct
        definition.SelectColumns <- source.SelectColumns
        definition.WhereColumnsAndValues <- source.WhereColumnsAndValues
        definition.OrderByColumns <- source.OrderByColumns
        definition.GroupByConditions <- source.GroupByConditions
        definition.HavingConditions <- source.HavingConditions
        definition.Joins <- source.Joins
        definition.CombineConditions <- source.CombineConditions
        definition.CteConditions <- source.CteConditions
        definition.Limit <- source.Limit
        definition.Offset <- source.Offset
        definition
