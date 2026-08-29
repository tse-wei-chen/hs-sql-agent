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
        member _.Take() =
            let token = data[index]
            if index < data.Length - 1 then index <- index + 1
            token

    let private fail (token: Token) (message: string) : 'T =
        invalidArg "sql" (message + " at offset " + string token.Start + ".")

    let private isKeyword keyword (token: Token) =
        match token.Kind with
        | Keyword value -> value = keyword
        | _ -> false

    let private isSymbol symbol (token: Token) =
        match token.Kind with
        | Symbol value -> value = symbol
        | _ -> false

    let private isOperator operator (token: Token) =
        match token.Kind with
        | Operator value -> value = operator
        | _ -> false

    let private acceptKeyword keyword (cursor: Cursor) =
        if isKeyword keyword cursor.Current then
            cursor.Advance()
            true
        else false

    let private acceptSymbol symbol (cursor: Cursor) =
        if isSymbol symbol cursor.Current then
            cursor.Advance()
            true
        else false

    let private acceptOperator operator (cursor: Cursor) =
        if isOperator operator cursor.Current then
            cursor.Advance()
            true
        else false

    let private expectKeyword keyword (cursor: Cursor) =
        if not (acceptKeyword keyword cursor) then fail cursor.Current ("Expected " + keyword)

    let private expectSymbol symbol (cursor: Cursor) =
        if not (acceptSymbol symbol cursor) then fail cursor.Current ("Expected '" + string symbol + "'")

    let private expectOperator operator (cursor: Cursor) =
        if not (acceptOperator operator cursor) then fail cursor.Current ("Expected operator '" + operator + "'")

    let private identifierPart (cursor: Cursor) : IdentifierPart =
        let token = cursor.Take()
        match token.Kind with
        | Identifier(value, quoted) ->
            { Value = value
              WasQuoted = quoted
              Span = { Start = token.Start; Length = token.Length } }
        | _ -> fail token "Expected identifier"

    let private identifier (cursor: Cursor) : Identifier =
        let parts = ResizeArray<IdentifierPart>()
        parts.Add(identifierPart cursor)
        while acceptSymbol '.' cursor do parts.Add(identifierPart cursor)
        Identifier.create (parts |> Seq.toList)

    let private functionName (identifier: Identifier) : FunctionName =
        identifier
        |> Identifier.parts
        |> List.map (fun part -> part.Value)
        |> String.concat "."
        |> FunctionName.create

    let rec private parseExpression (cursor: Cursor) : Expr = parseOr cursor

    and private parseOr (cursor: Cursor) : Expr =
        let mutable left = parseAnd cursor
        while acceptKeyword "OR" cursor do
            left <- Expr.Binary(BinaryOperator.Or, left, parseAnd cursor)
        left

    and private parseAnd (cursor: Cursor) : Expr =
        let mutable left = parseComparison cursor
        while acceptKeyword "AND" cursor do
            left <- Expr.Binary(BinaryOperator.And, left, parseComparison cursor)
        left

    and private parseComparison (cursor: Cursor) : Expr =
        let mutable left = parseConcat cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "=" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Equal, left, parseConcat cursor)
            | Operator "<>" | Operator "!=" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.NotEqual, left, parseConcat cursor)
            | Operator ">" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.GreaterThan, left, parseConcat cursor)
            | Operator "<" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.LessThan, left, parseConcat cursor)
            | Operator ">=" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.GreaterThanOrEqual, left, parseConcat cursor)
            | Operator "<=" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.LessThanOrEqual, left, parseConcat cursor)
            | Keyword "LIKE" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Like, left, parseConcat cursor)
            | Keyword "ILIKE" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.ILike, left, parseConcat cursor)
            | Keyword "IS" ->
                cursor.Advance()
                let negated = acceptKeyword "NOT" cursor
                expectKeyword "NULL" cursor
                left <- Expr.IsNull(left, negated)
            | Keyword "IN" -> cursor.Advance(); left <- parseInTail cursor left false
            | Keyword "BETWEEN" -> cursor.Advance(); left <- parseBetweenTail cursor left false
            | Keyword "NOT" when isKeyword "IN" (cursor.Peek 1) ->
                cursor.Advance(); cursor.Advance(); left <- parseInTail cursor left true
            | Keyword "NOT" when isKeyword "BETWEEN" (cursor.Peek 1) ->
                cursor.Advance(); cursor.Advance(); left <- parseBetweenTail cursor left true
            | _ -> keepGoing <- false
        left

    and private parseInTail (cursor: Cursor) (value: Expr) (negated: bool) : Expr =
        expectSymbol '(' cursor
        if isKeyword "SELECT" cursor.Current then
            invalidOp "IN (subquery) is not implemented in the rewrite AST yet."
        let items = ResizeArray<Expr>()
        if not (acceptSymbol ')' cursor) then
            items.Add(parseExpression cursor)
            while acceptSymbol ',' cursor do items.Add(parseExpression cursor)
            expectSymbol ')' cursor
        Expr.InList(value, items |> Seq.toList, negated)

    and private parseBetweenTail (cursor: Cursor) (value: Expr) (negated: bool) : Expr =
        let lower = parseConcat cursor
        expectKeyword "AND" cursor
        let upper = parseConcat cursor
        Expr.Between(value, lower, upper, negated)

    and private parseConcat (cursor: Cursor) : Expr =
        let mutable left = parseAdd cursor
        while acceptOperator "||" cursor do
            left <- Expr.Binary(BinaryOperator.Concat, left, parseAdd cursor)
        left

    and private parseAdd (cursor: Cursor) : Expr =
        let mutable left = parseMultiply cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "+" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Add, left, parseMultiply cursor)
            | Operator "-" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Subtract, left, parseMultiply cursor)
            | _ -> keepGoing <- false
        left

    and private parseMultiply (cursor: Cursor) : Expr =
        let mutable left = parseUnary cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "*" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Multiply, left, parseUnary cursor)
            | Operator "/" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Divide, left, parseUnary cursor)
            | Operator "%" -> cursor.Advance(); left <- Expr.Binary(BinaryOperator.Modulo, left, parseUnary cursor)
            | _ -> keepGoing <- false
        left

    and private parseUnary (cursor: Cursor) : Expr =
        match cursor.Current.Kind with
        | Keyword "NOT" -> cursor.Advance(); Expr.Unary(UnaryOperator.Not, parseUnary cursor)
        | Operator "-" -> cursor.Advance(); Expr.Unary(UnaryOperator.Negate, parseUnary cursor)
        | Operator "+" -> cursor.Advance(); Expr.Unary(UnaryOperator.Positive, parseUnary cursor)
        | _ -> parsePrimary cursor

    and private parsePrimary (cursor: Cursor) : Expr =
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
                Expr.FunctionCall
                    { Name = functionName name
                      Arguments = arguments |> Seq.toList
                      IsDistinct = distinct }
            else Expr.Column name
        | _ -> fail token "Expected expression"

    and private parseSelectItem (cursor: Cursor) : SelectItem =
        let expression = parseExpression cursor
        { Expression = expression
          Alias = if acceptKeyword "AS" cursor then Some(identifierPart cursor) else None }

    and private parseReturning (cursor: Cursor) : SelectItem list =
        if not (acceptKeyword "RETURNING" cursor) then []
        else
            let items = ResizeArray<SelectItem>()
            items.Add(parseSelectItem cursor)
            while acceptSymbol ',' cursor do items.Add(parseSelectItem cursor)
            items |> Seq.toList

    and private parseTableSource (cursor: Cursor) : TableSource =
        if acceptSymbol '(' cursor then
            let query = parseQuery cursor
            expectSymbol ')' cursor
            acceptKeyword "AS" cursor |> ignore
            TableSource.DerivedTable(query, identifierPart cursor)
        else
            let name = identifier cursor
            let alias =
                if acceptKeyword "AS" cursor then Some(identifierPart cursor)
                else
                    match cursor.Current.Kind with
                    | Identifier _ -> Some(identifierPart cursor)
                    | _ -> None
            TableSource.NamedTable(name, alias)

    and private parseJoin (cursor: Cursor) : Join =
        if acceptKeyword "CROSS" cursor then
            expectKeyword "JOIN" cursor
            Join.CrossJoin(parseTableSource cursor)
        else
            let kind =
                if acceptKeyword "INNER" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Inner
                elif acceptKeyword "LEFT" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Left
                elif acceptKeyword "RIGHT" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Right
                elif acceptKeyword "FULL" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Full
                elif acceptKeyword "JOIN" cursor then OnJoinKind.Inner
                else fail cursor.Current "Expected JOIN"
            let source = parseTableSource cursor
            expectKeyword "ON" cursor
            Join.OnJoin(kind, source, parseExpression cursor)

    and private startsJoin (cursor: Cursor) =
        [ "JOIN"; "INNER"; "LEFT"; "RIGHT"; "FULL"; "CROSS" ]
        |> List.exists (fun keyword -> isKeyword keyword cursor.Current)

    and private parseSelect (cursor: Cursor) : Select =
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
          ProjectionItems = projection |> Seq.toList |> NonEmpty.ofList "projection"
          From = from
          Joins = joins |> Seq.toList
          Where = where
          GroupBy = groupBy |> Seq.toList
          Having = having }

    and private parseOrderBy (cursor: Cursor) : OrderBy list =
        if not (acceptKeyword "ORDER" cursor) then []
        else
            expectKeyword "BY" cursor
            let items = ResizeArray<OrderBy>()
            let parseItem () =
                let expression = parseExpression cursor
                let descending =
                    if acceptKeyword "DESC" cursor then true
                    else
                        acceptKeyword "ASC" cursor |> ignore
                        false
                let nullOrdering =
                    if acceptKeyword "NULLS" cursor then
                        if acceptKeyword "FIRST" cursor then NullOrdering.NullsFirst
                        elif acceptKeyword "LAST" cursor then NullOrdering.NullsLast
                        else fail cursor.Current "Expected FIRST or LAST after NULLS"
                    else NullOrdering.Default
                { Expression = expression
                  Descending = descending
                  NullOrdering = nullOrdering }
            items.Add(parseItem())
            while acceptSymbol ',' cursor do items.Add(parseItem())
            items |> Seq.toList

    and private parseNonNegativeInt context (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | IntegerLiteral value when value <= int64 System.Int32.MaxValue -> int value
        | _ -> fail token (context + " requires a non-negative integer")

    and private parseSetOperator (cursor: Cursor) =
        if acceptKeyword "UNION" cursor then
            if acceptKeyword "ALL" cursor then Some SetOperator.UnionAll
            else Some SetOperator.Union
        elif acceptKeyword "INTERSECT" cursor then Some SetOperator.Intersect
        elif acceptKeyword "EXCEPT" cursor then Some SetOperator.Except
        else None

    and private parseQueryTail (cursor: Cursor) =
        let orderBy = parseOrderBy cursor
        let mutable limit = None
        let mutable offset = None

        if acceptKeyword "LIMIT" cursor then
            let first = parseNonNegativeInt "LIMIT" cursor
            if acceptSymbol ',' cursor then
                offset <- Some first
                limit <- Some(parseNonNegativeInt "LIMIT count" cursor)
            else
                limit <- Some first
                if acceptKeyword "OFFSET" cursor then
                    offset <- Some(parseNonNegativeInt "OFFSET" cursor)
        elif acceptKeyword "OFFSET" cursor then
            offset <- Some(parseNonNegativeInt "OFFSET" cursor)
            acceptKeyword "ROW" cursor |> ignore
            acceptKeyword "ROWS" cursor |> ignore
            if acceptKeyword "FETCH" cursor then
                if not (acceptKeyword "FIRST" cursor || acceptKeyword "NEXT" cursor) then
                    fail cursor.Current "Expected FIRST or NEXT after FETCH"
                limit <- Some(parseNonNegativeInt "FETCH" cursor)
                if not (acceptKeyword "ROW" cursor || acceptKeyword "ROWS" cursor) then
                    fail cursor.Current "Expected ROW or ROWS after FETCH count"
                expectKeyword "ONLY" cursor

        orderBy, limit, offset

    and private parseQuery (cursor: Cursor) : Query =
        let head = parseSelect cursor
        let branches = ResizeArray<SetBranch>()
        let mutable scanning = true
        while scanning do
            match parseSetOperator cursor with
            | Some operator ->
                let branchHead = parseSelect cursor
                branches.Add
                    { Operator = operator
                      Query =
                        { Head = branchHead
                          SetOperations = []
                          OrderBy = []
                          Limit = None
                          Offset = None } }
            | None -> scanning <- false
        let orderBy, limit, offset = parseQueryTail cursor
        { Head = head
          SetOperations = branches |> Seq.toList
          OrderBy = orderBy
          Limit = limit
          Offset = offset }

    and private parseInsert (cursor: Cursor) : Insert =
        expectKeyword "INSERT" cursor
        expectKeyword "INTO" cursor
        let target = identifier cursor
        let columns = ResizeArray<IdentifierPart>()
        if acceptSymbol '(' cursor then
            columns.Add(identifierPart cursor)
            while acceptSymbol ',' cursor do columns.Add(identifierPart cursor)
            expectSymbol ')' cursor
        let input =
            if acceptKeyword "VALUES" cursor then
                let rows = ResizeArray<NonEmpty<Expr>>()
                let parseRow () =
                    expectSymbol '(' cursor
                    let values = ResizeArray<Expr>()
                    values.Add(parseExpression cursor)
                    while acceptSymbol ',' cursor do values.Add(parseExpression cursor)
                    expectSymbol ')' cursor
                    values |> Seq.toList |> NonEmpty.ofList "values"
                rows.Add(parseRow())
                while acceptSymbol ',' cursor do rows.Add(parseRow())
                InsertInput.Values(rows |> Seq.toList |> NonEmpty.ofList "rows")
            elif isKeyword "SELECT" cursor.Current then
                InsertInput.QuerySource(parseQuery cursor)
            elif acceptKeyword "DEFAULT" cursor then
                expectKeyword "VALUES" cursor
                InsertInput.DefaultValues
            else fail cursor.Current "Expected VALUES, SELECT, or DEFAULT VALUES"
        { Target = target
          Columns = columns |> Seq.toList
          Input = input
          Returning = parseReturning cursor }

    and private parseUpdate (cursor: Cursor) : Update =
        expectKeyword "UPDATE" cursor
        let target = identifier cursor
        expectKeyword "SET" cursor
        let assignments = ResizeArray<Assignment>()
        let parseAssignment () =
            let targetColumn = identifier cursor
            expectOperator "=" cursor
            { Target = targetColumn
              Value = parseExpression cursor }
        assignments.Add(parseAssignment())
        while acceptSymbol ',' cursor do assignments.Add(parseAssignment())
        { Target = target
          AssignmentItems = assignments |> Seq.toList |> NonEmpty.ofList "assignments"
          Where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
          Returning = parseReturning cursor }

    and private parseDelete (cursor: Cursor) : Delete =
        expectKeyword "DELETE" cursor
        expectKeyword "FROM" cursor
        let target = identifier cursor
        { Target = target
          Where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
          Returning = parseReturning cursor }

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
