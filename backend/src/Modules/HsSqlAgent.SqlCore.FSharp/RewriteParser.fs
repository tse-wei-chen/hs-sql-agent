namespace HsSqlAgent.SqlCore.Rewrite

open System
open System.Globalization
open HsSqlAgent.SqlCore.Core.Compilation
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing
open HsSqlAgent.SqlCore.Rewrite.CoreModel
open HsSqlAgent.SqlCore.Rewrite.RewriteLexer
open HsSqlAgent.SqlCore.Rewrite.Typestate

module internal RewriteParser =

    type SourceDialect = PostgreSql | MySql | SqlServer | SQLite | Oracle | Firebird

    type MySqlPipesSemantics =
        | RejectAmbiguousPipes
        | PipesAsConcat

    type SourceSemantics =
        { EnforceDialectSyntax: bool
          MySqlPipes: MySqlPipesSemantics
          MySqlNoBackslashEscapes: bool
          Joins: JoinProofs
          Expressions: ExpressionProofs
          Dml: DmlProofs
          OnConflict: CapabilityProof
          Ordering: SourceOrderingProofs
          Lexical: RewriteLexer.LexicalSemantics }

    module SourceSemantics =
        let private permissiveJoins =
            { RightJoin = ProvenCapability
              FullJoin = ProvenCapability }

        let private permissiveFilterPredicate =
            { OuterReference = ProvenCapability
              Subquery = ProvenCapability
              WindowFunction = ProvenCapability }

        let private permissiveExpressions =
            { ILike = ProvenCapability
              IntervalLiteral = ProvenCapability
              RegexMatch = ProvenCapability
              AggregateFilter = ProvenCapability
              OffsetTimestamp = ProvenCapability
              FirebirdTimeZoneType = ProvenCapability
              FirebirdExtendedDecimal = ProvenCapability
              StandaloneTime = ProvenCapability
              FilterPredicate = permissiveFilterPredicate }

        let private permissiveDml =
            { Returning = ProvenCapability
              ReturningExpression = ProvenCapability
              UpdateFrom = ProvenCapability
              DeleteUsing = ProvenCapability }

        let private permissiveOrdering =
            { NullsFirst = ProvenCapability
              NullsLast = ProvenCapability }

        let defaultValue =
            { EnforceDialectSyntax = false
              MySqlPipes = RejectAmbiguousPipes
              MySqlNoBackslashEscapes = false
              Joins = permissiveJoins
              Expressions = permissiveExpressions
              Dml = permissiveDml
              OnConflict = ProvenCapability
              Ordering = permissiveOrdering
              Lexical = RewriteLexer.LexicalSemantics.standard }

        let mysqlPipesAsConcat =
            { EnforceDialectSyntax = true
              MySqlPipes = PipesAsConcat
              MySqlNoBackslashEscapes = false
              Joins = permissiveJoins
              Expressions = permissiveExpressions
              Dml = permissiveDml
              OnConflict = ProvenCapability
              Ordering = permissiveOrdering
              Lexical = RewriteLexer.LexicalSemantics.mysql false false }

    type private Cursor(tokens: Token list, dialect: SourceDialect, semantics: SourceSemantics) =
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
        member _.MySqlPipesAsConcat = semantics.MySqlPipes = PipesAsConcat
        member _.MySqlNoBackslashEscapes = semantics.MySqlNoBackslashEscapes
        member _.SourceJoins = semantics.Joins
        member _.SourceExpressions = semantics.Expressions
        member _.SourceDml = semantics.Dml
        member _.SourceOnConflict = semantics.OnConflict
        member _.SourceOrdering = semantics.Ordering

    let private rememberNodeSpan start (cursor: Cursor) (node: obj | null) =
        Parsed.rememberSpan node
            { Start = start
              Length = max 0 (cursor.Current.Start - start) }

    let private markExpr start cursor expression =
        rememberNodeSpan start cursor (box expression)
        expression

    let private sourceDialectName = function
        | SourceDialect.PostgreSql -> "Postgres"
        | SourceDialect.MySql -> "MySQL"
        | SourceDialect.SqlServer -> "MsSqlServer"
        | SourceDialect.SQLite -> "Sqlite"
        | SourceDialect.Oracle -> "Oracle"
        | SourceDialect.Firebird -> "Firebird"

    let private sourceDialectToolType = function
        | SourceDialect.PostgreSql -> SqlAgentToolType.Postgres
        | SourceDialect.MySql -> SqlAgentToolType.MySQL
        | SourceDialect.SqlServer -> SqlAgentToolType.MsSqlServer
        | SourceDialect.SQLite -> SqlAgentToolType.Sqlite
        | SourceDialect.Oracle -> SqlAgentToolType.Oracle
        | SourceDialect.Firebird -> SqlAgentToolType.Firebird

    let private sourceRowLimitGrammar (cursor: Cursor) =
        (SqlSourceDialectGrammarRules.For(sourceDialectToolType cursor.Dialect)).RowLimit

    let private typedTemporalSourceError (cursor: Cursor) spelling : 'T =
        let token = cursor.Current
        let finish = token.Start + max token.Length 1
        raise (SqlParseException(
            "Typed temporal literal " + spelling
            + " is not valid for source dialect " + sourceDialectName cursor.Dialect
            + " in the Core source profile. Position "
            + string token.Start + ", span ["
            + string token.Start + ".." + string finish + ")."))

    let private fail (token: Token) (message: string) : 'T =
        invalidArg "sql" (message + " at offset " + string token.Start + ".")

    let private requireSourceCapability = function
        | ProvenCapability -> ()
        | RejectedCapability message -> raise (SqlCompilationException(message))

    let private requireSourceParseCapability (token: Token) = function
        | ProvenCapability -> ()
        | RejectedCapability message ->
            let finish = token.Start + max token.Length 1
            raise (SqlParseException(
                message
                + " Position "
                + string token.Start
                + ", span ["
                + string token.Start
                + ".."
                + string finish
                + ")."))

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

    let private contextualIdentifierKeywords =
        set [
            "FETCH"; "KEY"; "DATE"; "TIME"; "TIMESTAMP"; "ZONE"; "CONFLICT"; "EXCLUDED"; "PERCENT"
            "DELETE"; "UPDATE"; "INSERT"; "VALUES"; "ESCAPE"; "NOTHING"; "NEXT"; "TIES"
            "WITHIN"; "WITHOUT"; "TOP"; "DUPLICATE"; "MATCHING"; "SEPARATOR"
        ]

    let private isContextualIdentifierKeyword value =
        Set.contains value contextualIdentifierKeywords

    let private asRequiredAliasKeywords =
        set [ "DEFAULT"; "DO"; "INTO"; "ONLY"; "RETURNING" ]

    let private isAliasKeyword value =
        isContextualIdentifierKeyword value || Set.contains value asRequiredAliasKeywords

    let private partFromToken token =
        match token.Kind with
        | Identifier(value, quoted) ->
            { Value = value; WasQuoted = quoted; PreserveSpelling = false; Span = { Start = token.Start; Length = token.Length } }
        | Keyword value when isContextualIdentifierKeyword value ->
            { Value = value; WasQuoted = false; PreserveSpelling = false; Span = { Start = token.Start; Length = token.Length } }
        | _ -> fail token "Expected identifier"

    let private identifierPart (cursor: Cursor) = cursor.Take() |> partFromToken

    let private aliasPartFromToken token =
        match token.Kind with
        | Identifier(value, quoted) ->
            { Value = value; WasQuoted = quoted; PreserveSpelling = false; Span = { Start = token.Start; Length = token.Length } }
        | Keyword value when isAliasKeyword value ->
            { Value = value; WasQuoted = false; PreserveSpelling = false; Span = { Start = token.Start; Length = token.Length } }
        | _ -> fail token "Expected alias"

    let private aliasIdentifierPart (cursor: Cursor) = cursor.Take() |> aliasPartFromToken

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
            | Keyword value when isContextualIdentifierKeyword value -> parts.Add(identifierPart cursor)
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
        | _ -> fail token (context + " requires an integer greater than zero")

    let private castTypeQualifiers =
        set [ "PRECISION"; "VARYING"; "WITH"; "WITHOUT"; "TIME"; "ZONE"; "SIGNED"; "UNSIGNED" ]

    let private tryCastTypeWord (cursor: Cursor) =
        match cursor.Current.Kind with
        | Identifier(value, _)
        | Keyword value ->
            cursor.Advance()
            Some value
        | _ -> None

    let private expectCastTypeWord context (cursor: Cursor) =
        match tryCastTypeWord cursor with
        | Some value -> value
        | None -> fail cursor.Current context

    let private appendCastQualifiers (parts: ResizeArray<string>) (cursor: Cursor) =
        let mutable scanning = true
        while scanning do
            match cursor.Current.Kind with
            | Identifier(value, _)
            | Keyword value when Set.contains value castTypeQualifiers ->
                parts.Add(value)
                cursor.Advance()
            | _ -> scanning <- false

    let private parseCastTypeName (cursor: Cursor) =
        let parts = ResizeArray<string>()
        let mutable baseName = expectCastTypeWord "Expected cast type" cursor

        while acceptSymbol '.' cursor do
            let typeComponent = expectCastTypeWord "Expected cast type component after '.'" cursor
            baseName <- baseName + "." + typeComponent

        parts.Add(baseName)
        appendCastQualifiers parts cursor

        if acceptSymbol '(' cursor then
            let mutable isMax = false
            let first =
                match cursor.Current.Kind with
                | IntegerLiteral value when value >= 0L ->
                    cursor.Advance()
                    string value
                | Identifier(value, _)
                | Keyword value when value.Equals("MAX", StringComparison.OrdinalIgnoreCase) ->
                    cursor.Advance()
                    isMax <- true
                    "MAX"
                | _ ->
                    fail cursor.Current "Cast type precision must be an integer or MAX"

            let mutable suffix = "(" + first
            if acceptSymbol ',' cursor then
                if isMax then
                    fail cursor.Current "Cast type MAX does not accept a scale"
                match cursor.Current.Kind with
                | IntegerLiteral value when value >= 0L ->
                    cursor.Advance()
                    suffix <- suffix + "," + string value
                | _ ->
                    fail cursor.Current "Cast type scale must be an integer"
            expectSymbol ')' cursor
            suffix <- suffix + ")"
            parts[parts.Count - 1] <- parts[parts.Count - 1] + suffix

        appendCastQualifiers parts cursor

        parts
        |> Seq.toList
        |> String.concat " "
        |> CastType.create

    let private parseCastType (cursor: Cursor) =
        parseCastTypeName cursor

    let private parsePostfixCastType (cursor: Cursor) =
        parseCastTypeName cursor

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

    let private localTimestampFormats =
        [| "yyyy-MM-dd HH:mm:ss"
           "yyyy-MM-dd HH:mm:ss.FFFFFFF"
           "yyyy-MM-dd'T'HH:mm:ss"
           "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF" |]

    let private hasExplicitTimestampOffset (text: string) =
        if text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) then true
        elif text.Length < 6 then false
        else
            let start = text.Length - 6
            (text[start] = '+' || text[start] = '-')
            && Char.IsDigit(text[start + 1])
            && Char.IsDigit(text[start + 2])
            && text[start + 3] = ':'
            && Char.IsDigit(text[start + 4])
            && Char.IsDigit(text[start + 5])

    let private timestampLocalPart (text: string) =
        if text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) then
            text.Substring(0, text.Length - 1)
        elif hasExplicitTimestampOffset text then
            text.Substring(0, text.Length - 6)
        else text

    let private tryParseLocalTimestamp (text: string) =
        let mutable value = DateTime.MinValue
        if DateTime.TryParseExact(
            text,
            localTimestampFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            &value) then
            Some(DateTime.SpecifyKind(value, DateTimeKind.Unspecified))
        else None

    let private parseTimestampLiteral (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | StringLiteral text ->
            match tryParseLocalTimestamp (timestampLocalPart text) with
            | None -> fail token "Invalid TIMESTAMP literal"
            | Some _ when hasExplicitTimestampOffset text ->
                match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces) with
                | true, value -> ScalarValue.OffsetDateTime value
                | _ -> fail token "Invalid TIMESTAMP literal"
            | Some local -> ScalarValue.LocalDateTime local
        | _ -> fail token "TIMESTAMP requires a string literal"

    let private parseOffsetTimestampLiteral (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | StringLiteral text when hasExplicitTimestampOffset text ->
            match tryParseLocalTimestamp (timestampLocalPart text) with
            | None -> fail token "Invalid TIMESTAMP WITH TIME ZONE literal"
            | Some _ ->
                match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces) with
                | true, value -> ScalarValue.OffsetDateTime value
                | _ -> fail token "Invalid TIMESTAMP WITH TIME ZONE literal"
        | StringLiteral _ -> fail token "TIMESTAMP WITH TIME ZONE requires an explicit UTC offset or Z suffix"
        | _ -> fail token "TIMESTAMP WITH TIME ZONE requires a string literal"

    let private parseLocalTimestampLiteral (cursor: Cursor) =
        let token = cursor.Take()
        match token.Kind with
        | StringLiteral text when not (hasExplicitTimestampOffset text) ->
            match tryParseLocalTimestamp text with
            | Some value -> ScalarValue.LocalDateTime value
            | None -> fail token "Invalid TIMESTAMP WITHOUT TIME ZONE literal"
        | StringLiteral _ -> fail token "TIMESTAMP WITHOUT TIME ZONE cannot contain a UTC offset or Z suffix"
        | _ -> fail token "TIMESTAMP WITHOUT TIME ZONE requires a string literal"

    let private applyTypedCast (cursor: Cursor) expression target =
        match expression, CastType.value target with
        | Literal(ScalarValue.Text text), typeName when typeName.Equals("DATE", StringComparison.OrdinalIgnoreCase) ->
            match DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, value -> Literal(ScalarValue.Date value)
            | _ -> fail cursor.Current "Invalid DATE literal in CAST"
        | Literal(ScalarValue.Text text), typeName
            when typeName.Equals("DATETIME", StringComparison.OrdinalIgnoreCase)
              || typeName.Equals("DATETIME2", StringComparison.OrdinalIgnoreCase)
              || typeName.Equals("SMALLDATETIME", StringComparison.OrdinalIgnoreCase) ->
            match DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces) with
            | true, value -> Literal(ScalarValue.LocalDateTime(DateTime.SpecifyKind(value, DateTimeKind.Unspecified)))
            | _ -> fail cursor.Current ("Invalid " + typeName + " literal in CAST")
        | _ -> Cast(expression, target)

    let rec private parseExpression (cursor: Cursor) : Expr = parseOr cursor

    and private parseOr (cursor: Cursor) =
        let start = cursor.Current.Start
        let mutable left = parseAnd cursor
        while acceptKeyword "OR" cursor do left <- Binary(BinaryOperator.Or, left, parseAnd cursor)
        markExpr start cursor left

    and private parseAnd (cursor: Cursor) =
        let start = cursor.Current.Start
        let mutable left = parseNot cursor
        while acceptKeyword "AND" cursor do left <- Binary(BinaryOperator.And, left, parseNot cursor)
        markExpr start cursor left

    and private parseNot (cursor: Cursor) =
        let start = cursor.Current.Start
        if acceptKeyword "NOT" cursor then
            Unary(UnaryOperator.Not, parseNot cursor) |> markExpr start cursor
        else
            parseComparison cursor

    and private parseComparison (cursor: Cursor) =
        let start = cursor.Current.Start
        let left = parseAdd cursor
        let result =
            match cursor.Current.Kind with
            | Operator "=" -> cursor.Advance(); Binary(BinaryOperator.Equal, left, parseAdd cursor)
            | Operator "<>" | Operator "!=" -> cursor.Advance(); Binary(BinaryOperator.NotEqual, left, parseAdd cursor)
            | Operator ">" -> cursor.Advance(); Binary(BinaryOperator.GreaterThan, left, parseAdd cursor)
            | Operator "<" -> cursor.Advance(); Binary(BinaryOperator.LessThan, left, parseAdd cursor)
            | Operator ">=" -> cursor.Advance(); Binary(BinaryOperator.GreaterThanOrEqual, left, parseAdd cursor)
            | Operator "<=" -> cursor.Advance(); Binary(BinaryOperator.LessThanOrEqual, left, parseAdd cursor)
            | Keyword "LIKE" -> cursor.Advance(); parseLikeTail cursor left false false
            | Keyword "ILIKE" ->
                requireSourceCapability cursor.SourceExpressions.ILike
                cursor.Advance()
                parseLikeTail cursor left false true
            | Keyword "IS" ->
                cursor.Advance()
                let negated = acceptKeyword "NOT" cursor
                expectKeyword "NULL" cursor
                IsNull(left, negated)
            | Keyword "IN" -> cursor.Advance(); parseInTail cursor left false
            | Keyword "BETWEEN" -> cursor.Advance(); parseBetweenTail cursor left false
            | Keyword "NOT" when isKeyword "IN" (cursor.Peek 1) -> cursor.Advance(); cursor.Advance(); parseInTail cursor left true
            | Keyword "NOT" when isKeyword "BETWEEN" (cursor.Peek 1) -> cursor.Advance(); cursor.Advance(); parseBetweenTail cursor left true
            | Keyword "NOT" when isKeyword "LIKE" (cursor.Peek 1) -> cursor.Advance(); cursor.Advance(); parseLikeTail cursor left true false
            | Keyword "NOT" when isKeyword "ILIKE" (cursor.Peek 1) ->
                requireSourceCapability cursor.SourceExpressions.ILike
                cursor.Advance()
                cursor.Advance()
                parseLikeTail cursor left true true
            | _ -> left
        markExpr start cursor result

    and private parseLikeTail cursor value negated caseInsensitive =
        let pattern = parseAdd cursor
        let hasExplicitEscape = acceptKeyword "ESCAPE" cursor
        if cursor.MySqlNoBackslashEscapes && not hasExplicitEscape then
            fail cursor.Current
                "MySQL source profile declares NO_BACKSLASH_ESCAPES; LIKE requires an explicit single-character ESCAPE so source semantics remain fail-closed"
        let escape =
            if not hasExplicitEscape then
                None
            else
                let token = cursor.Take()
                match token.Kind with
                | StringLiteral text when text.Length = 1 && not (Char.IsControl(text[0])) ->
                    Some(LikeEscape.create text[0])
                | StringLiteral _ ->
                    fail token "LIKE ESCAPE requires exactly one non-control character"
                | _ ->
                    fail token "LIKE ESCAPE requires a single-character string literal in the portable Core grammar"
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
        let lower = parseAdd cursor
        expectKeyword "AND" cursor
        let upper = parseAdd cursor
        Between(value, lower, upper, negated)

    and private parseAdd (cursor: Cursor) =
        let start = cursor.Current.Start
        let mutable left = parseMultiply cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "+" -> cursor.Advance(); left <- Binary(BinaryOperator.Add, left, parseMultiply cursor)
            | Operator "-" -> cursor.Advance(); left <- Binary(BinaryOperator.Subtract, left, parseMultiply cursor)
            | Operator "||" ->
                if cursor.Dialect = SourceDialect.MySql then
                    let message =
                        match SqlConcatCapabilityRules.SourceSemanticValidationError(SqlAgentToolType.MySQL) with
                        | null -> "MySQL '||' semantics require an explicit PIPES_AS_CONCAT or ANSI source-session contract."
                        | value -> value
                    raise (SqlCompilationException(message))
                cursor.Advance()
                left <- Binary(BinaryOperator.Concat, left, parseMultiply cursor)
            | _ -> keepGoing <- false
        markExpr start cursor left

    and private parseMultiply cursor =
        let start = cursor.Current.Start
        let mutable left = parseProfiledConcat cursor
        let mutable keepGoing = true
        while keepGoing do
            match cursor.Current.Kind with
            | Operator "*" -> cursor.Advance(); left <- Binary(BinaryOperator.Multiply, left, parseProfiledConcat cursor)
            | Operator "/" -> cursor.Advance(); left <- Binary(BinaryOperator.Divide, left, parseProfiledConcat cursor)
            | Operator "%" -> cursor.Advance(); left <- Binary(BinaryOperator.Modulo, left, parseProfiledConcat cursor)
            | _ -> keepGoing <- false
        markExpr start cursor left

    and private parseProfiledConcat cursor =
        let start = cursor.Current.Start
        let mutable left = parseUnary cursor
        if cursor.Dialect = SourceDialect.MySql && cursor.MySqlPipesAsConcat then
            while acceptOperator "||" cursor do
                left <- Binary(BinaryOperator.Concat, left, parseUnary cursor)
        markExpr start cursor left

    and private tryParsePostfixCast (cursor: Cursor) (expression: Expr) =
        if not (isOperator "::" cursor.Current) then None
        else
            let token = cursor.Current
            if cursor.Dialect <> SourceDialect.PostgreSql then
                let finish = token.Start + max token.Length 1
                raise (SqlParseException(
                    "PostgreSQL '::' CAST shorthand is not valid for source dialect "
                    + sourceDialectName cursor.Dialect
                    + "; use CAST(... AS ...). Position "
                    + string token.Start + ", span ["
                    + string token.Start + ".." + string finish + ")."))
            cursor.Advance()
            let target = parsePostfixCastType cursor
            Some(applyTypedCast cursor expression target)

    and private parseUnary cursor =
        let parseSigned signMultiplier =
            let sign = cursor.Take()
            let literal =
                match cursor.Current.Kind with
                | IntegerLiteral value ->
                    cursor.Advance()
                    Literal(ScalarValue.Integer(signMultiplier * value))
                | DecimalLiteral value ->
                    cursor.Advance()
                    Literal(ScalarValue.Decimal(decimal signMultiplier * value))
                | _ ->
                    fail sign ("Unary '" + (if signMultiplier < 0L then "-" else "+") + "' is only supported for numeric literals")
            let mutable expression = literal
            let mutable scanning = true
            while scanning do
                match tryParsePostfixCast cursor expression with
                | Some casted -> expression <- casted
                | None -> scanning <- false
            expression

        match cursor.Current.Kind with
        | Operator "-" -> parseSigned -1L
        | Operator "+" -> parseSigned 1L
        | _ -> parsePostfix cursor

    and private parsePostfix cursor =
        let mutable expression = parsePrimary cursor
        let mutable scanning = true
        let mutable withinSeen = false
        let mutable filterSeen = false
        let mutable overSeen = false
        let mutable castSeen = false

        while scanning do
            match cursor.Current.Kind with
            | Operator "::" ->
                match tryParsePostfixCast cursor expression with
                | Some casted ->
                    expression <- casted
                    castSeen <- true
                | None ->
                    scanning <- false
            | Keyword "WITHIN" ->
                if withinSeen || filterSeen || overSeen || castSeen then
                    fail cursor.Current "WITHIN GROUP must precede FILTER, OVER, and postfix CAST"
                cursor.Advance()
                expectKeyword "GROUP" cursor
                expectSymbol '(' cursor
                let ordering : OrderBy list = parseOrderBy false cursor
                if ordering.IsEmpty then fail cursor.Current "WITHIN GROUP requires ORDER BY"
                expectSymbol ')' cursor
                match expression with
                | FunctionCall call when call.AggregateOrderBy.IsEmpty ->
                    expression <-
                        FunctionCall
                            { call with
                                AggregateOrderBy = ordering
                                AggregateOrderSyntax = AggregateOrderSyntax.WithinGroupAggregateOrder }
                    withinSeen <- true
                | FunctionCall _ ->
                    fail cursor.Current "Aggregate ordering cannot be specified more than once"
                | _ ->
                    fail cursor.Current "WITHIN GROUP must modify a function call"
            | Keyword "FILTER" ->
                if filterSeen || overSeen || castSeen then
                    fail cursor.Current "FILTER must appear at most once and before OVER or postfix CAST"
                match expression with
                | FunctionCall _ ->
                    cursor.Advance()
                    expectSymbol '(' cursor
                    expectKeyword "WHERE" cursor
                    let predicate = parseExpression cursor
                    expectSymbol ')' cursor
                    expression <- FilteredAggregate(expression, predicate)
                    filterSeen <- true
                | _ ->
                    fail cursor.Current "FILTER must modify a function call"
            | Keyword "OVER" ->
                if overSeen || castSeen then
                    fail cursor.Current "OVER must appear at most once and before postfix CAST"
                match expression with
                | FunctionCall _
                | FilteredAggregate _ ->
                    cursor.Advance()
                    expression <- Windowed(expression, parseWindow cursor)
                    overSeen <- true
                | _ ->
                    fail cursor.Current "OVER must modify a function call or FILTER result"
            | _ ->
                scanning <- false
        expression

    and private parseWindow cursor =
        expectSymbol '(' cursor
        let partitions = ResizeArray<Expr>()
        if acceptKeyword "PARTITION" cursor then
            expectKeyword "BY" cursor
            partitions.Add(parseExpression cursor)
            while acceptSymbol ',' cursor do partitions.Add(parseExpression cursor)
        let orderBy = parseOrderBy false cursor
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
        applyTypedCast cursor value target

    and private parseExtract cursor =
        expectKeyword "EXTRACT" cursor
        expectSymbol '(' cursor
        let fieldText = keywordOrIdentifierText cursor |> fun value -> value.Trim().ToUpperInvariant()
        if not (SqlDatePartCapabilityRules.IsRepresentedPart(fieldText)) then
            fail cursor.Current (
                "EXTRACT date part '" + fieldText
                + "' is not yet represented by the canonical date-part family")
        let field = ExtractField.create fieldText
        expectKeyword "FROM" cursor
        let value = parseExpression cursor
        expectSymbol ')' cursor
        Extract(field, value)

    and private parseFunctionExpression (name: Identifier) (cursor: Cursor) =
        let nameParts = Identifier.parts name
        if nameParts.Length <> 1 || nameParts.Head.WasQuoted then
            fail cursor.Current "Quoted or qualified function identifiers are not supported by the portable Core grammar"
        expectSymbol '(' cursor
        let distinct = acceptKeyword "DISTINCT" cursor
        if not distinct then
            acceptKeyword "ALL" cursor |> ignore
        let arguments = ResizeArray<Expr>()
        let mutable aggregateOrderBy : OrderBy list = []
        let mutable aggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
        let mutable aggregateSeparator : string option = None

        if not (acceptSymbol ')' cursor) then
            if acceptOperator "*" cursor then
                arguments.Add(Wildcard None)
            else
                arguments.Add(parseExpression cursor)

            let mutable readingArguments = true
            while readingArguments && acceptSymbol ',' cursor do
                arguments.Add(parseExpression cursor)
                readingArguments <- not (isKeyword "ORDER" cursor.Current || isKeyword "SEPARATOR" cursor.Current)

            if isKeyword "ORDER" cursor.Current then
                aggregateOrderBy <- parseOrderBy false cursor
                aggregateOrderSyntax <- AggregateOrderSyntax.InlineAggregateOrder

            if acceptKeyword "SEPARATOR" cursor then
                let token = cursor.Take()
                match token.Kind with
                | StringLiteral value -> aggregateSeparator <- Some value
                | _ -> fail token "SEPARATOR requires a string literal"

            expectSymbol ')' cursor

        let values = arguments |> Seq.toList
        let isRawRegex =
            Identifier.parts name
            |> function
                | [ part ] -> part.Value.Equals("REGEXP_LIKE", StringComparison.OrdinalIgnoreCase)
                | _ -> false
        if isRawRegex then
            if not aggregateOrderBy.IsEmpty || aggregateSeparator.IsSome then
                fail cursor.Current "REGEXP_LIKE cannot carry aggregate modifiers"
            RawRegexCall(values, distinct)
        else
            FunctionCall
                { Name = functionName name
                  Arguments = values
                  IsDistinct = distinct
                  AggregateOrderBy = aggregateOrderBy
                  AggregateOrderSyntax = aggregateOrderSyntax
                  AggregateSeparator = aggregateSeparator }

    and private parseKeywordFunctionExpression (cursor: Cursor) =
        let token = cursor.Take()
        let part =
            match token.Kind with
            | Keyword value ->
                { Value = value
                  WasQuoted = false
                  PreserveSpelling = false
                  Span = { Start = token.Start; Length = token.Length } }
            | _ -> fail token "Expected keyword function"
        parseFunctionExpression (Identifier.create [ part ]) cursor

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
        elif isSymbol '(' cursor.Current then parseFunctionExpression name cursor
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
            if cursor.Dialect = SourceDialect.SqlServer then fail token "TRUE is not valid in T-SQL (SQL Server source dialect); use an explicit predicate or 0 or 1 where a bit value is required"
            cursor.Advance(); Literal(ScalarValue.Boolean true)
        | Keyword "FALSE" ->
            if cursor.Dialect = SourceDialect.SqlServer then fail token "FALSE is not valid in T-SQL (SQL Server source dialect); use an explicit predicate or 0 or 1 where a bit value is required"
            cursor.Advance(); Literal(ScalarValue.Boolean false)
        | Keyword "DATE" when (match (cursor.Peek 1).Kind with | StringLiteral _ -> true | _ -> false) ->
            match cursor.Dialect with
            | SourceDialect.PostgreSql
            | SourceDialect.MySql
            | SourceDialect.Oracle
            | SourceDialect.Firebird ->
                cursor.Advance()
                Literal(parseDateLiteral cursor)
            | SourceDialect.SqlServer
            | SourceDialect.SQLite ->
                typedTemporalSourceError cursor "DATE"
        | Keyword "DATE" ->
            parseIdentifierExpression cursor
        | Keyword "TIME" when isSymbol '(' (cursor.Peek 1) ->
            parseIdentifierExpression cursor
        | Keyword "TIME"
            when (match (cursor.Peek 1).Kind with
                  | StringLiteral _ -> true
                  | Keyword "WITH"
                  | Keyword "WITHOUT" -> true
                  | _ -> false) ->
            match cursor.Dialect with
            | SourceDialect.PostgreSql
            | SourceDialect.MySql
            | SourceDialect.Firebird ->
                cursor.Advance()
                if acceptKeyword "WITH" cursor then
                    typedTemporalSourceError cursor "TIME WITH TIME ZONE"
                elif acceptKeyword "WITHOUT" cursor then
                    if not (acceptKeyword "TIME" cursor && acceptKeyword "ZONE" cursor) then
                        fail cursor.Current "Expected TIME ZONE after TIME WITHOUT"
                    if cursor.Dialect <> SourceDialect.PostgreSql then
                        typedTemporalSourceError cursor "TIME WITHOUT TIME ZONE"
                    Literal(parseTimeLiteral cursor)
                else
                    Literal(parseTimeLiteral cursor)
            | SourceDialect.SqlServer
            | SourceDialect.SQLite
            | SourceDialect.Oracle ->
                typedTemporalSourceError cursor "TIME"
        | Keyword "TIME" ->
            parseIdentifierExpression cursor
        | Keyword "TIMESTAMP" when isSymbol '(' (cursor.Peek 1) ->
            parseIdentifierExpression cursor
        | Keyword "TIMESTAMP"
            when (match (cursor.Peek 1).Kind with
                  | StringLiteral _
                  | Keyword "WITH"
                  | Keyword "WITHOUT" -> true
                  | _ -> false) ->
            cursor.Advance()
            if acceptKeyword "WITH" cursor then
                if not (acceptKeyword "TIME" cursor && acceptKeyword "ZONE" cursor) then
                    fail cursor.Current "Expected TIME ZONE after TIMESTAMP WITH"
                if cursor.Dialect <> SourceDialect.PostgreSql then
                    typedTemporalSourceError cursor "TIMESTAMP WITH TIME ZONE"
                Literal(parseOffsetTimestampLiteral cursor)
            elif acceptKeyword "WITHOUT" cursor then
                if not (acceptKeyword "TIME" cursor && acceptKeyword "ZONE" cursor) then
                    fail cursor.Current "Expected TIME ZONE after TIMESTAMP WITHOUT"
                if cursor.Dialect <> SourceDialect.PostgreSql then
                    typedTemporalSourceError cursor "TIMESTAMP WITHOUT TIME ZONE"
                Literal(parseLocalTimestampLiteral cursor)
            else
                match cursor.Dialect with
                | SourceDialect.PostgreSql
                | SourceDialect.MySql
                | SourceDialect.Oracle
                | SourceDialect.Firebird ->
                    Literal(parseTimestampLiteral cursor)
                | SourceDialect.SqlServer
                | SourceDialect.SQLite ->
                    typedTemporalSourceError cursor "TIMESTAMP"
        | Keyword "TIMESTAMP" ->
            parseIdentifierExpression cursor
        | Keyword "CURRENT_DATE" ->
            cursor.Advance()
            if acceptSymbol '(' cursor then expectSymbol ')' cursor
            FunctionCall
                { Name = FunctionName.create "CURRENT_DATE"
                  Arguments = []
                  IsDistinct = false
                  AggregateOrderBy = []
                  AggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
                  AggregateSeparator = None }
        | Keyword "CURRENT_TIME" ->
            cursor.Advance()
            if acceptSymbol '(' cursor then expectSymbol ')' cursor
            FunctionCall
                { Name = FunctionName.create "CURRENT_TIME"
                  Arguments = []
                  IsDistinct = false
                  AggregateOrderBy = []
                  AggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
                  AggregateSeparator = None }
        | Keyword "CURRENT_TIMESTAMP" ->
            cursor.Advance()
            if acceptSymbol '(' cursor then expectSymbol ')' cursor
            FunctionCall
                { Name = FunctionName.create "CURRENT_TIMESTAMP"
                  Arguments = []
                  IsDistinct = false
                  AggregateOrderBy = []
                  AggregateOrderSyntax = AggregateOrderSyntax.NoAggregateOrder
                  AggregateSeparator = None }
        | Keyword "INTERVAL" ->
            requireSourceCapability cursor.SourceExpressions.IntervalLiteral
            cursor.Advance()
            match cursor.Take().Kind with
            | StringLiteral text -> Interval(IntervalLiteral.create text)
            | _ -> fail token "INTERVAL requires a string literal"
        | Keyword "CASE" -> parseCase cursor
        | Keyword "CAST" -> parseCast cursor
        | Keyword "EXTRACT" -> parseExtract cursor
        | Keyword "LEFT" when isSymbol '(' (cursor.Peek 1) -> parseKeywordFunctionExpression cursor
        | Keyword "RIGHT" when isSymbol '(' (cursor.Peek 1) -> parseKeywordFunctionExpression cursor
        | Keyword "EXISTS" ->
            cursor.Advance(); expectSymbol '(' cursor
            let query = parseQuery cursor
            expectSymbol ')' cursor
            Exists(query, false)
        | Symbol '(' when isKeyword "SELECT" (cursor.Peek 1) || isKeyword "WITH" (cursor.Peek 1) ->
            cursor.Advance(); let query = parseQuery cursor in expectSymbol ')' cursor; ScalarSubquery query
        | Symbol '(' -> cursor.Advance(); let expression = parseExpression cursor in expectSymbol ')' cursor; expression
        | Identifier(value, false) when cursor.Dialect = SourceDialect.Oracle && value.Equals("SYSDATE", StringComparison.OrdinalIgnoreCase) ->
            cursor.Advance()
            if acceptSymbol '(' cursor then
                if not (acceptSymbol ')' cursor) then fail cursor.Current "SYSDATE() does not accept arguments"
                raise (SqlCompilationException("Function 'SYSDATE' is not registered as a portable Core source function."))
            else
                let finish = token.Start + max token.Length 1
                raise (SqlParseException(
                    "Oracle bare SYSDATE is not represented as a portable Core temporal expression. Position "
                    + string token.Start + ", span [" + string token.Start + ".." + string finish + ")."))
        | Keyword value when isContextualIdentifierKeyword value -> parseIdentifierExpression cursor
        | Identifier _ -> parseIdentifierExpression cursor
        | _ -> fail token "Expected expression"

    and private parseSelectItem cursor =
        let expression = parseExpression cursor
        let alias =
            if acceptKeyword "AS" cursor then Some(aliasIdentifierPart cursor)
            else
                match cursor.Current.Kind with
                | Identifier _ -> Some(identifierPart cursor)
                | Keyword "FETCH" when not (isKeyword "FIRST" (cursor.Peek 1) || isKeyword "NEXT" (cursor.Peek 1)) ->
                    Some(identifierPart cursor)
                | Keyword value when value <> "FETCH" && isContextualIdentifierKeyword value ->
                    Some(identifierPart cursor)
                | _ -> None
        { Expression = expression; Alias = alias }

    and private parseReturning cursor =
        if not (acceptKeyword "RETURNING" cursor) then []
        else
            requireSourceParseCapability cursor.Current cursor.SourceDml.Returning

            let seenColumns = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

            let parseItem () =
                let expression = parseExpression cursor
                let alias =
                    if acceptKeyword "AS" cursor then Some(aliasIdentifierPart cursor)
                    else None

                if alias.IsNone then
                    match cursor.Current.Kind with
                    | Identifier _ ->
                        fail cursor.Current "RETURNING alias requires AS"
                    | Keyword value when isAliasKeyword value ->
                        fail cursor.Current "RETURNING alias requires AS"
                    | _ -> ()

                match expression, alias with
                | Wildcard None, Some _ ->
                    fail cursor.Current "RETURNING wildcard cannot be aliased"
                | Wildcard None, None ->
                    ReturningWildcard None
                | Column identifier, None when Identifier.parts identifier |> List.length = 1 ->
                    let name = (Identifier.parts identifier).Head.Value
                    if not (seenColumns.Add name) then
                        fail cursor.Current ("RETURNING column '" + name + "' is declared more than once")
                    ReturningColumn(identifier, None)
                | Column identifier, None ->
                    fail cursor.Current "RETURNING column references must be unqualified in the portable Core grammar"
                | expression, alias ->
                    requireSourceCapability cursor.SourceDml.ReturningExpression
                    ReturningExpression(expression, alias)

            let items = ResizeArray<ReturningItem>()
            items.Add(parseItem())
            while acceptSymbol ',' cursor do items.Add(parseItem())
            let values = items |> Seq.toList
            if values.Length > 1
               && values |> List.exists (function ReturningWildcard _ -> true | _ -> false) then
                fail cursor.Current "RETURNING wildcard cannot be mixed with columns or expressions"
            values

    and private parseTableSource (cursor: Cursor) =
        if isKeyword "LATERAL" cursor.Current then
            fail cursor.Current "LATERAL sources are not represented by the portable Core grammar"
        elif acceptSymbol '(' cursor then
            let query = parseQuery cursor
            expectSymbol ')' cursor
            acceptKeyword "AS" cursor |> ignore
            match cursor.Current.Kind with
            | Identifier _ -> DerivedTable(query, identifierPart cursor)
            | Keyword value when isContextualIdentifierKeyword value ->
                DerivedTable(query, identifierPart cursor)
            | _ -> fail cursor.Current "Derived table requires an alias"
        else
            let name = identifier cursor
            let alias =
                if acceptKeyword "AS" cursor then Some(identifierPart cursor)
                else
                    match cursor.Current.Kind with
                    | Identifier _ -> Some(identifierPart cursor)
                    | Keyword "FETCH" when not (isKeyword "FIRST" (cursor.Peek 1) || isKeyword "NEXT" (cursor.Peek 1)) ->
                        Some(identifierPart cursor)
                    | Keyword value when value <> "FETCH" && isContextualIdentifierKeyword value ->
                        Some(identifierPart cursor)
                    | _ -> None
            NamedTable(name, alias)

    and private parseJoin (cursor: Cursor) =
        let requireJoinProof proof =
            match proof with
            | ProvenCapability -> ()
            | RejectedCapability message -> raise (SqlCompilationException(message))

        if isKeyword "NATURAL" cursor.Current then
            fail cursor.Current "NATURAL JOIN is rejected because its schema-dependent implicit predicate is not represented by the portable Core grammar"
        elif acceptKeyword "CROSS" cursor then
            expectKeyword "JOIN" cursor
            let source = parseTableSource cursor
            if isKeyword "ON" cursor.Current || isKeyword "USING" cursor.Current then
                fail cursor.Current "CROSS JOIN must not have ON/USING predicates"
            CrossJoin source
        else
            let kind =
                if acceptKeyword "INNER" cursor then expectKeyword "JOIN" cursor; OnJoinKind.Inner
                elif acceptKeyword "LEFT" cursor then
                    acceptKeyword "OUTER" cursor |> ignore
                    expectKeyword "JOIN" cursor
                    OnJoinKind.Left
                elif acceptKeyword "RIGHT" cursor then
                    acceptKeyword "OUTER" cursor |> ignore
                    expectKeyword "JOIN" cursor
                    OnJoinKind.Right
                elif acceptKeyword "FULL" cursor then
                    acceptKeyword "OUTER" cursor |> ignore
                    expectKeyword "JOIN" cursor
                    OnJoinKind.Full
                elif acceptKeyword "JOIN" cursor then OnJoinKind.Inner
                else fail cursor.Current "Expected JOIN"

            match kind with
            | OnJoinKind.Right -> requireJoinProof cursor.SourceJoins.RightJoin
            | OnJoinKind.Full -> requireJoinProof cursor.SourceJoins.FullJoin
            | OnJoinKind.Inner | OnJoinKind.Left -> ()

            let source = parseTableSource cursor
            if acceptKeyword "USING" cursor then fail cursor.Current "JOIN ... USING is not represented by the portable DU"
            expectKeyword "ON" cursor
            OnJoin(kind, source, parseExpression cursor)

    and private startsJoin (cursor: Cursor) =
        [ "JOIN"; "INNER"; "LEFT"; "RIGHT"; "FULL"; "CROSS"; "NATURAL" ]
        |> List.exists (fun keyword -> isKeyword keyword cursor.Current)

    and private parseCtes (cursor: Cursor) =
        if not (acceptKeyword "WITH" cursor) then []
        else
            if acceptKeyword "RECURSIVE" cursor then fail cursor.Current "WITH RECURSIVE is not supported by the portable compiler"
            let ctes = ResizeArray<Cte>()
            let parseOne () =
                let start = cursor.Current.Start
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
                let cte = { Name = name; ColumnAliases = aliases |> Seq.toList; Query = query }
                rememberNodeSpan start cursor (box cte)
                cte
            ctes.Add(parseOne())
            while acceptSymbol ',' cursor do ctes.Add(parseOne())
            ctes |> Seq.toList

    and private parseSelectWithCtes (cursor: Cursor) (ctes: Cte list) =
        let start = cursor.Current.Start
        expectKeyword "SELECT" cursor
        let distinct =
            if acceptKeyword "DISTINCT" cursor then true
            else
                acceptKeyword "ALL" cursor |> ignore
                false
        let mutable top = None
        if cursor.Dialect = SourceDialect.SqlServer && acceptKeyword "TOP" cursor then
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
        let select =
            { Ctes = ctes
              Distinct = distinct
              ProjectionItems = projection |> Seq.toList |> NonEmpty.ofList "projection"
              From = from
              Joins = joins |> Seq.toList
              Where = where
              GroupBy = groupBy |> Seq.toList
              Having = having }
        rememberNodeSpan start cursor (box select)
        select, top

    and private parseOrderItem allowOrdinal (cursor: Cursor) =
        let ordinalBoundary next =
            isSymbol ',' next
            || isKeyword "ASC" next
            || isKeyword "DESC" next
            || isKeyword "NULLS" next
            || isKeyword "LIMIT" next
            || isKeyword "OFFSET" next
            || isKeyword "FETCH" next
            || match next.Kind with End | Symbol ')' -> true | _ -> false

        let expression =
            match cursor.Current.Kind, cursor.Peek 1 with
            | IntegerLiteral 0L, next when allowOrdinal && ordinalBoundary next ->
                raise (SqlCompilationException("ORDER BY ordinal must be positive (greater than zero)."))
            | IntegerLiteral value, next when allowOrdinal && value > 0L && value <= int64 Int32.MaxValue && ordinalBoundary next ->
                cursor.Advance()
                OrderOrdinal(PositiveRowCount.create (int value))
            | _ -> parseExpression cursor
        let descending = if acceptKeyword "DESC" cursor then true else acceptKeyword "ASC" cursor |> ignore; false
        let nullOrdering =
            if acceptKeyword "NULLS" cursor then
                if acceptKeyword "FIRST" cursor then
                    requireSourceCapability cursor.SourceOrdering.NullsFirst
                    NullOrdering.NullsFirst
                elif acceptKeyword "LAST" cursor then
                    requireSourceCapability cursor.SourceOrdering.NullsLast
                    NullOrdering.NullsLast
                else fail cursor.Current "Expected FIRST or LAST after NULLS"
            else NullOrdering.Default
        { Expression = expression; Descending = descending; NullOrdering = nullOrdering }

    and private parseOrderBy allowOrdinal cursor =
        if not (acceptKeyword "ORDER" cursor) then []
        else
            expectKeyword "BY" cursor
            let items = ResizeArray<OrderBy>()
            items.Add(parseOrderItem allowOrdinal cursor)
            while acceptSymbol ',' cursor do items.Add(parseOrderItem allowOrdinal cursor)
            items |> Seq.toList

    and private parseSetOperator cursor =
        if acceptKeyword "UNION" cursor then
            if acceptKeyword "ALL" cursor then Some SetOperator.UnionAll
            else
                acceptKeyword "DISTINCT" cursor |> ignore
                Some SetOperator.Union
        elif acceptKeyword "INTERSECT" cursor then
            if acceptKeyword "ALL" cursor then fail cursor.Current "INTERSECT ALL is not supported"
            acceptKeyword "DISTINCT" cursor |> ignore
            Some SetOperator.Intersect
        elif acceptKeyword "EXCEPT" cursor then
            if acceptKeyword "ALL" cursor then fail cursor.Current "EXCEPT ALL is not supported"
            acceptKeyword "DISTINCT" cursor |> ignore
            Some SetOperator.Except
        else None

    and private parseQueryTail (cursor: Cursor) =
        let orderBy = parseOrderBy true cursor
        let mutable limit = None
        let mutable offset = None
        let mutable usedCommaLimit = false
        let grammar = sourceRowLimitGrammar cursor

        let parseOffsetRowKeyword () =
            if grammar.UsesStandardOffsetFetch then
                if grammar.OffsetRowKeywordOptional then
                    if not (acceptKeyword "ROW" cursor) then
                        acceptKeyword "ROWS" cursor |> ignore
                elif not (acceptKeyword "ROW" cursor || acceptKeyword "ROWS" cursor) then
                    fail cursor.Current (
                        sourceDialectName cursor.Dialect
                        + " OFFSET requires ROW or ROWS after the offset count")

        let parseFetch () =
            if not grammar.SupportsFetch then
                fail cursor.Current (
                    "FETCH FIRST/NEXT is not valid for source dialect "
                    + sourceDialectName cursor.Dialect)
            if grammar.FetchRequiresPrecedingOffset && offset.IsNone then
                fail cursor.Current "SQL Server FETCH requires a preceding OFFSET"
            if not (acceptKeyword "FIRST" cursor || acceptKeyword "NEXT" cursor) then
                fail cursor.Current "Expected FIRST or NEXT after FETCH"

            let count =
                if isKeyword "ROW" cursor.Current || isKeyword "ROWS" cursor.Current then
                    if not grammar.FetchCountOptional then
                        fail cursor.Current "SQL Server FETCH requires an explicit positive integer row count"
                    1
                elif grammar.FetchCountMustBePositive then
                    parsePositiveRowCount "FETCH row count" cursor |> PositiveRowCount.value
                else
                    parseNonNegativeRowCount "FETCH row count" cursor |> NonNegativeRowCount.value

            if acceptKeyword "PERCENT" cursor then
                fail cursor.Current "FETCH ... PERCENT is not represented by the portable compiler"
            if not (acceptKeyword "ROW" cursor || acceptKeyword "ROWS" cursor) then
                fail cursor.Current "Expected ROW or ROWS after FETCH count"
            if acceptKeyword "WITH" cursor then
                expectKeyword "TIES" cursor
                fail cursor.Current "FETCH ... WITH TIES is not represented by the portable compiler"
            expectKeyword "ONLY" cursor
            limit <- Some(NonNegativeRowCount.create count)

        if acceptKeyword "LIMIT" cursor then
            if not grammar.SupportsLimitKeyword then
                fail cursor.Current (
                    "LIMIT is not valid in source dialect " + sourceDialectName cursor.Dialect
                    + "; use the dialect's native row-limiting syntax")

            if acceptKeyword "ALL" cursor then
                if not grammar.SupportsLimitAll then
                    fail cursor.Current (
                        "LIMIT ALL is valid only for PostgreSQL; source dialect "
                        + sourceDialectName cursor.Dialect + " remains fail-closed")
            else
                let first = parseNonNegativeRowCount "LIMIT" cursor
                if acceptSymbol ',' cursor then
                    if not grammar.SupportsCommaLimit then
                        fail cursor.Current "LIMIT offset,row_count is only valid in MySQL and SQLite"
                    usedCommaLimit <- true
                    offset <- Some first
                    limit <- Some(parseNonNegativeRowCount "LIMIT count" cursor)
                else
                    limit <- Some first

            if acceptKeyword "OFFSET" cursor then
                if usedCommaLimit then
                    fail cursor.Current "LIMIT offset,row_count cannot be combined with a separate OFFSET clause"
                if grammar.OffsetRequiresOrderBy && orderBy.IsEmpty then
                    fail cursor.Current (
                        sourceDialectName cursor.Dialect + " OFFSET/FETCH requires ORDER BY")
                offset <- Some(parseNonNegativeRowCount "OFFSET" cursor)
                parseOffsetRowKeyword ()

            if isKeyword "FETCH" cursor.Current then
                fail cursor.Current "LIMIT and FETCH cannot be combined"
        elif acceptKeyword "OFFSET" cursor then
            if grammar.OffsetRequiresLimit then
                fail cursor.Current (
                    "OFFSET requires a preceding LIMIT for source dialect "
                    + sourceDialectName cursor.Dialect)
            if grammar.OffsetRequiresOrderBy && orderBy.IsEmpty then
                fail cursor.Current (
                    sourceDialectName cursor.Dialect + " OFFSET/FETCH requires ORDER BY")
            offset <- Some(parseNonNegativeRowCount "OFFSET" cursor)
            parseOffsetRowKeyword ()
            if acceptKeyword "FETCH" cursor then parseFetch ()
        elif acceptKeyword "FETCH" cursor then
            parseFetch ()

        orderBy, limit, offset

    and private parseQuery cursor =
        let start = cursor.Current.Start
        let ctes = parseCtes cursor
        let head, top = parseSelectWithCtes cursor ctes
        let branches = ResizeArray<SetBranch>()
        let mutable scanning = true
        while scanning do
            match parseSetOperator cursor with
            | Some operator ->
                let branchQuery =
                    if acceptSymbol '(' cursor then
                        let query = parseQuery cursor
                        expectSymbol ')' cursor
                        query
                    else
                        let branchHead, branchTop = parseSelectWithCtes cursor []
                        { Head = branchHead
                          SetOperations = []
                          OrderBy = []
                          Limit = branchTop
                          Offset = None }
                branches.Add { Operator = operator; Query = branchQuery }
            | None -> scanning <- false
        let orderBy, tailLimit, offset = parseQueryTail cursor
        let limit = match top, tailLimit with Some value, None -> Some value | None, value -> value | Some _, Some _ -> fail cursor.Current "TOP cannot be combined with OFFSET/FETCH row limiting"
        let query = { Head = head; SetOperations = branches |> Seq.toList; OrderBy = orderBy; Limit = limit; Offset = offset }
        rememberNodeSpan start cursor (box query)
        query

    and private ensureUniqueInsertColumns (cursor: Cursor) (columns: IdentifierPart list) =
        let seen = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for column in columns do
            if not (seen.Add column.Value) then fail cursor.Current ("Duplicate INSERT target column '" + column.Value + "'")

    and private parseConflict (cursor: Cursor) =
        if not (acceptKeyword "ON" cursor) then None
        else
            expectKeyword "CONFLICT" cursor
            expectSymbol '(' cursor
            let targets = ResizeArray<Identifier>()
            let seenTargets = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            let parseTarget () =
                let part = identifierPart cursor
                if not (seenTargets.Add part.Value) then
                    fail cursor.Current ("ON CONFLICT target column '" + part.Value + "' is declared more than once")
                singlePartIdentifier part
            targets.Add(parseTarget())
            while acceptSymbol ',' cursor do targets.Add(parseTarget())
            expectSymbol ')' cursor
            expectKeyword "DO" cursor
            let action =
                if acceptKeyword "NOTHING" cursor then InsertConflictAction.DoNothing
                elif acceptKeyword "UPDATE" cursor then
                    expectKeyword "SET" cursor
                    let assignments = ResizeArray<ConflictAssignment>()
                    let seenAssignments = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    let parseAssignment () : ConflictAssignment =
                        let targetPart = identifierPart cursor
                        if not (seenAssignments.Add targetPart.Value) then
                            fail cursor.Current ("ON CONFLICT UPDATE assigns column '" + targetPart.Value + "' more than once")
                        let target = singlePartIdentifier targetPart
                        expectOperator "=" cursor
                        expectKeyword "EXCLUDED" cursor
                        expectSymbol '.' cursor
                        let proposed = singlePartIdentifier (identifierPart cursor)
                        { Target = target; Proposed = proposed }
                    assignments.Add(parseAssignment())
                    while acceptSymbol ',' cursor do assignments.Add(parseAssignment())
                    match cursor.Current.Kind with
                    | End
                    | Symbol ';'
                    | Keyword "RETURNING" -> ()
                    | _ ->
                        fail cursor.Current
                            "ON CONFLICT DO UPDATE conflict clause supports only target = EXCLUDED.source assignments; arbitrary expressions remain fail-closed"
                    UpdateProposedValues(assignments |> Seq.toList |> NonEmpty.ofList "conflict assignments")
                else fail cursor.Current "Expected NOTHING or UPDATE after ON CONFLICT DO"
            Some { TargetColumns = targets |> Seq.toList |> NonEmpty.ofList "conflict target"; Action = action }

    and private parseInsert (cursor: Cursor) =
        expectKeyword "INSERT" cursor
        expectKeyword "INTO" cursor
        let target = identifier cursor
        let columns = ResizeArray<IdentifierPart>()
        if acceptSymbol '(' cursor then
            columns.Add(identifierPart cursor)
            while acceptSymbol ',' cursor do columns.Add(identifierPart cursor)
            expectSymbol ')' cursor
        ensureUniqueInsertColumns cursor (columns |> Seq.toList)
        let requiresExplicitColumns =
            isKeyword "VALUES" cursor.Current
            || isKeyword "SELECT" cursor.Current
            || isKeyword "WITH" cursor.Current
        if columns.Count = 0 && requiresExplicitColumns then
            fail cursor.Current "INSERT requires an explicit INSERT column list"
        let input =
            if acceptKeyword "VALUES" cursor then
                let rows = ResizeArray<NonEmpty<Expr>>()
                let parseRow () =
                    expectSymbol '(' cursor
                    let values = ResizeArray<Expr>()
                    values.Add(parseExpression cursor)
                    while acceptSymbol ',' cursor do values.Add(parseExpression cursor)
                    expectSymbol ')' cursor
                    if values.Count <> columns.Count then
                        fail cursor.Current (
                            "INSERT row has " + string values.Count
                            + " values but " + string columns.Count
                            + " columns were declared")
                    values |> Seq.toList |> NonEmpty.ofList "values"
                rows.Add(parseRow())
                while acceptSymbol ',' cursor do rows.Add(parseRow())
                Values(rows |> Seq.toList |> NonEmpty.ofList "rows")
            elif isKeyword "SELECT" cursor.Current || isKeyword "WITH" cursor.Current then QuerySource(parseQuery cursor)
            elif acceptKeyword "DEFAULT" cursor then expectKeyword "VALUES" cursor; DefaultValues
            else fail cursor.Current "Expected VALUES, SELECT, or DEFAULT VALUES"
        let conflict =
            if isKeyword "ON" cursor.Current
               && isKeyword "DUPLICATE" (cursor.Peek 1)
               && isKeyword "KEY" (cursor.Peek 2) then
                fail cursor.Current
                    "MySQL ON DUPLICATE KEY UPDATE has no explicit conflict target and is not represented by the deterministic portable conflict clause"
            elif isKeyword "ON" cursor.Current then
                if cursor.Dialect <> SourceDialect.PostgreSql && cursor.Dialect <> SourceDialect.SQLite then
                    fail cursor.Current "ON CONFLICT is not valid in this source dialect"
                requireSourceParseCapability cursor.Current cursor.SourceOnConflict
                parseConflict cursor
            else None
        { Target = target; Columns = columns |> Seq.toList; Input = input; Conflict = conflict; Returning = parseReturning cursor }

    and private parseFirebirdUpsert (cursor: Cursor) =
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
        if not (acceptKeyword "MATCHING" cursor) then
            fail cursor.Current "Firebird UPDATE OR INSERT requires explicit MATCHING"
        expectSymbol '(' cursor
        let targets = ResizeArray<Identifier>()
        targets.Add(singlePartIdentifier (identifierPart cursor))
        while acceptSymbol ',' cursor do targets.Add(singlePartIdentifier (identifierPart cursor))
        expectSymbol ')' cursor
        let assignments =
            columns
            |> Seq.map (fun column -> { Target = singlePartIdentifier column; Proposed = singlePartIdentifier column })
            |> Seq.toList
        let action =
            UpdateProposedValues(NonEmpty.ofList "conflict assignments" assignments)
        { Target = target
          Columns = columns |> Seq.toList
          Input = Values(NonEmpty.create (values |> Seq.toList |> NonEmpty.ofList "values") [])
          Conflict = Some { TargetColumns = targets |> Seq.toList |> NonEmpty.ofList "conflict target"; Action = action }
          Returning = parseReturning cursor }

    and private parseNamedDmlSources cursor =
        let parseOne () =
            match parseTableSource cursor with
            | NamedTable(_, None) as source -> source
            | NamedTable(_, Some _) ->
                fail cursor.Current "UPDATE FROM / DELETE USING source aliases are not supported by the portable grammar"
            | CteTable _ ->
                fail cursor.Current "UPDATE FROM / DELETE USING CTE sources are not supported by the portable grammar"
            | DerivedTable _ ->
                fail cursor.Current "UPDATE FROM / DELETE USING derived sources and aliases are not supported by the portable grammar"

        let values = ResizeArray<TableSource>()
        values.Add(parseOne())
        while acceptSymbol ',' cursor do values.Add(parseOne())
        values |> Seq.toList

    and private parseUpdate cursor =
        expectKeyword "UPDATE" cursor
        let target = identifier cursor
        match cursor.Current.Kind with
        | Identifier _
        | Keyword "AS" -> fail cursor.Current "UPDATE target aliases are not supported by the portable grammar"
        | _ -> ()
        expectKeyword "SET" cursor
        let assignments = ResizeArray<Assignment>()
        let seenAssignments = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let parseAssignment () =
            let targetColumn = identifier cursor
            let parts = Identifier.parts targetColumn
            if parts.Length <> 1 then
                fail cursor.Current "UPDATE assignment columns must be unqualified"
            let columnName = parts.Head.Value
            if not (seenAssignments.Add columnName) then
                fail cursor.Current ("UPDATE assigns column '" + columnName + "' more than once")
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
        match cursor.Current.Kind with
        | Identifier _
        | Keyword "AS" -> fail cursor.Current "DELETE target aliases are not supported by the portable grammar"
        | _ -> ()
        let using =
            if acceptKeyword "USING" cursor then
                if cursor.Dialect <> SourceDialect.PostgreSql then fail cursor.Current "DELETE ... USING is only supported in the PostgreSQL source dialect"
                parseNamedDmlSources cursor
            else []
        { Target = target
          Using = using
          Where = if acceptKeyword "WHERE" cursor then Some(parseExpression cursor) else None
          Returning = parseReturning cursor }

    let parseForWith semantics dialect (sql: string) =
        if String.IsNullOrWhiteSpace(sql) then invalidArg "sql" "SQL text cannot be empty."
        let tokens = RewriteLexer.tokenizeWith semantics.Lexical sql
        let cursor = Cursor(tokens, dialect, semantics)
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

    let parseFor dialect sql = parseForWith SourceSemantics.defaultValue dialect sql

    let parse (sql: string) = parseFor SourceDialect.PostgreSql sql
