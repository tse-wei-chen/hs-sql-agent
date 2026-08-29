namespace HsSqlAgent.SqlCore.Rewrite

open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteParser =

    type private Cursor(tokens: Token list) =
        let data = List.toArray tokens
        let mutable index = 0
        member _.Current = data[index]
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

    let private acceptKeyword keyword (cursor: Cursor) =
        if isKeyword keyword cursor.Current then cursor.Advance(); true else false

    let private acceptSymbol symbol (cursor: Cursor) =
        if isSymbol symbol cursor.Current then cursor.Advance(); true else false

    let private expectKeyword keyword (cursor: Cursor) =
        if not (acceptKeyword keyword cursor) then fail cursor.Current ("Expected " + keyword)

    let private expectSymbol symbol (cursor: Cursor) =
        if not (acceptSymbol symbol cursor) then fail cursor.Current ("Expected '" + string symbol + "'")

    let private identifierPart (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | Identifier(value, quoted) ->
            { Value = value
              WasQuoted = quoted
              Span = { Start = token.Start; Length = token.Length } }
        | Keyword value ->
            { Value = value
              WasQuoted = false
              Span = { Start = token.Start; Length = token.Length } }
        | _ -> fail token "Expected identifier"

    let private identifier (cursor: Cursor) =
        let parts = ResizeArray<IdentifierPart>()
        parts.Add(identifierPart cursor)
        while acceptSymbol '.' cursor do parts.Add(identifierPart cursor)
        Identifier.create (parts |> Seq.toList)

    let private binaryOperator precedence token =
        let result op p = if p = precedence then Some op else None
        match token.Kind with
        | Keyword "OR" -> result BinaryOperator.Or 1
        | Keyword "AND" -> result BinaryOperator.And 2
        | Operator "=" -> result BinaryOperator.Equal 3
        | Operator "<>" | Operator "!=" -> result BinaryOperator.NotEqual 3
        | Operator ">" -> result BinaryOperator.GreaterThan 3
        | Operator "<" -> result BinaryOperator.LessThan 3
        | Operator ">=" -> result BinaryOperator.GreaterThanOrEqual 3
        | Operator "<=" -> result BinaryOperator.LessThanOrEqual 3
        | Keyword "LIKE" -> result BinaryOperator.Like 3
        | Keyword "ILIKE" -> result BinaryOperator.ILike 3
        | Operator "||" -> result BinaryOperator.Concat 4
        | Operator "+" -> result BinaryOperator.Add 5
        | Operator "-" -> result BinaryOperator.Subtract 5
        | Operator "*" -> result BinaryOperator.Multiply 6
        | Operator "/" -> result BinaryOperator.Divide 6
        | Operator "%" -> result BinaryOperator.Modulo 6
        | _ -> None

    let rec private parseExpression (cursor: Cursor) = parseBinary cursor 1

    and private parseBinary (cursor: Cursor) precedence =
        if precedence > 6 then parseUnary cursor
        else
            let mutable left = parseBinary cursor (precedence + 1)
            let mutable keepGoing = true
            while keepGoing do
                match binaryOperator precedence cursor.Current with
                | Some op ->
                    cursor.Advance()
                    let right = parseBinary cursor (precedence + 1)
                    left <- Expr.Binary(op, left, right)
                | None -> keepGoing <- false
            left

    and private parseUnary (cursor: Cursor) =
        match cursor.Current.Kind with
        | Keyword "NOT" -> cursor.Advance(); Expr.Unary(UnaryOperator.Not, parseUnary cursor)
        | Operator "-" -> cursor.Advance(); Expr.Unary(UnaryOperator.Negate, parseUnary cursor)
        | Operator "+" -> cursor.Advance(); Expr.Unary(UnaryOperator.Positive, parseUnary cursor)
        | _ -> parsePrimary cursor

    and private parsePrimary (cursor: Cursor) =
        let token = cursor.Current
        match token.Kind with
        | IntegerLiteral value -> cursor.Advance(); Expr.Literal(ScalarValue.Integer value)
        | DecimalLiteral value -> cursor.Advance(); Expr.Literal(ScalarValue.Decimal value)
        | StringLiteral value -> cursor.Advance(); Expr.Literal(ScalarValue.Text value)
        | Keyword "NULL" -> cursor.Advance(); Expr.Literal ScalarValue.Null
        | Keyword "TRUE" -> cursor.Advance(); Expr.Literal(ScalarValue.Boolean true)
        | Keyword "FALSE" -> cursor.Advance(); Expr.Literal(ScalarValue.Boolean false)
        | Symbol '(' ->
            cursor.Advance()
            let expression = parseExpression cursor
            expectSymbol ')' cursor
            expression
        | Identifier _ | Keyword _ ->
            let name = identifier cursor
            if acceptSymbol '(' cursor then
                let arguments = ResizeArray<Expr>()
                if not (acceptSymbol ')' cursor) then
                    arguments.Add(parseExpression cursor)
                    while acceptSymbol ',' cursor do arguments.Add(parseExpression cursor)
                    expectSymbol ')' cursor
                let fnName =
                    name |> Identifier.parts |> List.map (fun part -> part.Value) |> String.concat "." |> FunctionName.create
                Expr.FunctionCall { Name = fnName; Arguments = arguments |> Seq.toList; IsDistinct = false }
            else Expr.Column name
        | _ -> fail token "Expected expression"

    let private parseSelectItem (cursor: Cursor) =
        let expression = parseExpression cursor
        let alias =
            if acceptKeyword "AS" cursor then Some(identifierPart cursor)
            else None
        { Expression = expression; Alias = alias }

    let private parseTableSource (cursor: Cursor) =
        let name = identifier cursor
        let alias =
            if acceptKeyword "AS" cursor then Some(identifierPart cursor)
            else
                match cursor.Current.Kind with
                | Identifier _ -> Some(identifierPart cursor)
                | _ -> None
        TableSource.NamedTable(name, alias)

    let private parseSelect (cursor: Cursor) =
        expectKeyword "SELECT" cursor
        let distinct = acceptKeyword "DISTINCT" cursor
        let projection = ResizeArray<SelectItem>()
        projection.Add(parseSelectItem cursor)
        while acceptSymbol ',' cursor do projection.Add(parseSelectItem cursor)
        let from = if acceptKeyword "FROM" cursor then Some(parseTableSource cursor) else None
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
          Joins = []
          Where = where
          GroupBy = groupBy |> Seq.toList
          Having = having }

    let parse (sql: string) =
        let tokens = RewriteLexer.tokenize sql
        let cursor = Cursor(tokens)
        let start = cursor.Current.Start
        let select = parseSelect cursor
        acceptSymbol ';' cursor |> ignore
        match cursor.Current.Kind with
        | End -> ()
        | _ -> fail cursor.Current "Unexpected trailing token"
        let query =
            { Head = select
              SetOperations = []
              OrderBy = []
              Limit = None
              Offset = None }
        let document =
            { Statement = Statement.QueryStatement query
              Span = { Start = start; Length = sql.Length - start } }
        Parsed.create document
