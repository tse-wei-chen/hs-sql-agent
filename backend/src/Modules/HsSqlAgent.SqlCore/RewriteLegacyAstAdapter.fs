#nowarn "3261" "3262"

namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Text.Json
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// One-way compatibility seam from the historical public CLR AST into the closed rewrite DU.
/// No compiler stage is implemented here: after conversion every path uses the same typestate pipeline.
module internal RewriteLegacyAstAdapter =

    let private failClosed context (node: obj | null) =
        let nodeName =
            match node with
            | null -> "<null>"
            | value -> value.GetType().Name
        raise (SqlCompilationException(
            "Unsupported " + context + " at the legacy AST compatibility boundary: " + nodeName))

    let private spanOf (span: HsSqlAgent.SqlCore.Core.Ast.SourceSpan) : Span =
        { Start = span.Start
          Length = if span.Start < 0 || span.End < span.Start then 0 else span.End - span.Start }

    let private partOf (part: HsSqlAgent.SqlCore.Core.Ast.IdentifierPart) : IdentifierPart =
        { Value = part.Value
          WasQuoted = part.WasQuoted
          PreserveSpelling = part.PreserveSpelling
          Span = spanOf part.Span }

    let private identifierOf (identifier: HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier) =
        identifier.Parts
        |> Seq.map partOf
        |> Seq.toList
        |> Identifier.create

    let private singlePart context (identifier: HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier) =
        match identifier.Parts |> Seq.toList with
        | [ part ] -> partOf part
        | _ -> raise (SqlCompilationException(context + " requires a single-part identifier."))

    let private identifierText (identifier: HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier) =
        identifier.Parts |> Seq.map (fun part -> part.Value) |> String.concat "."

    let private jsonScalar (json: JsonElement) =
        match json.ValueKind with
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> ScalarValue.Null
        | JsonValueKind.True -> ScalarValue.Boolean true
        | JsonValueKind.False -> ScalarValue.Boolean false
        | JsonValueKind.String ->
            match json.GetString() with
            | null -> ScalarValue.Null
            | value -> ScalarValue.Text value
        | JsonValueKind.Number ->
            let mutable integer = 0L
            let mutable number = 0M
            if json.TryGetInt64(&integer) then ScalarValue.Integer integer
            elif json.TryGetDecimal(&number) then ScalarValue.Decimal number
            else ScalarValue.Floating(json.GetDouble())
        | _ ->
            raise (SqlCompilationException(
                "Legacy AST JSON literals must be scalar SQL values."))

    let private scalarValueOf (value: obj | null) =
        match value with
        | null -> ScalarValue.Null
        | :? bool as value -> ScalarValue.Boolean value
        | :? byte as value -> ScalarValue.Integer(int64 value)
        | :? sbyte as value -> ScalarValue.Integer(int64 value)
        | :? int16 as value -> ScalarValue.Integer(int64 value)
        | :? uint16 as value -> ScalarValue.Integer(int64 value)
        | :? int as value -> ScalarValue.Integer(int64 value)
        | :? uint32 as value -> ScalarValue.Integer(int64 value)
        | :? int64 as value -> ScalarValue.Integer value
        | :? uint64 as value when value <= uint64 Int64.MaxValue -> ScalarValue.Integer(int64 value)
        | :? uint64 as value -> ScalarValue.Decimal(decimal value)
        | :? decimal as value -> ScalarValue.Decimal value
        | :? double as value -> ScalarValue.Floating value
        | :? single as value -> ScalarValue.Floating(double value)
        | :? string as value -> ScalarValue.Text value
        | :? char as value -> ScalarValue.Text(string value)
        | :? DateOnly as value -> ScalarValue.Date value
        | :? TimeOnly as value -> ScalarValue.Time value
        | :? DateTime as value -> ScalarValue.LocalDateTime value
        | :? DateTimeOffset as value -> ScalarValue.OffsetDateTime value
        | :? TimeSpan as value -> ScalarValue.Duration value
        | :? (byte array) as value -> ScalarValue.Bytes value
        | :? SqlDateValue as value -> ScalarValue.Date value.Value
        | :? SqlTimeValue as value -> ScalarValue.Time value.Value
        | :? SqlLocalDateTimeValue as value -> ScalarValue.LocalDateTime value.Value
        | :? SqlOffsetDateTimeValue as value -> ScalarValue.OffsetDateTime value.Value
        | :? JsonElement as value -> jsonScalar value
        | value -> failClosed "literal value" value

    let private wildcardOf (identifier: HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier) =
        let parts = identifier.Parts |> Seq.toList
        match List.rev parts with
        | star :: reversedQualifier when star.Value = "*" && not star.WasQuoted ->
            match List.rev reversedQualifier with
            | [] -> Some(Wildcard None)
            | qualifier ->
                qualifier
                |> List.map partOf
                |> Identifier.create
                |> Some
                |> Wildcard
                |> Some
        | _ -> None

    let private escapeOf (escape: string | null) =
        match escape with
        | null -> None
        | value when value.Length = 1 -> Some(LikeEscape.create value[0])
        | _ -> raise (SqlCompilationException("LIKE ESCAPE requires exactly one non-control character."))

    let private binaryOperator (value: string) =
        match value.ToUpperInvariant() with
        | "+" -> Some BinaryOperator.Add
        | "-" -> Some BinaryOperator.Subtract
        | "*" -> Some BinaryOperator.Multiply
        | "/" -> Some BinaryOperator.Divide
        | "%" -> Some BinaryOperator.Modulo
        | "||"
        | "__CORE_MYSQL_PIPES_AS_CONCAT__" -> Some BinaryOperator.Concat
        | "=" -> Some BinaryOperator.Equal
        | "<>"
        | "!=" -> Some BinaryOperator.NotEqual
        | ">" -> Some BinaryOperator.GreaterThan
        | "<" -> Some BinaryOperator.LessThan
        | ">=" -> Some BinaryOperator.GreaterThanOrEqual
        | "<=" -> Some BinaryOperator.LessThanOrEqual
        | "AND" -> Some BinaryOperator.And
        | "OR" -> Some BinaryOperator.Or
        | _ -> None

    let private joinKind (value: string) =
        match value.ToUpperInvariant() with
        | "INNER" -> Choice1Of2 OnJoinKind.Inner
        | "LEFT" -> Choice1Of2 OnJoinKind.Left
        | "RIGHT" -> Choice1Of2 OnJoinKind.Right
        | "FULL" -> Choice1Of2 OnJoinKind.Full
        | "CROSS" -> Choice2Of2 ()
        | _ -> raise (SqlCompilationException("Unsupported JOIN kind '" + value + "'."))

    let private nullOrdering = function
        | HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.Default -> NullOrdering.Default
        | HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.First -> NullOrdering.NullsFirst
        | HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.Last -> NullOrdering.NullsLast
        | value -> raise (SqlCompilationException("Unsupported NULL ordering '" + string value + "'."))

    let private aggregateOrderSyntax = function
        | HsSqlAgent.SqlCore.Core.Ast.AggregateOrderSyntaxKind.None -> AggregateOrderSyntax.NoAggregateOrder
        | HsSqlAgent.SqlCore.Core.Ast.AggregateOrderSyntaxKind.Inline -> AggregateOrderSyntax.InlineAggregateOrder
        | HsSqlAgent.SqlCore.Core.Ast.AggregateOrderSyntaxKind.WithinGroup -> AggregateOrderSyntax.WithinGroupAggregateOrder
        | value -> raise (SqlCompilationException("Unsupported aggregate ORDER BY syntax '" + string value + "'."))

    let private frameBound (bound: HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundCore) =
        let offset () =
            if not bound.Offset.HasValue then
                raise (SqlCompilationException("Window frame PRECEDING/FOLLOWING requires an offset."))
            FrameOffset.create bound.Offset.Value
        match bound.Kind with
        | HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.UnboundedPreceding -> WindowFrameBound.UnboundedPreceding
        | HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.Preceding -> WindowFrameBound.Preceding(offset ())
        | HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.CurrentRow -> WindowFrameBound.CurrentRow
        | HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.Following -> WindowFrameBound.Following(offset ())
        | HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.UnboundedFollowing -> WindowFrameBound.UnboundedFollowing
        | value -> raise (SqlCompilationException("Unsupported window frame bound '" + string value + "'."))

    let rec private exprOf (expression: HsSqlAgent.SqlCore.Core.Ast.SqlExpr) : Expr =
        match expression with
        | :? HsSqlAgent.SqlCore.Core.Binding.BoundColumnExpr as column ->
            BoundColumn(
                identifierOf column.Name,
                if column.IsOuterReference then ColumnBinding.OuterRowSource else ColumnBinding.LocalRowSource)

        | :? HsSqlAgent.SqlCore.Core.Ast.LiteralExpr as literal ->
            match literal.Value with
            | :? HsSqlAgent.SqlCore.Core.Ast.OrderByOrdinalValue as ordinal ->
                if ordinal.Position <= 0 then
                    raise (SqlCompilationException(
                        "ORDER BY output position must be positive; received " + string ordinal.Position + "."))
                OrderOrdinal(PositiveRowCount.create ordinal.Position)
            | _ ->
                Literal(scalarValueOf literal.Value)

        | :? HsSqlAgent.SqlCore.Core.Ast.ColumnExpr as column ->
            wildcardOf column.Name
            |> Option.defaultWith (fun () -> Column(identifierOf column.Name))

        | :? HsSqlAgent.SqlCore.Core.Ast.IntervalExpr as interval ->
            Interval(IntervalLiteral.create interval.Literal)

        | :? HsSqlAgent.SqlCore.Core.Ast.UnaryExpr as unary ->
            let operand = exprOf unary.Operand
            match unary.Operator.ToUpperInvariant() with
            | "NOT" -> Unary(UnaryOperator.Not, operand)
            | "-" -> Unary(UnaryOperator.Negate, operand)
            | "+" -> Unary(UnaryOperator.Positive, operand)
            | _ -> failClosed ("unary operator '" + unary.Operator + "'") unary

        | :? HsSqlAgent.SqlCore.Core.Ast.BinaryExpr as binary ->
            let left = exprOf binary.Left
            let right = exprOf binary.Right
            match binary.Operator.ToUpperInvariant(), binaryOperator binary.Operator with
            | "LIKE", _ -> Like(left, right, escapeOf binary.LikeEscape, false, false)
            | "NOT LIKE", _ -> Like(left, right, escapeOf binary.LikeEscape, true, false)
            | "ILIKE", _ -> Like(left, right, escapeOf binary.LikeEscape, false, true)
            | "NOT ILIKE", _ -> Like(left, right, escapeOf binary.LikeEscape, true, true)
            | "IN", _ ->
                match binary.Right with
                | :? HsSqlAgent.SqlCore.Core.Ast.SubqueryExpr as subquery ->
                    InSubquery(left, queryOfStatement subquery.Query, false)
                | _ ->
                    raise (SqlCompilationException(
                        "Canonical binary IN/NOT IN requires a scalar subquery RHS; expression lists must use InExpr."))
            | "NOT IN", _ ->
                match binary.Right with
                | :? HsSqlAgent.SqlCore.Core.Ast.SubqueryExpr as subquery ->
                    InSubquery(left, queryOfStatement subquery.Query, true)
                | _ ->
                    raise (SqlCompilationException(
                        "Canonical binary IN/NOT IN requires a scalar subquery RHS; expression lists must use InExpr."))
            | _, Some operator -> Binary(operator, left, right)
            | _ -> failClosed ("binary operator '" + binary.Operator + "'") binary

        | :? HsSqlAgent.SqlCore.Core.Ast.FunctionCallExpr as functionCall ->
            let arguments = functionCall.Arguments |> Seq.map exprOf |> Seq.toList
            let identifier = identifierOf functionCall.Name
            let name = Identifier.text identifier
            if name.Equals("REGEXP_LIKE", StringComparison.OrdinalIgnoreCase)
               && functionCall.Name.Parts |> Seq.forall (fun part -> not part.WasQuoted) then
                RawRegexCall(arguments, functionCall.IsDistinct)
            else
                FunctionCall
                    { Name = FunctionName.ofIdentifier identifier
                      Arguments = arguments
                      IsDistinct = functionCall.IsDistinct
                      AggregateOrderBy = functionCall.AggregateOrderBy |> Seq.map orderByOf |> Seq.toList
                      AggregateOrderSyntax = aggregateOrderSyntax functionCall.AggregateOrderSyntax
                      AggregateSeparator = Option.ofObj functionCall.AggregateSeparatorClause }

        | :? HsSqlAgent.SqlCore.Core.Ast.FilterExpr as filter ->
            FilteredAggregate(exprOf filter.Expression, exprOf filter.Predicate)

        | :? HsSqlAgent.SqlCore.Core.Ast.WindowedExpr as windowed ->
            Windowed(exprOf windowed.Expression, windowOf windowed.Window)

        | :? HsSqlAgent.SqlCore.Core.Ast.CastExpr as cast ->
            Cast(exprOf cast.Expression, CastType.create cast.TypeName)

        | :? HsSqlAgent.SqlCore.Core.Ast.ExtractExpr as extract ->
            Extract(ExtractField.create extract.Field, exprOf extract.Expression)

        | :? HsSqlAgent.SqlCore.Core.Ast.SimpleCaseExpr as simpleCase ->
            simpleCaseOf simpleCase

        | :? HsSqlAgent.SqlCore.Core.Ast.CaseExpr as searchedCase ->
            let converted =
                searchedCase.Branches
                |> Seq.map (fun branch ->
                    { SearchedCaseBranch.Condition = exprOf branch.Condition
                      Result = exprOf branch.Value })
                |> Seq.toList
            let branches =
                match converted with
                | [] -> raise (SqlCompilationException("Searched CASE requires at least one WHEN branch."))
                | head :: tail -> NonEmpty.create head tail
            SearchedCase(branches, Option.ofObj searchedCase.ElseExpression |> Option.map exprOf)

        | :? HsSqlAgent.SqlCore.Core.Ast.InExpr as inExpression ->
            InList(
                exprOf inExpression.Value,
                inExpression.Items |> Seq.map exprOf |> Seq.toList |> NonEmpty.ofList "items",
                inExpression.IsNegated)

        | :? HsSqlAgent.SqlCore.Core.Ast.BetweenExpr as between ->
            Between(
                exprOf between.Value,
                exprOf between.Lower,
                exprOf between.Upper,
                between.IsNegated)

        | :? HsSqlAgent.SqlCore.Core.Ast.IsNullExpr as isNull ->
            IsNull(exprOf isNull.Value, isNull.IsNegated)

        | :? HsSqlAgent.SqlCore.Core.Ast.SubqueryExpr as subquery ->
            ScalarSubquery(queryOfStatement subquery.Query)

        | :? HsSqlAgent.SqlCore.Core.Ast.ExistsExpr as exists ->
            Exists(queryOfStatement exists.Query, exists.IsNegated)

        | _ -> failClosed "expression node" expression

    and private simpleCaseOf (simpleCase: HsSqlAgent.SqlCore.Core.Ast.SimpleCaseExpr) =
        let converted =
            simpleCase.Branches
            |> Seq.map (fun branch ->
                match branch.Condition with
                | :? HsSqlAgent.SqlCore.Core.Ast.BinaryExpr as equality
                    when equality.Operator.Equals("=", StringComparison.OrdinalIgnoreCase) ->
                    equality.Left, exprOf equality.Left, exprOf equality.Right, exprOf branch.Value
                | _ -> failClosed "simple CASE compatibility branch" branch.Condition)
            |> Seq.toList

        match converted with
        | [] -> raise (SqlCompilationException("Simple CASE requires at least one WHEN branch."))
        | (legacyInput, input, firstMatch, firstResult) :: tail ->
            let branches =
                { SimpleCaseBranch.Match = firstMatch; Result = firstResult }
                :: (tail
                    |> List.map (fun (candidateLegacyInput, candidateInput, matchValue, result) ->
                        if not (Object.Equals(legacyInput, candidateLegacyInput))
                           && not (Expr.equivalent input candidateInput) then
                            raise (SqlCompilationException(
                                "Simple CASE branches must share one canonical operand."))
                        { SimpleCaseBranch.Match = matchValue; Result = result }))
                |> NonEmpty.ofList "branches"
            SimpleCase(
                input,
                branches,
                Option.ofObj simpleCase.ElseExpression |> Option.map exprOf)

    and private orderByOf (order: HsSqlAgent.SqlCore.Core.Ast.OrderByItem) =
        { OrderBy.Expression = exprOf order.Expression
          Descending = order.Descending
          NullOrdering = nullOrdering order.NullOrdering }

    and private windowOf (window: HsSqlAgent.SqlCore.Core.Ast.WindowSpec) =
        let frame =
            Option.ofObj window.Frame
            |> Option.map (fun frame ->
                let unit =
                    match frame.Unit with
                    | HsSqlAgent.SqlCore.Core.Ast.WindowFrameUnitKind.Rows -> WindowFrameUnit.Rows
                    | HsSqlAgent.SqlCore.Core.Ast.WindowFrameUnitKind.Range -> WindowFrameUnit.Range
                    | value -> raise (SqlCompilationException("Unsupported window frame unit '" + string value + "'."))
                let start = frameBound frame.Start
                let extent =
                    match Option.ofObj frame.End with
                    | None -> WindowFrameExtent.SingleBound start
                    | Some finish -> WindowFrameExtent.BetweenBounds(start, frameBound finish)
                { WindowFrame.Unit = unit; Extent = extent })
        { WindowSpec.PartitionBy = window.PartitionBy |> Seq.map exprOf |> Seq.toList
          OrderBy = window.OrderBy |> Seq.map orderByOf |> Seq.toList
          Frame = frame }

    and private tableSourceOf (source: HsSqlAgent.SqlCore.Core.Ast.TableSource) : TableSource =
        match source with
        | :? HsSqlAgent.SqlCore.Core.Ast.NamedTableSource as table ->
            NamedTable(identifierOf table.Name, Option.ofObj table.Alias |> Option.map partOf)
        | :? HsSqlAgent.SqlCore.Core.Ast.DerivedTableSource as derived ->
            if derived.IsLateral then
                LateralDerivedTable(queryOfStatement derived.Query, partOf derived.Alias)
            else
                DerivedTable(queryOfStatement derived.Query, partOf derived.Alias)
        | _ -> failClosed "table source" source

    and private joinOf (join: HsSqlAgent.SqlCore.Core.Ast.JoinSource) =
        let source = tableSourceOf join.Source
        let usingColumns =
            if join.UsingColumns.IsDefaultOrEmpty then []
            else join.UsingColumns |> Seq.map (singlePart "JOIN USING column") |> Seq.toList
        let parsedKind = joinKind join.Kind
        if join.IsNatural then
            match parsedKind, Option.ofObj join.Predicate, usingColumns with
            | Choice1Of2 kind, None, [] -> NaturalJoin(kind, source)
            | Choice2Of2 (), _, _ ->
                raise (SqlCompilationException("NATURAL CROSS JOIN is not represented by the Core join model."))
            | _, _, _ ->
                raise (SqlCompilationException("NATURAL JOIN cannot carry ON or USING predicates."))
        else
            match parsedKind, Option.ofObj join.Predicate, usingColumns with
            | Choice2Of2 (), None, [] -> CrossJoin source
            | Choice2Of2 (), _, _ ->
                raise (SqlCompilationException("CROSS JOIN cannot carry ON or USING predicates."))
            | Choice1Of2 kind, Some predicate, [] -> OnJoin(kind, source, exprOf predicate)
            | Choice1Of2 kind, None, head :: tail ->
                UsingJoin(kind, source, NonEmpty.create head tail)
            | Choice1Of2 _, Some _, _ :: _ ->
                raise (SqlCompilationException(join.Kind + " JOIN cannot carry both ON and USING predicates."))
            | Choice1Of2 _, None, [] ->
                raise (SqlCompilationException(join.Kind + " JOIN requires an ON or USING predicate."))

    and private selectItemOf (item: HsSqlAgent.SqlCore.Core.Ast.SelectItem) =
        { SelectItem.Expression = exprOf item.Expression
          Alias = Option.ofObj item.Alias |> Option.map partOf }

    and private cteOf (cte: HsSqlAgent.SqlCore.Core.Ast.CteDefinition) =
        { Cte.Name = singlePart "CTE name" cte.Name
          ColumnAliases = cte.ColumnAliases |> Seq.map (singlePart "CTE column alias") |> Seq.toList
          Query = queryOfStatement cte.Query
          RecursiveScope = cte.RecursiveScope }

    and private selectOf (select: HsSqlAgent.SqlCore.Core.Ast.SelectStatement) =
        let projection =
            select.Select
            |> Seq.map selectItemOf
            |> Seq.toList
        let projectionItems =
            match projection with
            | [] ->
                NonEmpty.create
                    { SelectItem.Expression = Wildcard None
                      Alias = None }
                    []
            | head :: tail -> NonEmpty.create head tail
        let distinctMode =
            if not select.DistinctOn.IsDefaultOrEmpty then
                select.DistinctOn
                |> Seq.map exprOf
                |> Seq.toList
                |> NonEmpty.ofList "DISTINCT ON expressions"
                |> SelectDistinct.DistinctOn
            elif select.Distinct then
                SelectDistinct.DistinctRows
            else
                SelectDistinct.AllRows
        { Select.Ctes = select.Ctes |> Seq.map cteOf |> Seq.toList
          DistinctMode = distinctMode
          ProjectionItems = projectionItems
          From = Option.ofObj select.From |> Option.map tableSourceOf
          Joins = select.Joins |> Seq.map joinOf |> Seq.toList
          Where = Option.ofObj select.Where |> Option.map exprOf
          GroupBy = select.GroupBy |> Seq.map exprOf |> Seq.toList
          Having = Option.ofObj select.Having |> Option.map exprOf }

    and private setOperator = function
        | HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.Union -> SetOperator.Union
        | HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.UnionAll -> SetOperator.UnionAll
        | HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.Intersect -> SetOperator.Intersect
        | HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.IntersectAll -> SetOperator.IntersectAll
        | HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.Except -> SetOperator.Except
        | HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.ExceptAll -> SetOperator.ExceptAll
        | value -> raise (SqlCompilationException("Unsupported set operator '" + string value + "'."))

    and private rowCount argumentName (value: int Nullable) =
        if value.HasValue then Some(NonNegativeRowCount.create value.Value)
        else None

    and private percentage argumentName (value: decimal Nullable) =
        if value.HasValue then Some(NonNegativePercentage.create value.Value)
        else None

    and private queryOfStatement (statement: HsSqlAgent.SqlCore.Core.Ast.SqlStatement) : Query =
        match statement with
        | :? HsSqlAgent.SqlCore.Core.Ast.SelectStatement as select ->
            { Query.Head = selectOf select
              SetOperations = []
              OrderBy = select.OrderBy |> Seq.map orderByOf |> Seq.toList
              Limit = rowCount "limit" select.Limit
              Offset = rowCount "offset" select.Offset
              FetchPercent = percentage "fetchPercent" select.FetchPercent
              FetchWithTies = select.FetchWithTies }
        | :? HsSqlAgent.SqlCore.Core.Ast.QueryStatement as query ->
            { Query.Head = selectOf query.Head
              SetOperations =
                query.SetOperations
                |> Seq.map (fun operation ->
                    { SetBranch.Operator = setOperator operation.Kind
                      Query = queryOfStatement operation.Query })
                |> Seq.toList
              OrderBy = query.OrderBy |> Seq.map orderByOf |> Seq.toList
              Limit = rowCount "limit" query.Limit
              Offset = rowCount "offset" query.Offset
              FetchPercent = percentage "fetchPercent" query.FetchPercent
              FetchWithTies = query.FetchWithTies }
        | _ -> failClosed "query statement" statement

    and private returningItemOf (item: HsSqlAgent.SqlCore.Core.Ast.DmlReturningItem) =
        match item with
        | :? HsSqlAgent.SqlCore.Core.Ast.DmlReturningColumnItem as column ->
            let identifier = identifierOf column.Identifier
            if Identifier.parts identifier |> List.length = 1 then
                ReturningColumn(identifier, None)
            else
                ReturningExpression(Column identifier, None)
        | :? HsSqlAgent.SqlCore.Core.Ast.DmlReturningWildcardItem ->
            ReturningWildcard None
        | :? HsSqlAgent.SqlCore.Core.Ast.DmlReturningExpressionItem as expression ->
            ReturningExpression(
                exprOf expression.Expression,
                Option.ofObj expression.Alias |> Option.map partOf)
        | _ -> failClosed "DML RETURNING item" item

    and private insertConflictOf (conflict: HsSqlAgent.SqlCore.Core.Ast.InsertConflictClause) =
        let targetColumns =
            match conflict.TargetColumns |> Seq.map identifierOf |> Seq.toList with
            | [] -> None
            | values -> Some(NonEmpty.ofList "conflictTargetColumns" values)
        let action =
            match conflict.Action with
            | HsSqlAgent.SqlCore.Core.Ast.InsertConflictActionKind.DoNothing ->
                InsertConflictAction.DoNothing
            | HsSqlAgent.SqlCore.Core.Ast.InsertConflictActionKind.UpdateProposedValues ->
                let assignments =
                    conflict.Assignments
                    |> Seq.map (fun assignment ->
                        { ConflictAssignment.Target = identifierOf assignment.Column
                          Proposed = identifierOf assignment.ProposedColumn })
                    |> Seq.toList
                    |> NonEmpty.ofList "conflictAssignments"
                InsertConflictAction.UpdateProposedValues assignments
            | value ->
                raise (SqlCompilationException("Unsupported INSERT conflict action '" + string value + "'."))
        { InsertConflict.TargetColumns = targetColumns
          Action = action }

    and private insertInputOf (source: HsSqlAgent.SqlCore.Core.Ast.InsertSource) =
        match source with
        | :? HsSqlAgent.SqlCore.Core.Ast.InsertValuesSource as values ->
            values.Rows
            |> Seq.map (fun row ->
                row
                |> Seq.map exprOf
                |> Seq.toList
                |> NonEmpty.ofList "insertRow")
            |> Seq.toList
            |> NonEmpty.ofList "insertRows"
            |> InsertInput.Values
        | :? HsSqlAgent.SqlCore.Core.Ast.InsertQuerySource as query ->
            InsertInput.QuerySource(queryOfStatement query.Query)
        | _ -> failClosed "INSERT source" source

    and private statementOf (statement: HsSqlAgent.SqlCore.Core.Ast.SqlStatement) =
        match statement with
        | :? HsSqlAgent.SqlCore.Core.Ast.SelectStatement
        | :? HsSqlAgent.SqlCore.Core.Ast.QueryStatement ->
            Statement.QueryStatement(queryOfStatement statement)

        | :? HsSqlAgent.SqlCore.Core.Ast.InsertStatement as insert ->
            Statement.InsertStatement
                { Insert.Target = identifierOf insert.Target.Name
                  Columns = insert.Columns |> Seq.map (singlePart "INSERT column") |> Seq.toList
                  Input = insertInputOf insert.Source
                  Conflict = Option.ofObj insert.Conflict |> Option.map insertConflictOf
                  Returning = insert.Returning |> Seq.map returningItemOf |> Seq.toList }

        | :? HsSqlAgent.SqlCore.Core.Ast.UpdateStatement as update ->
            Statement.UpdateStatement
                { Update.Target = identifierOf update.Target.Name
                  TargetAlias = Option.ofObj update.Target.Alias |> Option.map partOf
                  AssignmentItems =
                    update.Assignments
                    |> Seq.map (fun assignment ->
                        { Assignment.Target = identifierOf assignment.Column
                          Value = exprOf assignment.Value })
                    |> Seq.toList
                    |> NonEmpty.ofList "assignments"
                  From =
                    (if update.FromSources.IsDefaultOrEmpty then
                        update.From |> Seq.map (fun source -> source :> HsSqlAgent.SqlCore.Core.Ast.TableSource)
                     else
                        update.FromSources :> seq<HsSqlAgent.SqlCore.Core.Ast.TableSource>)
                    |> Seq.map tableSourceOf
                    |> Seq.toList
                  Where = Option.ofObj update.Predicate |> Option.map exprOf
                  Returning = update.Returning |> Seq.map returningItemOf |> Seq.toList }

        | :? HsSqlAgent.SqlCore.Core.Ast.DeleteStatement as delete ->
            Statement.DeleteStatement
                { Delete.Target = identifierOf delete.Target.Name
                  TargetAlias = Option.ofObj delete.Target.Alias |> Option.map partOf
                  Using =
                    (if delete.UsingSources.IsDefaultOrEmpty then
                        delete.Using |> Seq.map (fun source -> source :> HsSqlAgent.SqlCore.Core.Ast.TableSource)
                     else
                        delete.UsingSources :> seq<HsSqlAgent.SqlCore.Core.Ast.TableSource>)
                    |> Seq.map tableSourceOf
                    |> Seq.toList
                  Where = Option.ofObj delete.Predicate |> Option.map exprOf
                  Returning = delete.Returning |> Seq.map returningItemOf |> Seq.toList }

        | _ -> failClosed "statement node" statement

    let toParsed (statement: HsSqlAgent.SqlCore.Core.Ast.SqlStatement) =
        ArgumentNullException.ThrowIfNull(statement)
        try
            Parsed.create
                { Document.Statement = statementOf statement
                  Span = spanOf statement.Span }
        with
        | :? SqlCompilationException -> reraise()
        | :? ArgumentException as ex ->
            raise (SqlCompilationException(ex.Message, ex))
