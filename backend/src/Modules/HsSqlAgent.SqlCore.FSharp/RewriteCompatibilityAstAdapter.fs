#nowarn "3261" "3262"

namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.Typestate

/// Projection from the closed F# parser DU to the temporary CLR compatibility AST.
/// Compiler stages never consume this projection for parser-native SQL; it exists only for
/// approval/audit callers that still inspect statement shape.
module internal RewriteCompatibilityAstAdapter =

    let private unknown = HsSqlAgent.SqlCore.Core.Ast.SourceSpan.Unknown

    let private spanOf (span: Span) : HsSqlAgent.SqlCore.Core.Ast.SourceSpan =
        { Start = span.Start
          End = if span.Start < 0 then -1 else span.Start + max 0 span.Length }

    let private partOf (part: IdentifierPart) =
        HsSqlAgent.SqlCore.Core.Ast.IdentifierPart(
            part.Value,
            part.WasQuoted,
            spanOf part.Span,
            part.PreserveSpelling)

    let private identifierOf identifier =
        HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier(
            identifier
            |> Identifier.parts
            |> List.map partOf
            |> ImmutableArray.CreateRange,
            unknown)

    let private identifierFromText (value: string) =
        value.Split('.', StringSplitOptions.RemoveEmptyEntries)
        |> Seq.map (fun part -> HsSqlAgent.SqlCore.Core.Ast.IdentifierPart(part, false, unknown))
        |> ImmutableArray.CreateRange
        |> fun parts -> HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier(parts, unknown)

    let private scalarValue = function
        | ScalarValue.Null -> null
        | ScalarValue.Boolean value -> box value
        | ScalarValue.Integer value -> box value
        | ScalarValue.Decimal value -> box value
        | ScalarValue.Floating value -> box value
        | ScalarValue.Text value -> box value
        | ScalarValue.Date value -> box (SqlDateValue(value))
        | ScalarValue.Time value -> box (SqlTimeValue(value))
        | ScalarValue.LocalDateTime value -> box (SqlLocalDateTimeValue(value))
        | ScalarValue.OffsetDateTime value -> box (SqlOffsetDateTimeValue(value))
        | ScalarValue.Duration value -> box value
        | ScalarValue.Bytes value -> box value

    let private binaryOperator = function
        | BinaryOperator.Add -> "+"
        | BinaryOperator.Subtract -> "-"
        | BinaryOperator.Multiply -> "*"
        | BinaryOperator.Divide -> "/"
        | BinaryOperator.Modulo -> "%"
        | BinaryOperator.Concat -> "||"
        | BinaryOperator.Equal -> "="
        | BinaryOperator.NotEqual -> "<>"
        | BinaryOperator.GreaterThan -> ">"
        | BinaryOperator.LessThan -> "<"
        | BinaryOperator.GreaterThanOrEqual -> ">="
        | BinaryOperator.LessThanOrEqual -> "<="
        | BinaryOperator.And -> "AND"
        | BinaryOperator.Or -> "OR"

    let private nullOrdering = function
        | NullOrdering.Default -> HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.Default
        | NullOrdering.NullsFirst -> HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.First
        | NullOrdering.NullsLast -> HsSqlAgent.SqlCore.Core.Ast.NullOrderingKind.Last

    let private aggregateOrderSyntax = function
        | AggregateOrderSyntax.NoAggregateOrder -> HsSqlAgent.SqlCore.Core.Ast.AggregateOrderSyntaxKind.None
        | AggregateOrderSyntax.InlineAggregateOrder -> HsSqlAgent.SqlCore.Core.Ast.AggregateOrderSyntaxKind.Inline
        | AggregateOrderSyntax.WithinGroupAggregateOrder -> HsSqlAgent.SqlCore.Core.Ast.AggregateOrderSyntaxKind.WithinGroup

    let private frameBound = function
        | WindowFrameBound.UnboundedPreceding ->
            HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundCore(
                HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.UnboundedPreceding,
                Nullable(),
                unknown)
        | WindowFrameBound.Preceding offset ->
            HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundCore(
                HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.Preceding,
                Nullable(FrameOffset.value offset),
                unknown)
        | WindowFrameBound.CurrentRow ->
            HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundCore(
                HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.CurrentRow,
                Nullable(),
                unknown)
        | WindowFrameBound.Following offset ->
            HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundCore(
                HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.Following,
                Nullable(FrameOffset.value offset),
                unknown)
        | WindowFrameBound.UnboundedFollowing ->
            HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundCore(
                HsSqlAgent.SqlCore.Core.Ast.WindowFrameBoundKindCore.UnboundedFollowing,
                Nullable(),
                unknown)

    let rec private exprOf expression : HsSqlAgent.SqlCore.Core.Ast.SqlExpr =
        match expression with
        | Expr.Column identifier
        | Expr.BoundColumn(identifier, _) ->
            HsSqlAgent.SqlCore.Core.Ast.ColumnExpr(identifierOf identifier, unknown)

        | Expr.Wildcard qualifier ->
            let parts =
                match qualifier with
                | None -> []
                | Some value -> value |> Identifier.parts |> List.map partOf
            let parts =
                parts @ [ HsSqlAgent.SqlCore.Core.Ast.IdentifierPart("*", false, unknown) ]
                |> ImmutableArray.CreateRange
            HsSqlAgent.SqlCore.Core.Ast.ColumnExpr(
                HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier(parts, unknown),
                unknown)

        | Expr.OrderOrdinal ordinal ->
            HsSqlAgent.SqlCore.Core.Ast.LiteralExpr(
                HsSqlAgent.SqlCore.Core.Ast.OrderByOrdinalValue(PositiveRowCount.value ordinal),
                unknown)

        | Expr.Literal value ->
            HsSqlAgent.SqlCore.Core.Ast.LiteralExpr(scalarValue value, unknown)

        | Expr.Interval literal ->
            HsSqlAgent.SqlCore.Core.Ast.IntervalExpr(IntervalLiteral.value literal, unknown)

        | Expr.Unary(operator, operand) ->
            let text =
                match operator with
                | UnaryOperator.Not -> "NOT"
                | UnaryOperator.Negate -> "-"
                | UnaryOperator.Positive -> "+"
            HsSqlAgent.SqlCore.Core.Ast.UnaryExpr(text, exprOf operand, unknown)

        | Expr.Binary(operator, left, right) ->
            HsSqlAgent.SqlCore.Core.Ast.BinaryExpr(
                exprOf left,
                binaryOperator operator,
                exprOf right,
                unknown)

        | Expr.Like(value, pattern, escape, negated, insensitive) ->
            let op =
                match negated, insensitive with
                | false, false -> "LIKE"
                | true, false -> "NOT LIKE"
                | false, true -> "ILIKE"
                | true, true -> "NOT ILIKE"
            let escapeText =
                escape
                |> Option.map (LikeEscape.value >> string)
                |> Option.defaultValue null
            HsSqlAgent.SqlCore.Core.Ast.BinaryExpr(
                exprOf value,
                op,
                exprOf pattern,
                unknown,
                escapeText)

        | Expr.RawRegexCall(arguments, distinct) ->
            HsSqlAgent.SqlCore.Core.Ast.FunctionCallExpr(
                identifierFromText "REGEXP_LIKE",
                arguments |> List.map exprOf |> ImmutableArray.CreateRange,
                distinct,
                unknown)

        | Expr.RegexMatch(value, pattern) ->
            HsSqlAgent.SqlCore.Core.Ast.FunctionCallExpr(
                identifierFromText "CORE_REGEX_MATCH",
                [ exprOf value; exprOf pattern ] |> ImmutableArray.CreateRange,
                false,
                unknown)

        | Expr.FunctionCall call ->
            let result =
                HsSqlAgent.SqlCore.Core.Ast.FunctionCallExpr(
                    identifierFromText (FunctionName.value call.Name),
                    call.Arguments |> List.map exprOf |> ImmutableArray.CreateRange,
                    call.IsDistinct,
                    unknown)
            result.AggregateOrderBy <- call.AggregateOrderBy |> List.map orderByOf |> ImmutableArray.CreateRange
            result.AggregateOrderSyntax <- aggregateOrderSyntax call.AggregateOrderSyntax
            result.AggregateSeparatorClause <- call.AggregateSeparator |> Option.defaultValue null
            result

        | Expr.FilteredAggregate(value, predicate) ->
            HsSqlAgent.SqlCore.Core.Ast.FilterExpr(exprOf value, exprOf predicate, unknown)

        | Expr.Windowed(value, window) ->
            HsSqlAgent.SqlCore.Core.Ast.WindowedExpr(exprOf value, windowOf window, unknown)

        | Expr.Cast(value, typeName) ->
            HsSqlAgent.SqlCore.Core.Ast.CastExpr(exprOf value, CastType.value typeName, unknown)

        | Expr.Extract(field, value) ->
            HsSqlAgent.SqlCore.Core.Ast.ExtractExpr(ExtractField.value field, exprOf value, unknown)

        | Expr.SimpleCase(input, branches, fallback) ->
            let inputExpr = exprOf input
            let mapped =
                branches
                |> NonEmpty.toList
                |> List.map (fun branch ->
                    let condition =
                        HsSqlAgent.SqlCore.Core.Ast.BinaryExpr(
                            inputExpr,
                            "=",
                            exprOf branch.Match,
                            unknown)
                    HsSqlAgent.SqlCore.Core.Ast.CaseBranch(condition, exprOf branch.Result))
                |> ImmutableArray.CreateRange
            HsSqlAgent.SqlCore.Core.Ast.SimpleCaseExpr(
                mapped,
                fallback |> Option.map exprOf |> Option.defaultValue (Unchecked.defaultof<_>),
                unknown)

        | Expr.SearchedCase(branches, fallback) ->
            HsSqlAgent.SqlCore.Core.Ast.CaseExpr(
                branches
                |> NonEmpty.toList
                |> List.map (fun branch ->
                    HsSqlAgent.SqlCore.Core.Ast.CaseBranch(
                        exprOf branch.Condition,
                        exprOf branch.Result))
                |> ImmutableArray.CreateRange,
                fallback |> Option.map exprOf |> Option.defaultValue (Unchecked.defaultof<_>),
                unknown)

        | Expr.InList(value, items, negated) ->
            HsSqlAgent.SqlCore.Core.Ast.InExpr(
                exprOf value,
                items |> NonEmpty.toList |> List.map exprOf |> ImmutableArray.CreateRange,
                negated,
                unknown)

        | Expr.InSubquery(value, query, negated) ->
            HsSqlAgent.SqlCore.Core.Ast.BinaryExpr(
                exprOf value,
                (if negated then "NOT IN" else "IN"),
                HsSqlAgent.SqlCore.Core.Ast.SubqueryExpr(queryOf query, unknown),
                unknown)

        | Expr.Between(value, lower, upper, negated) ->
            HsSqlAgent.SqlCore.Core.Ast.BetweenExpr(
                exprOf value,
                exprOf lower,
                exprOf upper,
                negated,
                unknown)

        | Expr.IsNull(value, negated) ->
            HsSqlAgent.SqlCore.Core.Ast.IsNullExpr(exprOf value, negated, unknown)

        | Expr.ScalarSubquery query ->
            HsSqlAgent.SqlCore.Core.Ast.SubqueryExpr(queryOf query, unknown)

        | Expr.Exists(query, negated) ->
            HsSqlAgent.SqlCore.Core.Ast.ExistsExpr(queryOf query, negated, unknown)

    and private orderByOf (order: OrderBy) =
        HsSqlAgent.SqlCore.Core.Ast.OrderByItem(
            exprOf order.Expression,
            order.Descending,
            nullOrdering order.NullOrdering,
            unknown)

    and private windowOf (window: WindowSpec) =
        let frame =
            window.Frame
            |> Option.map (fun value ->
                let unit =
                    match value.Unit with
                    | WindowFrameUnit.Rows -> HsSqlAgent.SqlCore.Core.Ast.WindowFrameUnitKind.Rows
                    | WindowFrameUnit.Range -> HsSqlAgent.SqlCore.Core.Ast.WindowFrameUnitKind.Range
                let start = frameBound value.Start
                let finish = value.End |> Option.map frameBound |> Option.defaultValue (Unchecked.defaultof<_>)
                HsSqlAgent.SqlCore.Core.Ast.WindowFrame(unit, start, finish, unknown))
            |> Option.defaultValue (Unchecked.defaultof<_>)
        HsSqlAgent.SqlCore.Core.Ast.WindowSpec(
            window.PartitionBy |> List.map exprOf |> ImmutableArray.CreateRange,
            window.OrderBy |> List.map orderByOf |> ImmutableArray.CreateRange,
            frame,
            unknown)

    and private tableSourceOf source : HsSqlAgent.SqlCore.Core.Ast.TableSource =
        match source with
        | TableSource.NamedTable(identifier, alias)
        | TableSource.CteTable(identifier, alias) ->
            HsSqlAgent.SqlCore.Core.Ast.NamedTableSource(
                identifierOf identifier,
                alias |> Option.map partOf |> Option.defaultValue null,
                unknown)
        | TableSource.DerivedTable(query, alias) ->
            HsSqlAgent.SqlCore.Core.Ast.DerivedTableSource(
                queryOf query,
                partOf alias,
                unknown)

    and private joinOf join =
        match join with
        | Join.CrossJoin source ->
            HsSqlAgent.SqlCore.Core.Ast.JoinSource(
                "CROSS",
                tableSourceOf source,
                Unchecked.defaultof<_>,
                unknown)
        | Join.OnJoin(kind, source, predicate) ->
            let text =
                match kind with
                | OnJoinKind.Inner -> "INNER"
                | OnJoinKind.Left -> "LEFT"
                | OnJoinKind.Right -> "RIGHT"
                | OnJoinKind.Full -> "FULL"
            HsSqlAgent.SqlCore.Core.Ast.JoinSource(
                text,
                tableSourceOf source,
                exprOf predicate,
                unknown)

    and private selectItemOf (item: SelectItem) =
        HsSqlAgent.SqlCore.Core.Ast.SelectItem(
            exprOf item.Expression,
            item.Alias |> Option.map partOf |> Option.defaultValue null,
            unknown)

    and private cteOf (cte: Cte) =
        HsSqlAgent.SqlCore.Core.Ast.CteDefinition(
            HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier(
                ImmutableArray.Create(partOf cte.Name),
                unknown),
            cte.ColumnAliases
            |> List.map (fun alias ->
                HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier(
                    ImmutableArray.Create(partOf alias),
                    unknown))
            |> ImmutableArray.CreateRange,
            queryOf cte.Query,
            unknown)

    and private selectOf (select: Select) orderBy limit offset =
        HsSqlAgent.SqlCore.Core.Ast.SelectStatement(
            select.Ctes |> List.map cteOf |> ImmutableArray.CreateRange,
            select.Distinct,
            select.Projection |> List.map selectItemOf |> ImmutableArray.CreateRange,
            select.From |> Option.map tableSourceOf |> Option.defaultValue (Unchecked.defaultof<_>),
            select.Joins |> List.map joinOf |> ImmutableArray.CreateRange,
            select.Where |> Option.map exprOf |> Option.defaultValue (Unchecked.defaultof<_>),
            select.GroupBy |> List.map exprOf |> ImmutableArray.CreateRange,
            select.Having |> Option.map exprOf |> Option.defaultValue (Unchecked.defaultof<_>),
            orderBy |> List.map orderByOf |> ImmutableArray.CreateRange,
            limit |> Option.map (NonNegativeRowCount.value >> Nullable) |> Option.defaultValue (Nullable()),
            offset |> Option.map (NonNegativeRowCount.value >> Nullable) |> Option.defaultValue (Nullable()),
            unknown)

    and private setOperator = function
        | SetOperator.Union -> HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.Union
        | SetOperator.UnionAll -> HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.UnionAll
        | SetOperator.Intersect -> HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.Intersect
        | SetOperator.Except -> HsSqlAgent.SqlCore.Core.Ast.SetOperationKind.Except

    and private queryOf (query: Query) : HsSqlAgent.SqlCore.Core.Ast.SqlStatement =
        match query.SetOperations with
        | [] ->
            selectOf query.Head query.OrderBy query.Limit query.Offset
        | operations ->
            let head = selectOf query.Head [] None None
            HsSqlAgent.SqlCore.Core.Ast.QueryStatement(
                head,
                operations
                |> List.map (fun branch ->
                    HsSqlAgent.SqlCore.Core.Ast.SetOperation(
                        setOperator branch.Operator,
                        queryOf branch.Query,
                        unknown))
                |> ImmutableArray.CreateRange,
                query.OrderBy |> List.map orderByOf |> ImmutableArray.CreateRange,
                query.Limit |> Option.map (NonNegativeRowCount.value >> Nullable) |> Option.defaultValue (Nullable()),
                query.Offset |> Option.map (NonNegativeRowCount.value >> Nullable) |> Option.defaultValue (Nullable()),
                unknown)

    let private returningOf item : HsSqlAgent.SqlCore.Core.Ast.DmlReturningItem =
        match item with
        | ReturningItem.ReturningColumn(identifier, None) ->
            HsSqlAgent.SqlCore.Core.Ast.DmlReturningColumnItem(identifierOf identifier, unknown)
        | ReturningItem.ReturningWildcard None ->
            HsSqlAgent.SqlCore.Core.Ast.DmlReturningWildcardItem(unknown)
        | ReturningItem.ReturningColumn(identifier, alias) ->
            HsSqlAgent.SqlCore.Core.Ast.DmlReturningExpressionItem(
                HsSqlAgent.SqlCore.Core.Ast.ColumnExpr(identifierOf identifier, unknown),
                alias |> Option.map partOf |> Option.defaultValue null,
                unknown)
        | ReturningItem.ReturningWildcard alias ->
            HsSqlAgent.SqlCore.Core.Ast.DmlReturningExpressionItem(
                exprOf (Expr.Wildcard None),
                alias |> Option.map partOf |> Option.defaultValue null,
                unknown)
        | ReturningItem.ReturningExpression(expression, alias) ->
            HsSqlAgent.SqlCore.Core.Ast.DmlReturningExpressionItem(
                exprOf expression,
                alias |> Option.map partOf |> Option.defaultValue null,
                unknown)

    let private namedDmlSource context source =
        match tableSourceOf source with
        | :? HsSqlAgent.SqlCore.Core.Ast.NamedTableSource as named -> named
        | _ -> raise (SqlCompilationException(context + " supports named DML sources only."))

    let private conflictOf (conflict: InsertConflict) =
        let action, assignments =
            match conflict.Action with
            | InsertConflictAction.DoNothing ->
                HsSqlAgent.SqlCore.Core.Ast.InsertConflictActionKind.DoNothing,
                ImmutableArray<HsSqlAgent.SqlCore.Core.Ast.InsertConflictAssignment>.Empty
            | InsertConflictAction.UpdateProposedValues values ->
                HsSqlAgent.SqlCore.Core.Ast.InsertConflictActionKind.UpdateProposedValues,
                (values
                 |> NonEmpty.toList
                 |> List.map (fun assignment ->
                    HsSqlAgent.SqlCore.Core.Ast.InsertConflictAssignment(
                        identifierOf assignment.Target,
                        identifierOf assignment.Proposed,
                        unknown))
                 |> ImmutableArray.CreateRange)
        HsSqlAgent.SqlCore.Core.Ast.InsertConflictClause(
            conflict.TargetColumns |> NonEmpty.toList |> List.map identifierOf |> ImmutableArray.CreateRange,
            action,
            assignments,
            unknown)

    let private statementOf = function
        | Statement.QueryStatement query ->
            queryOf query

        | Statement.InsertStatement insert ->
            let source =
                match insert.Input with
                | InsertInput.Values rows ->
                    HsSqlAgent.SqlCore.Core.Ast.InsertValuesSource(
                        rows
                        |> NonEmpty.toList
                        |> List.map (fun row ->
                            row |> NonEmpty.toList |> List.map exprOf |> ImmutableArray.CreateRange)
                        |> ImmutableArray.CreateRange,
                        unknown)
                    :> HsSqlAgent.SqlCore.Core.Ast.InsertSource
                | InsertInput.QuerySource query ->
                    HsSqlAgent.SqlCore.Core.Ast.InsertQuerySource(queryOf query, unknown)
                    :> HsSqlAgent.SqlCore.Core.Ast.InsertSource
                | InsertInput.DefaultValues ->
                    raise (SqlParseException(
                        "INSERT DEFAULT VALUES is parsed by the F# core but is not exposed through the temporary legacy AST compatibility surface."))

            let result =
                HsSqlAgent.SqlCore.Core.Ast.InsertStatement(
                    HsSqlAgent.SqlCore.Core.Ast.NamedTableSource(
                        identifierOf insert.Target,
                        null,
                        unknown),
                    insert.Columns
                    |> List.map (fun column ->
                        HsSqlAgent.SqlCore.Core.Ast.SqlIdentifier(
                            ImmutableArray.Create(partOf column),
                            unknown))
                    |> ImmutableArray.CreateRange,
                    source,
                    unknown)
            result.Conflict <- insert.Conflict |> Option.map conflictOf |> Option.defaultValue (Unchecked.defaultof<_>)
            result.Returning <- insert.Returning |> List.map returningOf |> ImmutableArray.CreateRange
            result :> HsSqlAgent.SqlCore.Core.Ast.SqlStatement

        | Statement.UpdateStatement update ->
            let result =
                HsSqlAgent.SqlCore.Core.Ast.UpdateStatement(
                    HsSqlAgent.SqlCore.Core.Ast.NamedTableSource(identifierOf update.Target, null, unknown),
                    update.Assignments
                    |> List.map (fun assignment ->
                        HsSqlAgent.SqlCore.Core.Ast.Assignment(
                            identifierOf assignment.Target,
                            exprOf assignment.Value,
                            unknown))
                    |> ImmutableArray.CreateRange,
                    update.Where |> Option.map exprOf |> Option.defaultValue (Unchecked.defaultof<_>),
                    unknown)
            result.From <- update.From |> List.map (namedDmlSource "UPDATE FROM") |> ImmutableArray.CreateRange
            result.Returning <- update.Returning |> List.map returningOf |> ImmutableArray.CreateRange
            result :> HsSqlAgent.SqlCore.Core.Ast.SqlStatement

        | Statement.DeleteStatement delete ->
            let result =
                HsSqlAgent.SqlCore.Core.Ast.DeleteStatement(
                    HsSqlAgent.SqlCore.Core.Ast.NamedTableSource(identifierOf delete.Target, null, unknown),
                    delete.Where |> Option.map exprOf |> Option.defaultValue (Unchecked.defaultof<_>),
                    unknown)
            result.Using <- delete.Using |> List.map (namedDmlSource "DELETE USING") |> ImmutableArray.CreateRange
            result.Returning <- delete.Returning |> List.map returningOf |> ImmutableArray.CreateRange
            result :> HsSqlAgent.SqlCore.Core.Ast.SqlStatement

    let toStatement (parsed: ParsedSql) =
        let document = Parsed.value parsed
        let statement = statementOf document.Statement
        // Preserve the full statement source span at the public boundary.
        match statement with
        | :? HsSqlAgent.SqlCore.Core.Ast.SelectStatement as select ->
            select :> HsSqlAgent.SqlCore.Core.Ast.SqlStatement
        | value -> value

    let kind (parsed: ParsedSql) =
        match (Parsed.value parsed).Statement with
        | Statement.QueryStatement _ -> SqlStatementKind.Query
        | Statement.InsertStatement _ -> SqlStatementKind.Insert
        | Statement.UpdateStatement _ -> SqlStatementKind.Update
        | Statement.DeleteStatement _ -> SqlStatementKind.Delete
