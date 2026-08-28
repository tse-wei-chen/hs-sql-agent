namespace HsSqlAgent.SqlCore.Internal

open System
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Core.Binding
open HsSqlAgent.SqlCore.Core.Compilation

/// Closed functional representation used while the parser/binder are migrated.
/// The adapter from the legacy C# hierarchy is intentionally the only open-world
/// type-test boundary. Everything after it is exhaustive pattern matching.
module internal FunctionalAst =

    [<RequireQualifiedAccess>]
    type UnaryOperator =
        | Not

    [<RequireQualifiedAccess>]
    type BinaryOperator =
        | Add
        | Subtract
        | Multiply
        | Divide
        | Modulo
        | Concat
        | Equal
        | NotEqual
        | GreaterThan
        | LessThan
        | GreaterThanOrEqual
        | LessThanOrEqual
        | And
        | Or
        | Like of escapeCharacter: string option
        | ILike of escapeCharacter: string option
        | InSubquery
        | NotInSubquery

    [<RequireQualifiedAccess>]
    type JoinKind =
        | Inner
        | Left
        | Right
        | Full
        | Cross

    [<RequireQualifiedAccess>]
    type SetOperation =
        | Union
        | UnionAll
        | Intersect
        | Except

    [<RequireQualifiedAccess>]
    type ConflictAction =
        | DoNothing
        | UpdateProposedValues of assignmentCount: int

    type Expr =
        | Literal
        | Column
        | BoundColumn
        | Interval
        | Unary of UnaryOperator * Expr
        | Binary of Expr * BinaryOperator * Expr
        | FunctionCall of arguments: Expr list * aggregateOrderBy: Expr list
        | Filter of expression: Expr * predicate: Expr
        | Windowed of expression: Expr * partitionBy: Expr list * orderBy: Expr list
        | Cast of Expr
        | SimpleCase of branches: (Expr * Expr) list * elseExpression: Expr option
        | SearchedCase of branches: (Expr * Expr) list * elseExpression: Expr option
        | InList of value: Expr * items: Expr list * isNegated: bool
        | Between of value: Expr * lower: Expr * upper: Expr * isNegated: bool
        | IsNull of value: Expr * isNegated: bool
        | Subquery of Statement
        | Exists of query: Statement * isNegated: bool

    and TableSource =
        | NamedTable
        | DerivedTable of Statement

    and ReturningItem =
        | ReturningColumn
        | ReturningWildcard
        | ReturningExpression of Expr

    and InsertSource =
        | Values of Expr list list
        | QuerySource of Statement

    and Statement =
        | Select of
            ctes: Statement list *
            fromSource: TableSource option *
            joins: (JoinKind * TableSource * Expr option) list *
            projections: Expr list *
            whereExpression: Expr option *
            groupBy: Expr list *
            havingExpression: Expr option *
            orderBy: Expr list
        | SetQuery of
            head: Statement *
            operations: (SetOperation * Statement) list *
            orderBy: Expr list
        | Insert of
            source: InsertSource *
            conflict: ConflictAction option *
            returningItems: ReturningItem list
        | Update of
            assignments: Expr list *
            fromSources: TableSource list *
            predicate: Expr option *
            returningItems: ReturningItem list
        | Delete of
            usingSources: TableSource list *
            predicate: Expr option *
            returningItems: ReturningItem list

    [<Struct>]
    type AuditSummary =
        {
            StatementCount: int
            ExpressionCount: int
            JoinCount: int
        }

    let private failClosed context (node: obj) =
        let nodeName = node.GetType().Name

        raise (SqlCompilationException(
            $"Unsupported {context} at the F# functional AST boundary: {nodeName}"))

    let private binaryOperatorOf (binary: BinaryExpr) =
        match binary.Operator.ToUpperInvariant() with
        | "+" -> BinaryOperator.Add
        | "-" -> BinaryOperator.Subtract
        | "*" -> BinaryOperator.Multiply
        | "/" -> BinaryOperator.Divide
        | "%" -> BinaryOperator.Modulo
        | "||" -> BinaryOperator.Concat
        | "=" -> BinaryOperator.Equal
        | "<>"
        | "!=" -> BinaryOperator.NotEqual
        | ">" -> BinaryOperator.GreaterThan
        | "<" -> BinaryOperator.LessThan
        | ">=" -> BinaryOperator.GreaterThanOrEqual
        | "<=" -> BinaryOperator.LessThanOrEqual
        | "AND" -> BinaryOperator.And
        | "OR" -> BinaryOperator.Or
        | "LIKE" -> BinaryOperator.Like(Option.ofObj binary.LikeEscape)
        | "ILIKE" -> BinaryOperator.ILike(Option.ofObj binary.LikeEscape)
        | "IN" -> BinaryOperator.InSubquery
        | "NOT IN" -> BinaryOperator.NotInSubquery
        | _ -> failClosed $"binary operator '{binary.Operator}'" binary

    let private unaryOperatorOf (unary: UnaryExpr) =
        match unary.Operator.ToUpperInvariant() with
        | "NOT" -> UnaryOperator.Not
        | _ -> failClosed $"unary operator '{unary.Operator}'" unary

    let private joinKindOf (join: JoinSource) =
        match join.Kind.ToUpperInvariant() with
        | "INNER" -> JoinKind.Inner
        | "LEFT" -> JoinKind.Left
        | "RIGHT" -> JoinKind.Right
        | "FULL" -> JoinKind.Full
        | "CROSS" -> JoinKind.Cross
        | _ -> failClosed $"JOIN kind '{join.Kind}'" join

    let private setOperationOf (operation: HsSqlAgent.SqlCore.Core.Ast.SetOperation) =
        match operation.Kind with
        | SetOperationKind.Union -> SetOperation.Union
        | SetOperationKind.UnionAll -> SetOperation.UnionAll
        | SetOperationKind.Intersect -> SetOperation.Intersect
        | SetOperationKind.Except -> SetOperation.Except
        | _ -> failClosed $"set operation '{operation.Kind}'" operation

    let private conflictOf (conflict: InsertConflictClause) =
        match conflict.Action with
        | InsertConflictActionKind.DoNothing ->
            ConflictAction.DoNothing
        | InsertConflictActionKind.UpdateProposedValues ->
            ConflictAction.UpdateProposedValues conflict.Assignments.Length
        | _ ->
            failClosed $"INSERT conflict action '{conflict.Action}'" conflict

    let rec private exprOf (expression: SqlExpr) : Expr =
        match expression with
        | :? BoundColumnExpr ->
            BoundColumn

        | :? LiteralExpr ->
            Literal

        | :? ColumnExpr ->
            Column

        | :? IntervalExpr ->
            Interval

        | :? UnaryExpr as unary ->
            Unary(unaryOperatorOf unary, exprOf unary.Operand)

        | :? BinaryExpr as binary ->
            Binary(exprOf binary.Left, binaryOperatorOf binary, exprOf binary.Right)

        | :? FunctionCallExpr as functionCall ->
            FunctionCall(
                functionCall.Arguments |> Seq.map exprOf |> Seq.toList,
                functionCall.AggregateOrderBy
                |> Seq.map (fun item -> exprOf item.Expression)
                |> Seq.toList)

        | :? FilterExpr as filter ->
            Filter(exprOf filter.Expression, exprOf filter.Predicate)

        | :? WindowedExpr as windowed ->
            Windowed(
                exprOf windowed.Expression,
                windowed.Window.PartitionBy |> Seq.map exprOf |> Seq.toList,
                windowed.Window.OrderBy
                |> Seq.map (fun item -> exprOf item.Expression)
                |> Seq.toList)

        | :? CastExpr as cast ->
            Cast(exprOf cast.Expression)

        // SimpleCaseExpr derives from CaseExpr, so it must be matched first.
        | :? SimpleCaseExpr as simpleCase ->
            SimpleCase(
                simpleCase.Branches
                |> Seq.map (fun branch -> exprOf branch.Condition, exprOf branch.Value)
                |> Seq.toList,
                Option.ofObj simpleCase.ElseExpression |> Option.map exprOf)

        | :? CaseExpr as searchedCase ->
            SearchedCase(
                searchedCase.Branches
                |> Seq.map (fun branch -> exprOf branch.Condition, exprOf branch.Value)
                |> Seq.toList,
                Option.ofObj searchedCase.ElseExpression |> Option.map exprOf)

        | :? InExpr as inExpression ->
            InList(
                exprOf inExpression.Value,
                inExpression.Items |> Seq.map exprOf |> Seq.toList,
                inExpression.IsNegated)

        | :? BetweenExpr as between ->
            Between(
                exprOf between.Value,
                exprOf between.Lower,
                exprOf between.Upper,
                between.IsNegated)

        | :? IsNullExpr as isNull ->
            IsNull(exprOf isNull.Value, isNull.IsNegated)

        | :? SubqueryExpr as subquery ->
            Subquery(statementOf subquery.Query)

        | :? ExistsExpr as exists ->
            Exists(statementOf exists.Query, exists.IsNegated)

        | _ ->
            failClosed "expression node" expression

    and private tableSourceOf (source: HsSqlAgent.SqlCore.Core.Ast.TableSource) : TableSource =
        match source with
        | :? NamedTableSource ->
            NamedTable

        | :? DerivedTableSource as derived ->
            DerivedTable(statementOf derived.Query)

        | _ ->
            failClosed "table source" source

    and private returningItemOf (item: DmlReturningItem) : ReturningItem =
        match item with
        | :? DmlReturningColumnItem ->
            ReturningColumn

        | :? DmlReturningWildcardItem ->
            ReturningWildcard

        | :? DmlReturningExpressionItem as expression ->
            ReturningExpression(exprOf expression.Expression)

        | _ ->
            failClosed "DML returning item" item

    and private insertSourceOf (source: HsSqlAgent.SqlCore.Core.Ast.InsertSource) : InsertSource =
        match source with
        | :? InsertValuesSource as values ->
            Values(
                values.Rows
                |> Seq.map (fun row -> row |> Seq.map exprOf |> Seq.toList)
                |> Seq.toList)

        | :? InsertQuerySource as query ->
            QuerySource(statementOf query.Query)

        | _ ->
            failClosed "INSERT source" source

    and private statementOf (statement: SqlStatement) : Statement =
        match statement with
        | :? SelectStatement as select ->
            let joins =
                select.Joins
                |> Seq.map (fun join ->
                    let kind = joinKindOf join
                    let predicate = Option.ofObj join.Predicate |> Option.map exprOf

                    match kind, predicate with
                    | JoinKind.Cross, Some _ ->
                        raise (SqlCompilationException(
                            "CROSS JOIN cannot carry a predicate in the functional AST."))
                    | JoinKind.Inner, None
                    | JoinKind.Left, None
                    | JoinKind.Right, None
                    | JoinKind.Full, None ->
                        raise (SqlCompilationException(
                            $"{join.Kind} JOIN requires a predicate in the functional AST."))
                    | JoinKind.Cross, None
                    | JoinKind.Inner, Some _
                    | JoinKind.Left, Some _
                    | JoinKind.Right, Some _
                    | JoinKind.Full, Some _ ->
                        kind, tableSourceOf join.Source, predicate)
                |> Seq.toList

            Select(
                select.Ctes
                |> Seq.map (fun cte -> statementOf cte.Query)
                |> Seq.toList,
                Option.ofObj select.From |> Option.map tableSourceOf,
                joins,
                select.Select
                |> Seq.map (fun item -> exprOf item.Expression)
                |> Seq.toList,
                Option.ofObj select.Where |> Option.map exprOf,
                select.GroupBy |> Seq.map exprOf |> Seq.toList,
                Option.ofObj select.Having |> Option.map exprOf,
                select.OrderBy
                |> Seq.map (fun item -> exprOf item.Expression)
                |> Seq.toList)

        | :? QueryStatement as query ->
            SetQuery(
                statementOf query.Head,
                query.SetOperations
                |> Seq.map (fun operation ->
                    setOperationOf operation, statementOf operation.Query)
                |> Seq.toList,
                query.OrderBy
                |> Seq.map (fun item -> exprOf item.Expression)
                |> Seq.toList)

        | :? InsertStatement as insert ->
            Insert(
                insertSourceOf insert.Source,
                Option.ofObj insert.Conflict |> Option.map conflictOf,
                insert.Returning |> Seq.map returningItemOf |> Seq.toList)

        | :? UpdateStatement as update ->
            Update(
                update.Assignments
                |> Seq.map (fun assignment -> exprOf assignment.Value)
                |> Seq.toList,
                update.From
                |> Seq.map (fun source -> tableSourceOf source)
                |> Seq.toList,
                Option.ofObj update.Predicate |> Option.map exprOf,
                update.Returning |> Seq.map returningItemOf |> Seq.toList)

        | :? DeleteStatement as delete ->
            Delete(
                delete.Using
                |> Seq.map (fun source -> tableSourceOf source)
                |> Seq.toList,
                Option.ofObj delete.Predicate |> Option.map exprOf,
                delete.Returning |> Seq.map returningItemOf |> Seq.toList)

        | _ ->
            failClosed "statement node" statement

    let private plus left right =
        {
            StatementCount = left.StatementCount + right.StatementCount
            ExpressionCount = left.ExpressionCount + right.ExpressionCount
            JoinCount = left.JoinCount + right.JoinCount
        }

    let private zero =
        {
            StatementCount = 0
            ExpressionCount = 0
            JoinCount = 0
        }

    let private sum summaries =
        summaries |> Seq.fold plus zero

    let rec private auditExpr expression =
        let childSummary =
            match expression with
            | Literal
            | Column
            | BoundColumn
            | Interval ->
                zero

            | Unary(_, operand)
            | Cast operand ->
                auditExpr operand

            | Binary(left, _, right) ->
                plus (auditExpr left) (auditExpr right)

            | FunctionCall(arguments, aggregateOrderBy) ->
                Seq.append arguments aggregateOrderBy
                |> Seq.map auditExpr
                |> sum

            | Filter(expression, predicate) ->
                plus (auditExpr expression) (auditExpr predicate)

            | Windowed(expression, partitionBy, orderBy) ->
                Seq.concat [ [ expression ]; partitionBy; orderBy ]
                |> Seq.map auditExpr
                |> sum

            | SimpleCase(branches, elseExpression)
            | SearchedCase(branches, elseExpression) ->
                let branchSummary =
                    branches
                    |> Seq.collect (fun (condition, value) -> [ condition; value ])
                    |> Seq.map auditExpr
                    |> sum

                match elseExpression with
                | Some value -> plus branchSummary (auditExpr value)
                | None -> branchSummary

            | InList(value, items, _) ->
                Seq.append [ value ] items
                |> Seq.map auditExpr
                |> sum

            | Between(value, lower, upper, _) ->
                [ value; lower; upper ]
                |> Seq.map auditExpr
                |> sum

            | IsNull(value, _) ->
                auditExpr value

            | Subquery query ->
                auditStatement query

            | Exists(query, _) ->
                auditStatement query

        {
            childSummary with
                ExpressionCount = childSummary.ExpressionCount + 1
        }

    and private auditSource source =
        match source with
        | NamedTable ->
            zero
        | DerivedTable query ->
            auditStatement query

    and private auditReturning item =
        match item with
        | ReturningColumn
        | ReturningWildcard ->
            zero
        | ReturningExpression expression ->
            auditExpr expression

    and private auditInsertSource source =
        match source with
        | Values rows ->
            rows
            |> Seq.collect id
            |> Seq.map auditExpr
            |> sum
        | QuerySource query ->
            auditStatement query

    and private auditStatement statement =
        let childSummary =
            match statement with
            | Select(ctes, fromSource, joins, projections, whereExpression, groupBy, havingExpression, orderBy) ->
                let cteSummary = ctes |> Seq.map auditStatement |> sum

                let fromSummary =
                    match fromSource with
                    | Some source -> auditSource source
                    | None -> zero

                let joinSummary =
                    joins
                    |> Seq.map (fun (_, source, predicate) ->
                        let sourceSummary = auditSource source
                        let predicateSummary =
                            match predicate with
                            | Some expression -> auditExpr expression
                            | None -> zero

                        let combined = plus sourceSummary predicateSummary
                        { combined with JoinCount = combined.JoinCount + 1 })
                    |> sum

                let expressionSummary =
                    seq {
                        yield! projections
                        yield! groupBy
                        yield! orderBy
                        match whereExpression with
                        | Some value -> yield value
                        | None -> ()
                        match havingExpression with
                        | Some value -> yield value
                        | None -> ()
                    }
                    |> Seq.map auditExpr
                    |> sum

                sum [ cteSummary; fromSummary; joinSummary; expressionSummary ]

            | SetQuery(head, operations, orderBy) ->
                sum [
                    auditStatement head
                    operations
                    |> Seq.map (fun (_, query) -> auditStatement query)
                    |> sum
                    orderBy |> Seq.map auditExpr |> sum
                ]

            | Insert(source, conflict, returningItems) ->
                let conflictSummary =
                    match conflict with
                    | Some ConflictAction.DoNothing
                    | Some (ConflictAction.UpdateProposedValues _)
                    | None ->
                        zero

                sum [
                    auditInsertSource source
                    conflictSummary
                    returningItems |> Seq.map auditReturning |> sum
                ]

            | Update(assignments, fromSources, predicate, returningItems) ->
                sum [
                    assignments |> Seq.map auditExpr |> sum
                    fromSources |> Seq.map auditSource |> sum
                    match predicate with
                    | Some value -> auditExpr value
                    | None -> zero
                    returningItems |> Seq.map auditReturning |> sum
                ]

            | Delete(usingSources, predicate, returningItems) ->
                sum [
                    usingSources |> Seq.map auditSource |> sum
                    match predicate with
                    | Some value -> auditExpr value
                    | None -> zero
                    returningItems |> Seq.map auditReturning |> sum
                ]

        {
            childSummary with
                StatementCount = childSummary.StatementCount + 1
        }

    /// Convert the legacy open C# hierarchy into the closed F# compiler shape and
    /// exhaustively walk it. Unknown legacy shapes fail closed at the adapter.
    let verify (statement: SqlStatement) : AuditSummary =
        statement
        |> statementOf
        |> auditStatement
