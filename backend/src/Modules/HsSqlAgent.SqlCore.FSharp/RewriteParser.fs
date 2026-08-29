namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteParser =

    type private Cursor(tokens: Token list) =
        let data = List.toArray tokens
        let mutable index = 0
        member _.Current = data[index]
        member _.Peek(offset: int) = data[min (data.Length - 1) (index + offset)]
        member _.Advance() = if index < data.Length - 1 then index <- index + 1
        member _.Take() = let token = data[index] in if index < data.Length - 1 then index <- index + 1; token

    let private fail (token: Token) message =
        invalidArg "sql" (message + " at offset " + string token.Start + ".")

    let private isKeyword keyword token =
        match token.Kind with
        | Keyword value -> value = keyword
        | _ -> false

    let private isSymbol symbol token =
        match token.Kind with
        | Symbol value -> value = symbol
        | _ -> false

    let private isOperator operator token =
        match token.Kind with
        | Operator value -> value = operator
        | _ -> false

    let private acceptKeyword keyword (cursor: Cursor) =
        if isKeyword keyword cursor.Current then cursor.Advance(); true else false

    let private acceptSymbol symbol (cursor: Cursor) =
        if isSymbol symbol cursor.Current then cursor.Advance(); true else false

    let private acceptOperator operator (cursor: Cursor) =
        if isOperator operator cursor.Current then cursor.Advance(); true else false

    let private expectKeyword keyword cursor =
        if not (acceptKeyword keyword cursor) then fail cursor.Current ("Expected " + keyword)

    let private expectSymbol symbol cursor =
        if not (acceptSymbol symbol cursor) then fail cursor.Current ("Expected '" + string symbol + "'")

    let private expectOperator operator cursor =
        if not (acceptOperator operator cursor) then fail cursor.Current ("Expected operator '" + operator + "'")

    let private identifierPart (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | Identifier(value, quoted) ->
            { Value = value; WasQuoted = quoted; Span = { Start = token.Start; Length = token.Length } }
        | _ -> fail token "Expected identifier"

    let private identifier cursor =
        let parts = ResizeArray<IdentifierPart>()
        parts.Add(identifierPart cursor)
        while acceptSymbol '.' cursor do parts.Add(identifierPart cursor)
        Identifier.create (parts |> Seq.toList)

    let private functionName identifier =
        identifier
        |> Identifier.parts
        |> List.map (fun part -> part.Value)
        |> String.concat "."
        |> FunctionName.create

    let rec private parseExpression cursor = parseOr cursor

    and private parseOr cursor =
        let mutable left = parseAnd cursor
        while acceptKeyword "OR" cursor do
            left <- Expr.Binary(BinaryOperator.Or, left, parseAnd cursor)
        left

    and private parseAnd cursor =
        let mutable left = parseComparison cursor
        while acceptKeyword "AND" cursor do
            left <- Expr.Binary(BinaryOperator.And, left, parseComparison cursor)
        left

    and private parseComparison cursor =
        let mutable left = parseConcat cursor
        let mutable keepGoing = true
        while keepGoing do
            let binary op =
                cursor.Advance()
                left <- Expr.Binary(op, left, parseConcat cursor)
            match cursor.Current.Kind with
            | Operator "=" -> binary BinaryOperator.Equal
            | Operator "<>" | Operator "!=" -> binary BinaryOperator.NotEqual
            | Operator ">" -> binary BinaryOperator.GreaterThan
            | Operator "<" -> binary BinaryOperator.LessThan
            | Operator ">=" -> binary BinaryOperator.GreaterThanOrEqual
            | Operator "<=" -> binary BinaryOperator.LessThanOrEqual
            | Keyword "LIKE" -> binary BinaryOperator.Like
            | Keyword "ILIKE" -> binary BinaryOperator.ILike
            | Keyword "IS" ->
                cursor.Advance()
                let negated = acceptKeyword "NOT" cursor
                expectKeyword "NULL" cursor
                left <- Expr.IsNull(left, negated)
            | Keyword "IN" ->
                cursor.Advance()
                left <- parseInTail cursor left false
            | Keyword "BETWEEN" ->
                cursor.Advance()
                left <- parseBetweenTail cursor left false
            | Keyword "NOT" when isKeyword "IN" (cursor.Peek 1) ->
                cursor.Advance(); cursor.Advance()
                left <- parseInTail cursor left true
            | Keyword "NOT" when isKeyword "BETWEEN" (cursor.Peek 1) ->
                cursor.Advance(); cursor.Advance()
                left <- parseBetweenTail cursor left true
            | _ -> keepGoing <- false
        left

    and private parseInTail cursor value negated =
        expectSymbol '(' cursor
        if isKeyword "SELECT" cursor.Current then
            let query = parseQuery cursor
            expectSymbol ')' cursor
            Expr.Binary(
                if negated then BinaryOperator.NotEqual else BinaryOperator.Equal,
                value,
                Expr.ScalarSubquery query)
        else
            let items = ResizeArray<Expr>()
            if not (acceptSymbol ')' cursor) then
                items.Add(parseExpression cursor)
                while acceptSymbol ',' cursor do items.Add(parseExpression cursor)
                expectSymbol ')' cursor
            Expr.InList(value, items |> Seq.toList, negated)

    and private parseBetweenTail cursor value negated =
        let lower = parseConcat cursor
        expectKeyword "AND" cursor
        let upper = parseConcat cursor
        Expr.Between(value, lower, upper, negated)

    and private parseConcat cursor =
        let mutable left = parseAdd cursor
        while acceptOperator "||" cursor do
            left <- Expr.Binary(BinaryOperator.Concat, left, parseAdd cursor)
        left

    and private parseAdd cursor =
        let mutable left = parseMultiply cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "+" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Add, left, parseMultiply cursor)
            | Operator "-" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Subtract, left, parseMultiply cursor)
            | _ -> keepGoing <- false
        left

    and private parseMultiply cursor =
        let mutable left = parseUnary cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "*" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Multiply, left, parseUnary cursor)
            | Operator "/" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Divide, left, parseUnary cursor)
            | Operator "%" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Modulo, left, parseUnary cursor)
            | _ -> keepGoing <- false
        left

    and private parseUnary cursor =
        match cursor.Current.Kind with
        | Keyword "NOT" -> cursor.Advance(); Expr.Unary(UnaryOperator.Not, parseUnary cursor)
        | Operator "-" -> cursor.Advance(); Expr.Unary(UnaryOperator.Negate, parseUnary cursor)
        | Operator "+" -> cursor.Advance(); Expr.Unary(UnaryOperator.Positive, parseUnary cursor)
        | _ -> parsePrimary cursor

    and private parsePrimary cursor =
        let token = cursor.Current
        match token.Kind with
        | IntegerLiteral value -> cursor.Advance(); Expr.Literal(ScalarValue.Integer value)
        | DecimalLiteral value -> cursor.Advance(); Expr.Literal(ScalarValue.Decimal value)
        | StringLiteral value -> cursor.Advance(); Expr.Literal(ScalarValue.Text value)
        | Keyword "NULL" -> cursor.Advance(); Expr.Literal ScalarValue.Null
        | Keyword "TRUE" -> cursor.Advance(); Expr.Literal(ScalarValue.Boolean true)
        | Keyword "FALSE" -> cursor.Advance(); Expr.Literal(ScalarValue.Boolean false)
        | Symbol '(' when isKeyword "SELECT" (cursor.Peek 1) ->
            cursor.Advance()
            let query = parseQuery cursor
            expectSymbol ')' cursor
            Expr.ScalarSubquery query
        | Symbol '(' ->
            cursor.Advance()
            let expression = parseExpression cursor
            expectSymbol ')' cursor
            expression
        | Identifier _ ->
            let name = identifier cursor
            if acceptSymbol '(' cursor then
                let distinct = acceptKeyword "DISTINCT" cursor
                let arguments = ResizeArray<Expr>()
                if not (acceptSymbol ')' cursor) then
                    arguments.Add(parseExpression cursor)
                    while acceptSymbol ',' cursor do arguments.Add(parseExpression cursor)
                    expectSymbol ')' cursor
                Expr.FunctionCall { Name = functionName name; Arguments = arguments |> Seq.toList; IsDistinct = distinct }
            else Expr.Column name
        | _ -> fail token "Expected expression"

    and private parseSelectItem cursor =
        let expression = parseExpression cursor
        let alias = if acceptKeyword "AS" cursor then Some(identifierPart cursor) else None
        { Expression = expression; Alias = alias }

    and private parseReturning cursor =
        if not (acceptKeyword "RETURNING" cursor) then []
        else
            let items = ResizeArray<SelectItem>()
            items.Add(parseSelectItem cursor)
            while acceptSymbol ',' cursor do items.Add(parseSelectItem cursor)
            items |> Seq.toList

    and private parseTableSource cursor =
        if acceptSymbol '(' cursor then
            let query = parseQuery cursor
            expectSymbol ')' cursor
            let alias =
                if acceptKeyword "AS" cursor then identifierPart cursor
                else identifierPart cursor
            TableSource.DerivedTable(query, alias)
        else
            let name = identifier cursor
            let alias =
                if acceptKeyword "AS" cursor then Some(identifierPart cursor)
                else
                    match cursor.Current.Kind with
                    | Identifier _ -> Some(identifierPart cursor)
                    | _ -> None
            TableSource.NamedTable(name, alias)

    and private parseJoin cursor =
        let kind =
            if acceptKeyword "INNER" cursor then expectKeyword "JOIN" cursor; JoinKind.Inner
            elif acceptKeyword "LEFT" cursor then expectKeyword "JOIN" cursor; JoinKind.Left
            elif acceptKeyword "RIGHT" cursor then expectKeyword "JOIN" cursor; JoinKind.Right
            elif acceptKeyword "FULL" cursor then expectKeyword "JOIN" cursor; JoinKind.Full
            elif acceptKeyword "CROSS" cursor then expectKeyword "JOIN" cursor; JoinKind.Cross
            elif acceptKeyword "JOIN" cursor then JoinKind.Inner
            else fail cursor.Current "Expected JOIN"
        let source = parseTableSource cursor
        let predicate =
            match kind with
            | JoinKind.Cross -> None
            | _ -> expectKeyword "ON" cursor; Some(parseExpression cursor)
        { Kind = kind; Source = source; Predicate = predicate }

    and private startsJoin cursor =
        [ "JOIN"; "INNER"; "LEFT"; "RIGHT"; "FULL"; "CROSS" ]
        |> List.exists (fun keyword -> isKeyword keyword cursor.Current)

    and private parseSelect cursor =
        expectKeyword "SELECT" cursor
        let distinct = acceptKeyword "DISTINCT" cursor
        let projection = ResizeArray<SelectItem>()
        projection.Add(parseSelectItem cursor)
        while acceptSymbol ',' cursor do projection.Add(parseSelectItem cursor)
        let from = if acceptKeyword "FROM" cursor then Some(parseTableSource cursor) else None
        let joins = ResizeArray<Join>()
        while startsJoin cursor do joins.Add(parseJoin cursor)
        let where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
        let groupBy = ResizeArray<Expr>()
        if acceptKeyword "GROUP" cursor then
            expectKeyword "BY" cursor
            groupBy.Add(parseExpression cursor)
            while acceptSymbol ',' cursor do groupBy.Add(parseExpression cursor)
        let having = if acceptKeyword "HAVING" cursor then Some(parseExpression cursor) else None
        { Distinct = distinct
          Projection = projection |> Seq.toList
          From = from
          Joins = joins |> Seq.toList
          Where = where
          GroupBy = groupBy |> Seq.toList
          Having = having }

    and private parseOrderBy cursor =
        if not (acceptKeyword "ORDER" cursor) then []
        else
            expectKeyword "BY" cursor
            let items = ResizeArray<OrderBy>()
            let parseItem () =
                let expression = parseExpression cursor
                let descending = if acceptKeyword "DESC" cursor then true else acceptKeyword "ASC" cursor |> ignore; false
                { Expression = expression; Descending = descending; NullOrdering = NullOrdering.Default }
            items.Add(parseItem())
            while acceptSymbol ',' cursor do items.Add(parseItem())
            items |> Seq.toList

    and private parseIntOption keyword cursor =
        if not (acceptKeyword keyword cursor) then None
        else
            let token = cursor.Take()
            match token.Kind with
            | IntegerLiteral value when value <= int64 System.Int32.MaxValue -> Some(int value)
            | _ -> fail token (keyword + " requires a non-negative integer")

    and private parseQuery cursor =
        let head = parseSelect cursor
        let orderBy = parseOrderBy cursor
        let limit = parseIntOption "LIMIT" cursor
        let offset = parseIntOption "OFFSET" cursor
        { Head = head; SetOperations = []; OrderBy = orderBy; Limit = limit; Offset = offset }

    and private parseInsert cursor =
        expectKeyword "INSERT" cursor
        expectKeyword "INTO" cursor
        let target = identifier cursor
        let columns = ResizeArray<IdentifierPart>()
        if acceptSymbol '(' cursor then
            columns.Add(identifierPart cursor)
            while acceptSymbol ',' cursor do columns.Add(identifierPart cursor)
            expectSymbol ')' cursor
        let rows = ResizeArray<Expr list>()
        let mutable source = None
        if acceptKeyword "VALUES" cursor then
            let parseRow () =
                expectSymbol '(' cursor
                let values = ResizeArray<Expr>()
                values.Add(parseExpression cursor)
                while acceptSymbol ',' cursor do values.Add(parseExpression cursor)
                expectSymbol ')' cursor
                values |> Seq.toList
            rows.Add(parseRow())
            while acceptSymbol ',' cursor do rows.Add(parseRow())
        elif isKeyword "SELECT" cursor.Current then
            source <- Some(parseQuery cursor)
        elif acceptKeyword "DEFAULT" cursor then
            expectKeyword "VALUES" cursor
        else fail cursor.Current "Expected VALUES, SELECT, or DEFAULT VALUES"
        { Target = target
          Columns = columns |> Seq.toList
          Rows = rows |> Seq.toList
          Source = source
          Returning = parseReturning cursor }

    and private parseUpdate cursor =
        expectKeyword "UPDATE" cursor
        let target = identifier cursor
        expectKeyword "SET" cursor
        let assignments = ResizeArray<Assignment>()
        let parseAssignment () =
            let targetColumn = identifier cursor
            expectOperator "=" cursor
            { Target = targetColumn; Value = parseExpression cursor }
        assignments.Add(parseAssignment())
        while acceptSymbol ',' cursor do assignments.Add(parseAssignment())
        let where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
        { Target = target
          Assignments = assignments |> Seq.toList
          Where = where
          Returning = parseReturning cursor }

    and private parseDelete cursor =
        expectKeyword "DELETE" cursor
        expectKeyword "FROM" cursor
        let target = identifier cursor
        let where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
        { Target = target; Where = where; Returning = parseReturning cursor }

    let parse (sql: string) =
        let tokens = RewriteLexer.tokenize sql
        let cursor = Cursor(tokens)
        let start = cursor.Current.Start
        let statement =
            match cursor.Current.Kind with
            | Keyword "SELECT" -> Statement.QueryStatement(parseQuery cursor)
            | Keyword "INSERT" -> Statement.InsertStatement(parseInsert cursor)
            | Keyword "UPDATE" -> Statement.UpdateStatement(parseUpdate cursor)
            | Keyword "DELETE" -> Statement.DeleteStatement(parseDelete cursor)
            | _ -> fail cursor.Current "Expected SELECT, INSERT, UPDATE, or DELETE"
        acceptSymbol ';' cursor |> ignore
        match cursor.Current.Kind with
        | End -> ()
        | _ -> fail cursor.Current "Unexpected trailing token"
        Parsed.create
            { Statement = statement
              Span = { Start = start; Length = sql.Length - start } }
