namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Globalization
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteParser =

    type SourceDialect = PostgreSql | MySql | SqlServer | SQLite | Oracle | Firebird

    type private Cursor(tokens: Token list, dialect: SourceDialect) =
        let data = List.toArray tokens
        let mutable index = 0
        member _.Current = data[index]
        member _.Peek(offset: int) = data[min (data.Length - 1) (index + offset)]
        member _.Advance() = if index < data.Length - 1 then index <- index + 1
        member _.Take() =
            let token = data[index]
            if index < data.Length - 1 then index <- index + 1
            token
        member _.Dialect = dialect

    let private fail (token: Token) (message: string) : 'T =
        invalidArg "sql" (message + " at offset " + string token.Start + ".")

    let private isKeyword keyword (token: Token) =
        match token.Kind with Keyword value -> value = keyword | _ -> false
    let private isSymbol symbol (token: Token) =
        match token.Kind with Symbol value -> value = symbol | _ -> false
    let private isOperator operator (token: Token) =
        match token.Kind with Operator value -> value = operator | _ -> false

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

    let private partFromToken token =
        match token.Kind with
        | Identifier(value, quoted) ->
            { Value = value; WasQuoted = quoted; Span = { Start = token.Start; Length = token.Length } }
        | _ -> fail token "Expected identifier"

    let private identifierPart (cursor: Cursor) = cursor.Take() |> partFromToken

    let private keywordOrIdentifierText (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | Identifier(value, _) | Keyword value -> value
        | _ -> fail token "Expected identifier or keyword"

    let private identifier (cursor: Cursor) : Identifier =
        let parts = ResizeArray<IdentifierPart>()
        parts.Add(identifierPart cursor)
        let mutable scanning = true
        while scanning && acceptSymbol '.' cursor do
            match cursor.Current.Kind with
            | Identifier _ -> parts.Add(identifierPart cursor)
            | _ -> scanning <- false; fail cursor.Current "Expected identifier after '.'"
        Identifier.create (parts |> Seq.toList)

    let private singlePartIdentifier (part: IdentifierPart) = Identifier.create [ part ]

    let private functionName (identifier: Identifier) : FunctionName =
        identifier |> Identifier.text |> FunctionName.create

    let private parseNonNegativeRowCount context (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | IntegerLiteral value when value >= 0L && value <= int64 Int32.MaxValue -> NonNegativeRowCount.create (int value)
        | _ -> fail token (context + " requires a non-negative integer")

    let private parsePositiveRowCount context (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | IntegerLiteral value when value > 0L && value <= int64 Int32.MaxValue -> PositiveRowCount.create (int value)
        | _ -> fail token (context + " requires a positive integer")

    let private parseCastType (cursor: Cursor) =
        let baseName = keywordOrIdentifierText cursor
        let suffix =
            if acceptSymbol '(' cursor then
                let first = cursor.Take()
                let firstText =
                    match first.Kind with IntegerLiteral value when value >= 0L -> string value | _ -> fail first "CAST type size must be an integer"
                let second =
                    if acceptSymbol ',' cursor then
                        let token = cursor.Take()
                        match token.Kind with IntegerLiteral value when value >= 0L -> "," + string value | _ -> fail token "CAST type scale must be an integer"
                    else ""
                expectSymbol ')' cursor
                "(" + firstText + second + ")"
            else ""
        CastType.create (baseName + suffix)

    let private parseDateLiteral (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | StringLiteral text ->
            match DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, value -> ScalarValue.Date value
            | _ -> fail token "Invalid DATE literal"
        | _ -> fail token "DATE requires a string literal"

    let private parseTimeLiteral (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | StringLiteral text ->
            match TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, value -> ScalarValue.Time value
            | _ -> fail token "Invalid TIME literal"
        | _ -> fail token "TIME requires a string literal"

    let private parseTimestampLiteral (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | StringLiteral text ->
            match DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces) with
            | true, value -> ScalarValue.LocalDateTime value
            | _ -> fail token "Invalid TIMESTAMP literal"
        | _ -> fail token "TIMESTAMP requires a string literal"

    let rec private parseExpression (cursor: Cursor) : Expr = parseOr cursor

    and private parseOr (cursor: Cursor) =
        let mutable left = parseAnd cursor
        while acceptKeyword "OR" cursor do left <- Binary(BinaryOperator.Or, left, parseAnd cursor)
        left

    and private parseAnd (cursor: Cursor) =
        let mutable left = parseComparison cursor
        while acceptKeyword "AND" cursor do left <- Binary(BinaryOperator.And, left, parseComparison cursor)
        left

    and private parseComparison (cursor: Cursor) =
        let mutable left = parseConcat cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "=" -> cursor.Advance(); left <- Binary(BinaryOperator.Equal, left, parseConcat cursor)
            | Operator "<>" | Operator "!=" -> cursor.Advance(); left <- Binary(BinaryOperator.NotEqual, left, parseConcat cursor)
            | Operator ">" -> cursor.Advance(); left <- Binary(BinaryOperator.GreaterThan, left, parseConcat cursor)
            | Operator "<" -> cursor.Advance(); left <- Binary(BinaryOperator.LessThan, left, parseConcat cursor)
            | Operator ">=" -> cursor.Advance(); left <- Binary(BinaryOperator.GreaterThanOrEqual, left, parseConcat cursor)
            | Operator "<=" -> cursor.Advance(); left <- Binary(BinaryOperator.LessThanOrEqual, left, parseConcat cursor)
            | Keyword "LIKE" -> cursor.Advance(); left <- parseLikeTail cursor left false false
            | Keyword "ILIKE" -> cursor.Advance(); left <- parseLikeTail cursor left false true
            | Keyword "IS" ->
                cursor.Advance()
                let negated = acceptKeyword "NOT" cursor
                expectKeyword "NULL" cursor
                left <- IsNull(left, negated)
            | Keyword "IN" -> cursor.Advance(); left <- parseInTail cursor left false
            | Keyword "BETWEEN" -> cursor.Advance(); left <- parseBetweenTail cursor left false
            | Keyword "NOT" when isKeyword "IN" (cursor.Peek 1) -> cursor.Advance(); cursor.Advance(); left <- parseInTail cursor left true
            | Keyword "NOT" when isKeyword "BETWEEN" (cursor.Peek 1) -> cursor.Advance(); cursor.Advance(); left <- parseBetweenTail cursor left true
            | Keyword "NOT" when isKeyword "LIKE" (cursor.Peek 1) -> cursor.Advance(); cursor.Advance(); left <- parseLikeTail cursor left true false
            | Keyword "NOT" when isKeyword "ILIKE" (cursor.Peek 1) -> cursor.Advance(); cursor.Advance(); left <- parseLikeTail cursor left true true
            | _ -> keepGoing <- false
        left

    and private parseLikeTail cursor value negated caseInsensitive =
        let pattern = parseConcat cursor
        let escape = if acceptKeyword "ESCAPE" cursor then Some(parseConcat cursor) else None
        match escape with
        | Some(Literal(ScalarValue.Text text)) when text.Length <> 1 -> fail cursor.Current "LIKE ESCAPE must be exactly one character"
        | _ -> ()
        Like(value, pattern, escape, negated, caseInsensitive)

    and private parseInTail cursor value negated =
        expectSymbol '(' cursor
        if isKeyword "SELECT" cursor.Current || isKeyword "WITH" cursor.Current then
            let query = parseQuery cursor
            expectSymbol ')' cursor
            InSubquery(value, query, negated)
        else
            if acceptSymbol ')' cursor then fail cursor.Current "IN list cannot be empty"
            let items = ResizeArray<Expr>()
            items.Add(parseExpression cursor)
            while acceptSymbol ',' cursor do items.Add(parseExpression cursor)
            expectSymbol ')' cursor
            InList(value, items |> Seq.toList |> NonEmpty.ofList "items", negated)

    and private parseBetweenTail cursor value negated =
        let lower = parseConcat cursor
        expectKeyword "AND" cursor
        let upper = parseConcat cursor
        Between(value, lower, upper, negated)

    and private parseConcat cursor =
        let mutable left = parseAdd cursor
        while acceptOperator "||" cursor do left <- Binary(BinaryOperator.Concat, left, parseAdd cursor)
        left

    and private parseAdd cursor =
        let mutable left = parseMultiply cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "+" -> cursor.Advance(); left <- Binary(BinaryOperator.Add, left, parseMultiply cursor)
            | Operator "-" -> cursor.Advance(); left <- Binary(BinaryOperator.Subtract, left, parseMultiply cursor)
            | _ -> keepGoing <- false
        left

    and private parseMultiply cursor =
        let mutable left = parseUnary cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "*" -> cursor.Advance(); left <- Binary(BinaryOperator.Multiply, left, parseUnary cursor)
            | Operator "/" -> cursor.Advance(); left <- Binary(BinaryOperator.Divide, left, parseUnary cursor)
            | Operator "%" -> cursor.Advance(); left <- Binary(BinaryOperator.Modulo, left, parseUnary cursor)
            | _ -> keepGoing <- false
        left

    and private parseUnary cursor =
        match cursor.Current.Kind with
        | Keyword "NOT" -> cursor.Advance(); Unary(UnaryOperator.Not, parseUnary cursor)
        | Operator "-" ->
            let sign = cursor.Take()
            match cursor.Current.Kind with
            | IntegerLiteral value -> cursor.Advance(); Literal(ScalarValue.Integer(-value))
            | DecimalLiteral value -> cursor.Advance(); Literal(ScalarValue.Decimal(-value))
            | _ -> fail sign "Unary '-' is only supported for numeric literals"
        | Operator "+" ->
            let sign = cursor.Take()
            match cursor.Current.Kind with
            | IntegerLiteral value -> cursor.Advance(); Literal(ScalarValue.Integer value)
            | DecimalLiteral value -> cursor.Advance(); Literal(ScalarValue.Decimal value)
            | _ -> fail sign "Unary '+' is only supported for numeric literals"
        | _ -> parsePostfix cursor

    and private parsePostfix cursor =
        let mutable expression = parsePrimary cursor
        let mutable scanning = true
        while scanning do
            if acceptOperator "::" cursor then
                expression <- Cast(expression, parseCastType cursor)
            elif acceptKeyword "FILTER" cursor then
                expectSymbol '(' cursor
                expectKeyword "WHERE" cursor
                let predicate = parseExpression cursor
                expectSymbol ')' cursor
                expression <- FilteredAggregate(expression, predicate)
            elif acceptKeyword "OVER" cursor then
                expression <- Windowed(expression, parseWindow cursor)
            else scanning <- false
        expression

    and private parseWindow cursor =
        expectSymbol '(' cursor
        let partitions = ResizeArray<Expr>()
        if acceptKeyword "PARTITION" cursor then
            expectKeyword "BY" cursor
            partitions.Add(parseExpression cursor)
            while acceptSymbol ',' cursor do partitions.Add(parseExpression cursor)
        let orderBy = parseOrderBy cursor
        let frame =
            if isKeyword "ROWS" cursor.Current || isKeyword "RANGE" cursor.Current then
                let unit = if acceptKeyword "ROWS" cursor then WindowFrameUnit.Rows else expectKeyword "RANGE" cursor; WindowFrameUnit.Range
                let extent =
                    if acceptKeyword "BETWEEN" cursor then
                        let first = parseFrameBound cursor
                        expectKeyword "AND" cursor
                        BetweenBounds(first, parseFrameBound cursor)
                    else SingleBound(parseFrameBound cursor)
                Some { Unit = unit; Extent = extent }
            else None
        expectSymbol ')' cursor
        { PartitionBy = partitions |> Seq.toList; OrderBy = orderBy; Frame = frame }

    and private parseFrameBound cursor =
        if acceptKeyword "UNBOUNDED" cursor then
            if acceptKeyword "PRECEDING" cursor then UnboundedPreceding
            elif acceptKeyword "FOLLOWING" cursor then UnboundedFollowing
            else fail cursor.Current "Expected PRECEDING or FOLLOWING after UNBOUNDED"
        elif acceptKeyword "CURRENT" cursor then
            expectKeyword "ROW" cursor
            CurrentRow
        else
            let token = cursor.Take()
            match token.Kind with
            | IntegerLiteral value when value >= 0L && value <= int64 Int32.MaxValue ->
                let offset = FrameOffset.create (int value)
                if acceptKeyword "PRECEDING" cursor then Preceding offset
                elif acceptKeyword "FOLLOWING" cursor then Following offset
                else fail cursor.Current "Expected PRECEDING or FOLLOWING after frame offset"
            | _ -> fail token "Expected window frame bound"

    and private parseCase cursor =
        expectKeyword "CASE" cursor
        if acceptKeyword "WHEN" cursor then
            let branches = ResizeArray<SearchedCaseBranch>()
            let parseBranchAfterWhen () =
                let condition = parseExpression cursor
                expectKeyword "THEN" cursor
                { Condition = condition; Result = parseExpression cursor }
            branches.Add(parseBranchAfterWhen())
            while acceptKeyword "WHEN" cursor do branches.Add(parseBranchAfterWhen())
            let fallback = if acceptKeyword "ELSE" cursor then Some(parseExpression cursor) else None
            expectKeyword "END" cursor
            SearchedCase(branches |> Seq.toList |> NonEmpty.ofList "CASE branches", fallback)
        else
            let input = parseExpression cursor
            if not (acceptKeyword "WHEN" cursor) then fail cursor.Current "CASE requires at least one WHEN branch"
            let branches = ResizeArray<SimpleCaseBranch>()
            let parseBranchAfterWhen () =
                let matched = parseExpression cursor
                expectKeyword "THEN" cursor
                { Match = matched; Result = parseExpression cursor }
            branches.Add(parseBranchAfterWhen())
            while acceptKeyword "WHEN" cursor do branches.Add(parseBranchAfterWhen())
            let fallback = if acceptKeyword "ELSE" cursor then Some(parseExpression cursor) else None
            expectKeyword "END" cursor
            SimpleCase(input, branches |> Seq.toList |> NonEmpty.ofList "CASE branches", fallback)

    and private parseCast cursor =
        expectKeyword "CAST" cursor
        expectSymbol '(' cursor
        let value = parseExpression cursor
        expectKeyword "AS" cursor
        let target = parseCastType cursor
        expectSymbol ')' cursor
        match value, CastType.value target with
        | Literal(ScalarValue.Text text), typeName when typeName.Equals("DATE", StringComparison.OrdinalIgnoreCase) ->
            match DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, _ -> Cast(value, target)
            | _ -> fail cursor.Current "Invalid DATE literal in CAST"
        | _ -> Cast(value, target)

    and private parseExtract cursor =
        expectKeyword "EXTRACT" cursor
        expectSymbol '(' cursor
        let field = keywordOrIdentifierText cursor |> ExtractField.create
        expectKeyword "FROM" cursor
        let value = parseExpression cursor
        expectSymbol ')' cursor
        Extract(field, value)

    and private parseIdentifierExpression cursor =
        let parts = ResizeArray<IdentifierPart>()
        parts.Add(identifierPart cursor)
        let mutable wildcard = false
        let mutable scanning = true
        while scanning && acceptSymbol '.' cursor do
            if acceptOperator "*" cursor then wildcard <- true; scanning <- false
            else parts.Add(identifierPart cursor)
        let name = Identifier.create (parts |> Seq.toList)
        if wildcard then Wildcard(Some name)
        elif acceptSymbol '(' cursor then
            let distinct = acceptKeyword "DISTINCT" cursor
            let arguments = ResizeArray<Expr>()
            if not (acceptSymbol ')' cursor) then
                if acceptOperator "*" cursor then arguments.Add(Wildcard None)
                else arguments.Add(parseExpression cursor)
                while acceptSymbol ',' cursor do arguments.Add(parseExpression cursor)
                expectSymbol ')' cursor
            FunctionCall { Name = functionName name; Arguments = arguments |> Seq.toList; IsDistinct = distinct }
        else Column name

    and private parsePrimary cursor =
        let token = cursor.Current
        match token.Kind with
        | IntegerLiteral value -> cursor.Advance(); Literal(ScalarValue.Integer value)
        | DecimalLiteral value -> cursor.Advance(); Literal(ScalarValue.Decimal value)
        | StringLiteral value -> cursor.Advance(); Literal(ScalarValue.Text value)
        | Operator "*" -> cursor.Advance(); Wildcard None
        | Keyword "NULL" -> cursor.Advance(); Literal ScalarValue.Null
        | Keyword "TRUE" ->
            if cursor.Dialect = SourceDialect.SqlServer then fail token "TRUE is not valid in the SQL Server source dialect"
            cursor.Advance(); Literal(ScalarValue.Boolean true)
        | Keyword "FALSE" ->
            if cursor.Dialect = SourceDialect.SqlServer then fail token "FALSE is not valid in the SQL Server source dialect"
            cursor.Advance(); Literal(ScalarValue.Boolean false)
        | Keyword "DATE" ->
            if cursor.Dialect = SourceDialect.SqlServer then fail token "DATE literals are not valid in the SQL Server source dialect"
            cursor.Advance(); Literal(parseDateLiteral cursor)
        | Keyword "TIME" -> cursor.Advance(); Literal(parseTimeLiteral cursor)
        | Keyword "TIMESTAMP" -> cursor.Advance(); Literal(parseTimestampLiteral cursor)
        | Keyword "INTERVAL" ->
            cursor.Advance()
            match cursor.Take().Kind with
            | StringLiteral text -> Interval(IntervalLiteral.create text)
            | _ -> fail token "INTERVAL requires a string literal"
        | Keyword "CASE" -> parseCase cursor
        | Keyword "CAST" -> parseCast cursor
        | Keyword "EXTRACT" -> parseExtract cursor
        | Keyword "EXISTS" ->
            cursor.Advance(); expectSymbol '(' cursor
            let query = parseQuery cursor
            expectSymbol ')' cursor
            Exists(query, false)
        | Symbol '(' when isKeyword "SELECT" (cursor.Peek 1) || isKeyword "WITH" (cursor.Peek 1) ->
            cursor.Advance(); let query = parseQuery cursor in expectSymbol ')' cursor; ScalarSubquery query
        | Symbol '(' -> cursor.Advance(); let expression = parseExpression cursor in expectSymbol ')' cursor; expression
        | Identifier _ -> parseIdentifierExpression cursor
        | _ -> fail token "Expected expression"

    and private parseSelectItem cursor =
        let expression = parseExpression cursor
        { Expression = expression; Alias = if acceptKeyword "AS" cursor then Some(identifierPart cursor) else None }

    and private parseReturning cursor =
        if not (acceptKeyword "RETURNING" cursor) then []
        else
            let items = ResizeArray<SelectItem>()
            items.Add(parseSelectItem cursor)
            while acceptSymbol ',' cursor do items.Add(parseSelectItem cursor)
            let values = items |> Seq.toList
            if values.Length > 1 && values |> List.exists (fun item -> match item.Expression with Wildcard _ -> true | _ -> false) then
                fail cursor.Current "RETURNING wildcard cannot be combined with other expressions"
            values

    and private parseTableSource cursor =
        if acceptSymbol '(' cursor then
            let query = parseQuery cursor
            expectSymbol ')' cursor
            acceptKeyword "AS" cursor |> ignore
            match cursor.Current.Kind with
            | Identifier _ -> DerivedTable(query, identifierPart cursor)
            | _ -> fail cursor.Current "Derived table requires an alias"
        else
            let name = identifier cursor
            let alias =
                if acceptKeyword "AS" cursor then Some(identifierPart cursor)
                else match cursor.Current.Kind with Identifier _ -> Some(identifierPart cursor) | _ -> None
            NamedTable(name, alias)

    and private parseJoin cursor =
        if acceptKeyword "CROSS" cursor then
            expectKeyword "JOIN" cursor
            let source = parseTableSource cursor
            if acceptKeyword "ON" cursor then fail cursor.Current "CROSS JOIN cannot have an ON predicate"
            CrossJoin source
        else
            let kind =
                if acceptKeyword "INNER" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Inner
                elif acceptKeyword "LEFT" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Left
                elif acceptKeyword "RIGHT" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Right
                elif acceptKeyword "FULL" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Full
                elif acceptKeyword "JOIN" cursor then OnJoinKind.Inner
                else fail cursor.Current "Expected JOIN"
            let source = parseTableSource cursor
            if acceptKeyword "USING" cursor then fail cursor.Current "JOIN ... USING is not represented by the portable DU"
            expectKeyword "ON" cursor
            OnJoin(kind, source, parseExpression cursor)

    and private startsJoin cursor =
        [ "JOIN"; "INNER"; "LEFT"; "RIGHT"; "FULL"; "CROSS" ] |> List.exists (fun keyword -> isKeyword keyword cursor.Current)

    and private parseCtes cursor =
        if not (acceptKeyword "WITH" cursor) then []
        else
            if acceptKeyword "RECURSIVE" cursor then fail cursor.Current "WITH RECURSIVE is not supported by the portable compiler"
            let ctes = ResizeArray<Cte>()
            let parseOne () =
                let name = identifierPart cursor
                let aliases = ResizeArray<IdentifierPart>()
                if acceptSymbol '(' cursor then
                    aliases.Add(identifierPart cursor)
                    while acceptSymbol ',' cursor do aliases.Add(identifierPart cursor)
                    expectSymbol ')' cursor
                expectKeyword "AS" cursor
                expectSymbol '(' cursor
                let query = parseQuery cursor
                expectSymbol ')' cursor
                { Name = name; ColumnAliases = aliases |> Seq.toList; Query = query }
            ctes.Add(parseOne())
            while acceptSymbol ',' cursor do ctes.Add(parseOne())
            ctes |> Seq.toList

    and private parseSelectWithCtes cursor ctes =
        expectKeyword "SELECT" cursor
        let distinct = acceptKeyword "DISTINCT" cursor
        let mutable top = None
        if acceptKeyword "TOP" cursor then
            if cursor.Dialect <> SourceDialect.SqlServer then fail cursor.Current "TOP is only valid in the SQL Server source dialect"
            let value = if acceptSymbol '(' cursor then let v = parseNonNegativeRowCount "TOP" cursor in expectSymbol ')' cursor; v else parseNonNegativeRowCount "TOP" cursor
            top <- Some value
        let projection = ResizeArray<SelectItem>()
        projection.Add(parseSelectItem cursor)
        while acceptSymbol ',' cursor do projection.Add(parseSelectItem cursor)
        let from = if acceptKeyword "FROM" cursor then Some(parseTableSource cursor) else None
        let joins = ResizeArray<Join>()
        while acceptSymbol ',' cursor do joins.Add(CrossJoin(parseTableSource cursor))
        while startsJoin cursor do joins.Add(parseJoin cursor)
        let where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
        let groupBy = ResizeArray<Expr>()
        if acceptKeyword "GROUP" cursor then
            expectKeyword "BY" cursor
            groupBy.Add(parseExpression cursor)
            while acceptSymbol ',' cursor do groupBy.Add(parseExpression cursor)
        let having = if acceptKeyword "HAVING" cursor then Some(parseExpression cursor) else None
        { Ctes = ctes
          Distinct = distinct
          ProjectionItems = projection |> Seq.toList |> NonEmpty.ofList "projection"
          From = from
          Joins = joins |> Seq.toList
          Where = where
          GroupBy = groupBy |> Seq.toList
          Having = having }, top

    and private parseOrderItem cursor =
        let expression =
            match cursor.Current.Kind, cursor.Peek 1 with
            | IntegerLiteral value, next when value > 0L && value <= int64 Int32.MaxValue && (isSymbol ',' next || isKeyword "ASC" next || isKeyword "DESC" next || isKeyword "NULLS" next || isKeyword "LIMIT" next || isKeyword "OFFSET" next || isKeyword "FETCH" next || match next.Kind with End | Symbol ')' -> true | _ -> false) ->
                cursor.Advance(); OrderOrdinal(PositiveRowCount.create (int value))
            | _ -> parseExpression cursor
        let descending = if acceptKeyword "DESC" cursor then true else acceptKeyword "ASC" cursor |> ignore; false
        let nullOrdering =
            if acceptKeyword "NULLS" cursor then
                if acceptKeyword "FIRST" cursor then NullOrdering.NullsFirst
                elif acceptKeyword "LAST" cursor then NullOrdering.NullsLast
                else fail cursor.Current "Expected FIRST or LAST after NULLS"
            else NullOrdering.Default
        { Expression = expression; Descending = descending; NullOrdering = nullOrdering }

    and private parseOrderBy cursor =
        if not (acceptKeyword "ORDER" cursor) then []
        else
            expectKeyword "BY" cursor
            let items = ResizeArray<OrderBy>()
            items.Add(parseOrderItem cursor)
            while acceptSymbol ',' cursor do items.Add(parseOrderItem cursor)
            items |> Seq.toList

    and private parseSetOperator cursor =
        if acceptKeyword "UNION" cursor then
            if acceptKeyword "ALL" cursor then Some SetOperator.UnionAll else Some SetOperator.Union
        elif acceptKeyword "INTERSECT" cursor then
            if acceptKeyword "ALL" cursor then fail cursor.Current "INTERSECT ALL is not supported"
            Some SetOperator.Intersect
        elif acceptKeyword "EXCEPT" cursor then
            if acceptKeyword "ALL" cursor then fail cursor.Current "EXCEPT ALL is not supported"
            Some SetOperator.Except
        else None

    and private parseQueryTail cursor =
        let orderBy = parseOrderBy cursor
        let mutable limit = None
        let mutable offset = None
        if acceptKeyword "LIMIT" cursor then
            if cursor.Dialect = SourceDialect.SqlServer || cursor.Dialect = SourceDialect.Oracle || cursor.Dialect = SourceDialect.Firebird then
                fail cursor.Current "LIMIT is not valid in this source dialect"
            let first = parseNonNegativeRowCount "LIMIT" cursor
            if acceptSymbol ',' cursor then
                if cursor.Dialect <> SourceDialect.MySql then fail cursor.Current "LIMIT offset,count is only valid in MySQL"
                offset <- Some first
                limit <- Some(parseNonNegativeRowCount "LIMIT count" cursor)
            else
                limit <- Some first
                if acceptKeyword "OFFSET" cursor then offset <- Some(parseNonNegativeRowCount "OFFSET" cursor)
        elif acceptKeyword "OFFSET" cursor then
            offset <- Some(parseNonNegativeRowCount "OFFSET" cursor)
            acceptKeyword "ROW" cursor |> ignore
            acceptKeyword "ROWS" cursor |> ignore
            if acceptKeyword "FETCH" cursor then
                if not (acceptKeyword "FIRST" cursor || acceptKeyword "NEXT" cursor) then fail cursor.Current "Expected FIRST or NEXT after FETCH"
                limit <- Some(parseNonNegativeRowCount "FETCH" cursor)
                if not (acceptKeyword "ROW" cursor || acceptKeyword "ROWS" cursor) then fail cursor.Current "Expected ROW or ROWS after FETCH count"
                expectKeyword "ONLY" cursor
            elif cursor.Dialect = SourceDialect.SqlServer && orderBy.IsEmpty then
                fail cursor.Current "SQL Server OFFSET requires ORDER BY"
        orderBy, limit, offset

    and private parseQuery cursor =
        let ctes = parseCtes cursor
        let head, top = parseSelectWithCtes cursor ctes
        let branches = ResizeArray<SetBranch>()
        let mutable scanning = true
        while scanning do
            match parseSetOperator cursor with
            | Some operator ->
                let branchHead, branchTop = parseSelectWithCtes cursor []
                branches.Add { Operator = operator; Query = { Head = branchHead; SetOperations = []; OrderBy = []; Limit = branchTop; Offset = None } }
            | None -> scanning <- false
        let orderBy, tailLimit, offset = parseQueryTail cursor
        let limit = match top, tailLimit with Some value, None -> Some value | None, value -> value | Some _, Some _ -> fail cursor.Current "TOP cannot be combined with a second row limit"
        { Head = head; SetOperations = branches |> Seq.toList; OrderBy = orderBy; Limit = limit; Offset = offset }

    and private ensureUniqueInsertColumns cursor columns =
        let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for column in columns do
            if not (seen.Add column.Value) then fail cursor.Current ("Duplicate INSERT target column '" + column.Value + "'")

    and private parseConflict cursor =
        if not (acceptKeyword "ON" cursor) then None
        else
            expectKeyword "CONFLICT" cursor
            expectSymbol '(' cursor
            let targets = ResizeArray<Identifier>()
            targets.Add(singlePartIdentifier (identifierPart cursor))
            while acceptSymbol ',' cursor do targets.Add(singlePartIdentifier (identifierPart cursor))
            expectSymbol ')' cursor
            expectKeyword "DO" cursor
            let action =
                if acceptKeyword "NOTHING" cursor then InsertConflictAction.DoNothing
                elif acceptKeyword "UPDATE" cursor then
                    expectKeyword "SET" cursor
                    let assignments = ResizeArray<ConflictAssignment>()
                    let parseAssignment () =
                        let target = singlePartIdentifier (identifierPart cursor)
                        expectOperator "=" cursor
                        expectKeyword "EXCLUDED" cursor
                        expectSymbol '.' cursor
                        let proposed = singlePartIdentifier (identifierPart cursor)
                        { Target = target; Proposed = proposed }
                    assignments.Add(parseAssignment())
                    while acceptSymbol ',' cursor do assignments.Add(parseAssignment())
                    UpdateProposedValues(assignments |> Seq.toList |> NonEmpty.ofList "conflict assignments")
                else fail cursor.Current "Expected NOTHING or UPDATE after ON CONFLICT DO"
            Some { TargetColumns = targets |> Seq.toList |> NonEmpty.ofList "conflict target"; Action = action }

    and private parseInsert cursor =
        expectKeyword "INSERT" cursor
        expectKeyword "INTO" cursor
        let target = identifier cursor
        let columns = ResizeArray<IdentifierPart>()
        if acceptSymbol '(' cursor then
            columns.Add(identifierPart cursor)
            while acceptSymbol ',' cursor do columns.Add(identifierPart cursor)
            expectSymbol ')' cursor
        ensureUniqueInsertColumns cursor (columns |> Seq.toList)
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
                Values(rows |> Seq.toList |> NonEmpty.ofList "rows")
            elif isKeyword "SELECT" cursor.Current || isKeyword "WITH" cursor.Current then QuerySource(parseQuery cursor)
            elif acceptKeyword "DEFAULT" cursor then expectKeyword "VALUES" cursor; DefaultValues
            else fail cursor.Current "Expected VALUES, SELECT, or DEFAULT VALUES"
        let conflict =
            if isKeyword "ON" cursor.Current then
                if cursor.Dialect <> SourceDialect.PostgreSql && cursor.Dialect <> SourceDialect.SQLite then fail cursor.Current "ON CONFLICT is not valid in this source dialect"
                parseConflict cursor
            else None
        { Target = target; Columns = columns |> Seq.toList; Input = input; Conflict = conflict; Returning = parseReturning cursor }

    and private parseFirebirdUpsert cursor =
        expectKeyword "UPDATE" cursor
        expectKeyword "OR" cursor
        expectKeyword "INSERT" cursor
        expectKeyword "INTO" cursor
        let target = identifier cursor
        expectSymbol '(' cursor
        let columns = ResizeArray<IdentifierPart>()
        columns.Add(identifierPart cursor)
        while acceptSymbol ',' cursor do columns.Add(identifierPart cursor)
        expectSymbol ')' cursor
        ensureUniqueInsertColumns cursor (columns |> Seq.toList)
        expectKeyword "VALUES" cursor
        expectSymbol '(' cursor
        let values = ResizeArray<Expr>()
        values.Add(parseExpression cursor)
        while acceptSymbol ',' cursor do values.Add(parseExpression cursor)
        expectSymbol ')' cursor
        expectKeyword "MATCHING" cursor
        expectSymbol '(' cursor
        let targets = ResizeArray<Identifier>()
        targets.Add(singlePartIdentifier (identifierPart cursor))
        while acceptSymbol ',' cursor do targets.Add(singlePartIdentifier (identifierPart cursor))
        expectSymbol ')' cursor
        let targetNames = targets |> Seq.map Identifier.text |> Set.ofSeq
        let assignments =
            columns
            |> Seq.filter (fun column -> not (targetNames.Contains column.Value))
            |> Seq.map (fun column -> { Target = singlePartIdentifier column; Proposed = singlePartIdentifier column })
            |> Seq.toList
        let action =
            match assignments with
            | [] -> DoNothing
            | values -> UpdateProposedValues(NonEmpty.ofList "conflict assignments" values)
        { Target = target
          Columns = columns |> Seq.toList
          Input = Values(NonEmpty.create (values |> Seq.toList |> NonEmpty.ofList "values") [])
          Conflict = Some { TargetColumns = targets |> Seq.toList |> NonEmpty.ofList "conflict target"; Action = action }
          Returning = parseReturning cursor }

    and private parseNamedDmlSources cursor =
        let values = ResizeArray<TableSource>()
        values.Add(parseTableSource cursor)
        while acceptSymbol ',' cursor do values.Add(parseTableSource cursor)
        values |> Seq.toList

    and private parseUpdate cursor =
        expectKeyword "UPDATE" cursor
        let target = identifier cursor
        match cursor.Current.Kind with
        | Identifier _ -> fail cursor.Current "UPDATE target aliases are not supported by the portable grammar"
        | _ -> ()
        expectKeyword "SET" cursor
        let assignments = ResizeArray<Assignment>()
        let parseAssignment () =
            let targetColumn = identifier cursor
            expectOperator "=" cursor
            { Target = targetColumn; Value = parseExpression cursor }
        assignments.Add(parseAssignment())
        while acceptSymbol ',' cursor do assignments.Add(parseAssignment())
        let from =
            if acceptKeyword "FROM" cursor then
                if cursor.Dialect <> SourceDialect.PostgreSql then fail cursor.Current "UPDATE ... FROM is only supported in the PostgreSQL source dialect"
                parseNamedDmlSources cursor
            else []
        { Target = target
          AssignmentItems = assignments |> Seq.toList |> NonEmpty.ofList "assignments"
          From = from
          Where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
          Returning = parseReturning cursor }

    and private parseDelete cursor =
        expectKeyword "DELETE" cursor
        expectKeyword "FROM" cursor
        let target = identifier cursor
        let using =
            if acceptKeyword "USING" cursor then
                if cursor.Dialect <> SourceDialect.PostgreSql then fail cursor.Current "DELETE ... USING is only supported in the PostgreSQL source dialect"
                parseNamedDmlSources cursor
            else []
        { Target = target
          Using = using
          Where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
          Returning = parseReturning cursor }

    let parseFor dialect (sql: string) =
        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        let tokens = RewriteLexer.tokenize sql
        let cursor = Cursor(tokens, dialect)
        let start = cursor.Current.Start
        let statement =
            match cursor.Current.Kind with
            | Keyword "WITH" | Keyword "SELECT" -> QueryStatement(parseQuery cursor)
            | Keyword "INSERT" -> InsertStatement(parseInsert cursor)
            | Keyword "UPDATE" when isKeyword "OR" (cursor.Peek 1) && isKeyword "INSERT" (cursor.Peek 2) ->
                if dialect <> SourceDialect.Firebird then fail cursor.Current "UPDATE OR INSERT is only supported in the Firebird source dialect"
                InsertStatement(parseFirebirdUpsert cursor)
            | Keyword "UPDATE" -> UpdateStatement(parseUpdate cursor)
            | Keyword "DELETE" -> DeleteStatement(parseDelete cursor)
            | _ -> fail cursor.Current "Expected SELECT, INSERT, UPDATE, or DELETE"
        acceptSymbol ';' cursor |> ignore
        match cursor.Current.Kind with End -> () | _ -> fail cursor.Current "Unexpected trailing token"
        Parsed.create { Statement = statement; Span = { Start = start; Length = sql.Length - start } }

    let parse (sql: string) = parseFor SourceDialect.PostgreSql sql
