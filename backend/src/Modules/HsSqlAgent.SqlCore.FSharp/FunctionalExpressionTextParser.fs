namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Globalization
open System.Text
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing

/// Expression grammar implemented in F# while preserving the existing token
/// stream and AST contracts.
module internal FunctionalExpressionTextParser =

    [<Literal>]
    let MySqlPipesConcatToken =
        "__CORE_MYSQL_PIPES_CONCAT_TOKEN__"

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    type internal ExpressionGrammar(
        reader: CoreTokenReader,
        parseSubquery: Func<SqlStatement>,
        requireExplicitLikeEscape: bool) =

        member private _.Identifier(
            value: string,
            span: SourceSpan,
            wasQuoted: bool) =

            SqlIdentifier(
                ImmutableArray.Create(
                    IdentifierPart(
                        value,
                        wasQuoted,
                        span)),
                span)

        member private this.IdentifierFromToken(token: Token) =
            this.Identifier(
                token.Value,
                CoreTokenReader.Span(token),
                CoreTokenReader.IsQuotedIdentifier(token))

        member private _.IsTemporalType(value: string) =
            value.Equals("DATE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("TIME", StringComparison.OrdinalIgnoreCase)
            || value.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase)

        member private _.IsCastTypeQualifier(value: string) =
            value.Equals("PRECISION", StringComparison.OrdinalIgnoreCase)
            || value.Equals("VARYING", StringComparison.OrdinalIgnoreCase)
            || value.Equals("WITH", StringComparison.OrdinalIgnoreCase)
            || value.Equals("WITHOUT", StringComparison.OrdinalIgnoreCase)
            || value.Equals("TIME", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ZONE", StringComparison.OrdinalIgnoreCase)
            || value.Equals("SIGNED", StringComparison.OrdinalIgnoreCase)
            || value.Equals("UNSIGNED", StringComparison.OrdinalIgnoreCase)

        member private _.IsComparisonOperator(value: string) =
            value = "="
            || value = "<>"
            || value = "!="
            || value = ">"
            || value = "<"
            || value = ">="
            || value = "<="

        member private _.ParseNumber(value: string) : obj | null =
            let isIntegerSpelling =
                value.IndexOf('.') < 0
                && value.IndexOf('e') < 0
                && value.IndexOf('E') < 0

            if isIntegerSpelling then
                match Int32.TryParse(
                    value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture) with
                | true, integer ->
                    box integer
                | false, _ ->
                    box (
                        Decimal.Parse(
                            value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture))
            else
                box (
                    Decimal.Parse(
                        value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture))

        member private _.DecodeString(token: string) =
            token
                .Substring(1, token.Length - 2)
                .Replace(
                    "''",
                    "'",
                    StringComparison.Ordinal)

        member private this.ParseOr() : SqlExpr =
            let start = reader.Position
            let mutable left : SqlExpr = this.ParseAnd()

            while reader.MatchWord("OR") do
                left <-
                    BinaryExpr(
                        left,
                        "OR",
                        this.ParseAnd(),
                        reader.SpanFrom(start))

            left

        member private this.ParseAnd() : SqlExpr =
            let start = reader.Position
            let mutable left : SqlExpr = this.ParseNot()

            while reader.MatchWord("AND") do
                left <-
                    BinaryExpr(
                        left,
                        "AND",
                        this.ParseNot(),
                        reader.SpanFrom(start))

            left

        member private this.ParseNot() : SqlExpr =
            if not (reader.PeekWord("NOT")) then
                this.ParsePredicate()
            else
                let start = reader.Position
                reader.Advance() |> ignore

                UnaryExpr(
                    "NOT",
                    this.ParseNot(),
                    reader.SpanFrom(start))
                :> SqlExpr

        member private this.ParsePredicate() : SqlExpr =
            let start = reader.Position
            let left = this.ParseAdditive()
            let token = reader.Peek()

            if token.Type = TokenType.Operator
               && this.IsComparisonOperator(token.Value) then
                let raw = reader.Advance().Value
                let right = this.ParseAdditive()

                BinaryExpr(
                    left,
                    (if raw = "!=" then "<>" else raw),
                    right,
                    reader.SpanFrom(start))
                :> SqlExpr

            elif reader.MatchWord("IS") then
                let negated = reader.MatchWord("NOT")

                if not (reader.MatchWord("NULL")) then
                    raise (CoreTokenReader.Error(
                        "Core predicates currently support IS [NOT] NULL only.",
                        reader.Peek()))

                IsNullExpr(
                    left,
                    negated,
                    reader.SpanFrom(start))
                :> SqlExpr

            else
                let negatedModifier =
                    if reader.PeekWord("NOT")
                       && (reader.PeekWord(1, "IN")
                           || reader.PeekWord(1, "BETWEEN")
                           || reader.PeekWord(1, "LIKE")
                           || reader.PeekWord(1, "ILIKE")) then
                        reader.Advance() |> ignore
                        true
                    else
                        false

                if reader.MatchWord("IN") then
                    reader.Expect(
                        TokenType.LParen,
                        "'(' after IN")
                    |> ignore

                    if reader.PeekWord("SELECT")
                       || reader.PeekWord("WITH") then

                        let query = parseSubquery.Invoke()

                        reader.Expect(
                            TokenType.RParen,
                            "')' after IN subquery")
                        |> ignore

                        BinaryExpr(
                            left,
                            (if negatedModifier then
                                "NOT IN"
                             else
                                "IN"),
                            SubqueryExpr(
                                query,
                                query.Span),
                            reader.SpanFrom(start))
                        :> SqlExpr
                    else
                        if reader.Peek().Type = TokenType.RParen then
                            raise (CoreTokenReader.Error(
                                "IN expression list cannot be empty.",
                                reader.Peek()))

                        let items = ResizeArray<SqlExpr>()
                        let mutable keepReading = true

                        while keepReading do
                            items.Add(this.ParseExpression())
                            keepReading <-
                                reader.Match(TokenType.Comma)

                        reader.Expect(
                            TokenType.RParen,
                            "')' after IN expression list")
                        |> ignore

                        InExpr(
                            left,
                            items |> toImmutableArray,
                            negatedModifier,
                            reader.SpanFrom(start))
                        :> SqlExpr

                elif reader.MatchWord("BETWEEN") then
                    let lower = this.ParseAdditive()
                    reader.ExpectWord("AND") |> ignore
                    let upper = this.ParseAdditive()

                    BetweenExpr(
                        left,
                        lower,
                        upper,
                        negatedModifier,
                        reader.SpanFrom(start))
                    :> SqlExpr

                elif reader.MatchWord("LIKE")
                     || reader.MatchWord("ILIKE") then

                    let op =
                        reader.Peek(-1).Value.ToUpperInvariant()

                    let right = this.ParseAdditive()

                    let likeEscape =
                        if reader.MatchWord("ESCAPE") then
                            let escapeToken = reader.Peek()

                            if escapeToken.Type <> TokenType.String then
                                raise (CoreTokenReader.Error(
                                    "LIKE ESCAPE requires a single-character string literal in the portable Core grammar.",
                                    escapeToken))

                            reader.Advance() |> ignore

                            let value =
                                this.DecodeString(
                                    escapeToken.Value)

                            if value.Length <> 1
                               || Char.IsControl(value[0]) then
                                raise (CoreTokenReader.Error(
                                    "LIKE ESCAPE requires exactly one non-control character.",
                                    escapeToken))

                            Some value
                        else
                            None

                    if requireExplicitLikeEscape
                       && op = "LIKE"
                       && Option.isNone likeEscape then
                        raise (CoreTokenReader.Error(
                            "MySQL LIKE under NO_BACKSLASH_ESCAPES requires an explicit single-character ESCAPE clause so Core does not guess pattern escape semantics.",
                            reader.Peek(-1)))

                    let binary =
                        BinaryExpr(
                            left,
                            op,
                            right,
                            reader.SpanFrom(start),
                            Option.toObj likeEscape)

                    if negatedModifier then
                        UnaryExpr(
                            "NOT",
                            binary,
                            reader.SpanFrom(start))
                        :> SqlExpr
                    else
                        binary :> SqlExpr

                elif negatedModifier then
                    raise (CoreTokenReader.Error(
                        "NOT must be followed by IN, BETWEEN, LIKE, or ILIKE in this predicate position.",
                        reader.Peek()))

                else
                    left

        member private this.ParseAdditive() : SqlExpr =
            let start = reader.Position
            let mutable left : SqlExpr =
                this.ParseMultiplicative()

            let isOperator() =
                reader.Peek().Type = TokenType.Operator
                && (reader.Peek().Value = "+"
                    || reader.Peek().Value = "-"
                    || reader.Peek().Value = "||")

            while isOperator() do
                let op = reader.Advance().Value
                left <-
                    BinaryExpr(
                        left,
                        op,
                        this.ParseMultiplicative(),
                        reader.SpanFrom(start))

            left

        member private this.ParseMultiplicative() : SqlExpr =
            let start = reader.Position
            let mutable left : SqlExpr =
                this.ParseProfiledConcat()

            let isOperator() =
                reader.Peek().Type = TokenType.Operator
                && (reader.Peek().Value = "*"
                    || reader.Peek().Value = "/"
                    || reader.Peek().Value = "%")

            while isOperator() do
                let op = reader.Advance().Value
                left <-
                    BinaryExpr(
                        left,
                        op,
                        this.ParseProfiledConcat(),
                        reader.SpanFrom(start))

            left

        member private this.ParseProfiledConcat() : SqlExpr =
            let start = reader.Position
            let mutable left : SqlExpr = this.ParsePostfix()

            while reader.Peek().Type = TokenType.Operator
                  && reader.Peek().Value = MySqlPipesConcatToken do

                reader.Advance() |> ignore

                left <-
                    BinaryExpr(
                        left,
                        "||",
                        this.ParsePostfix(),
                        reader.SpanFrom(start))

            left

        member private this.ParsePostfix() : SqlExpr =
            let start = reader.Position
            let mutable expression : SqlExpr =
                this.ParseUnaryNumeric()

            while reader.Peek().Type = TokenType.Operator
                  && reader.Peek().Value = "::" do

                reader.Advance() |> ignore

                expression <-
                    CastExpr(
                        expression,
                        this.ParseCastTypeName(),
                        reader.SpanFrom(start))

            expression

        member private this.ParseUnaryNumeric() : SqlExpr =
            let token = reader.Peek()

            if token.Type <> TokenType.Operator
               || (token.Value <> "+"
                   && token.Value <> "-") then
                this.ParsePrimary()
            else
                let start = reader.Position
                let sign = reader.Advance()
                let numberToken = reader.Peek()

                if numberToken.Type <> TokenType.Number then
                    raise (CoreTokenReader.Error(
                        $"Unary '{sign.Value}' is accepted only for numeric literals; general unary arithmetic is not represented by the Core lowerer.",
                        numberToken))

                let parsed =
                    this.ParseNumber(
                        reader.Advance().Value)

                let value =
                    if sign.Value <> "-" then
                        parsed
                    else
                        match parsed with
                        | :? int as integer ->
                            // Preserve the legacy raw-query parser contract:
                            // signed negative integral literals are carried as
                            // decimal values at the SQL parameter boundary.
                            box (decimal (-integer))
                        | :? decimal as number ->
                            box (-number)
                        | _ ->
                            raise (CoreTokenReader.Error(
                                "Unsupported signed numeric literal.",
                                sign))

                LiteralExpr(
                    value,
                    reader.SpanFrom(start))
                :> SqlExpr

        member private this.IsTemporalLiteralStart(token: Token) =
            if CoreTokenReader.IsQuotedIdentifier(token)
               || not (this.IsTemporalType(token.Value)) then
                false
            elif reader.Peek(1).Type = TokenType.String then
                true
            else
                (token.Value = "TIME"
                 || token.Value = "TIMESTAMP")
                && (reader.PeekWord(1, "WITH")
                    || reader.PeekWord(1, "WITHOUT"))

        member private this.ParsePrimary() : SqlExpr =
            let start = reader.Position
            let token = reader.Peek()

            if reader.MatchWord("CASE") then
                this.ParseCase(start)

            elif reader.MatchWord("CAST") then
                this.ParseCast(start)

            elif reader.MatchWord("EXISTS") then
                reader.Expect(
                    TokenType.LParen,
                    "'(' after EXISTS")
                |> ignore

                let query = parseSubquery.Invoke()

                reader.Expect(
                    TokenType.RParen,
                    "')' after EXISTS subquery")
                |> ignore

                ExistsExpr(
                    query,
                    false,
                    reader.SpanFrom(start))
                :> SqlExpr

            elif reader.MatchWord("NULL") then
                LiteralExpr(
                    null,
                    reader.SpanFrom(start))
                :> SqlExpr

            elif reader.MatchWord("TRUE") then
                LiteralExpr(
                    box true,
                    reader.SpanFrom(start))
                :> SqlExpr

            elif reader.MatchWord("FALSE") then
                LiteralExpr(
                    box false,
                    reader.SpanFrom(start))
                :> SqlExpr

            elif reader.Match(TokenType.LParen) then
                if reader.PeekWord("SELECT")
                   || reader.PeekWord("WITH") then
                    let query = parseSubquery.Invoke()

                    reader.Expect(
                        TokenType.RParen,
                        "')' after scalar subquery")
                    |> ignore

                    SubqueryExpr(
                        query,
                        reader.SpanFrom(start))
                    :> SqlExpr
                else
                    let inner = this.ParseExpression()

                    reader.Expect(
                        TokenType.RParen,
                        "')' after expression")
                    |> ignore

                    CoreParserAstClone.WithSpan(
                        inner,
                        reader.SpanFrom(start))

            elif token.Type = TokenType.Number then
                reader.Advance() |> ignore

                LiteralExpr(
                    this.ParseNumber(token.Value),
                    reader.SpanFrom(start))
                :> SqlExpr

            elif token.Type = TokenType.String then
                reader.Advance() |> ignore

                LiteralExpr(
                    this.DecodeString(token.Value),
                    reader.SpanFrom(start))
                :> SqlExpr

            elif token.Type = TokenType.Parameter then
                raise (CoreTokenReader.Error(
                    $"Unbound SQL parameter '{token.Value}'.",
                    token))

            elif this.IsTemporalLiteralStart(token) then
                this.ParseTemporalLiteral(start)

            elif reader.PeekWord("INTERVAL")
                 && reader.Peek(1).Type = TokenType.String then

                reader.Advance() |> ignore

                IntervalExpr(
                    this.DecodeString(
                        reader.Advance().Value),
                    reader.SpanFrom(start))
                :> SqlExpr

            elif reader.PeekWord("CURRENT_DATE")
                 || reader.PeekWord("CURRENT_TIME")
                 || reader.PeekWord("CURRENT_TIMESTAMP") then

                let nameToken = reader.Advance()

                if reader.Match(TokenType.LParen) then
                    reader.Expect(
                        TokenType.RParen,
                        "')' after current temporal function")
                    |> ignore

                FunctionCallExpr(
                    this.IdentifierFromToken(nameToken),
                    ImmutableArray<SqlExpr>.Empty,
                    false,
                    reader.SpanFrom(start))
                :> SqlExpr

            elif reader.PeekWord("EXTRACT")
                 && reader.Peek(1).Type = TokenType.LParen then
                this.ParseExtract(start)

            elif (token.Type = TokenType.Identifier
                  || token.Type = TokenType.Keyword)
                 && reader.Peek(1).Type = TokenType.LParen then
                this.ParseFunction(start)

            elif token.Type = TokenType.Operator
                 && token.Value = "*" then

                reader.Advance() |> ignore
                let span = CoreTokenReader.Span(token)

                ColumnExpr(
                    SqlIdentifier(
                        ImmutableArray.Create(
                            IdentifierPart(
                                "*",
                                false,
                                span)),
                        span),
                    reader.SpanFrom(start))
                :> SqlExpr

            elif token.Type = TokenType.Identifier then
                let identifier =
                    reader.ParseIdentifierPath(
                        "column identifier",
                        true)

                ColumnExpr(
                    identifier,
                    reader.SpanFrom(start))
                :> SqlExpr

            else
                raise (CoreTokenReader.Error(
                    $"Unexpected token '{token.Value}' in SQL expression.",
                    token))

        member private this.ParseExtract(start: int) : SqlExpr =
            reader.ExpectWord("EXTRACT") |> ignore
            reader.Expect(
                TokenType.LParen,
                "'(' after EXTRACT")
            |> ignore

            let partToken = reader.Peek()

            if partToken.Type <> TokenType.Identifier
               && partToken.Type <> TokenType.Keyword then
                raise (CoreTokenReader.Error(
                    "EXTRACT requires a date-part keyword.",
                    partToken))

            let part =
                reader.Advance().Value.ToUpperInvariant()

            reader.ExpectWord("FROM") |> ignore
            let value = this.ParseExpression()

            reader.Expect(
                TokenType.RParen,
                "')' after EXTRACT expression")
            |> ignore

            if not (
                SqlDatePartCapabilityRules.IsRepresentedPart(
                    part)) then
                raise (CoreTokenReader.Error(
                    $"EXTRACT({part} ...) is not yet represented by the canonical date-part family.",
                    partToken))

            FunctionCallExpr(
                this.Identifier(
                    part,
                    CoreTokenReader.Span(partToken),
                    false),
                ImmutableArray.Create(value),
                false,
                reader.SpanFrom(start))
            :> SqlExpr

        member private this.ParseOrderByItems() =
            let items = ResizeArray<OrderByItem>()
            let mutable keepReading = true

            while keepReading do
                let orderStart = reader.Position
                let expression = this.ParseExpression()

                let descending =
                    if reader.MatchWord("DESC") then
                        true
                    else
                        reader.MatchWord("ASC") |> ignore
                        false

                let nullOrdering =
                    if reader.MatchWord("NULLS") then
                        if reader.MatchWord("FIRST") then
                            NullOrderingKind.First
                        elif reader.MatchWord("LAST") then
                            NullOrderingKind.Last
                        else
                            raise (CoreTokenReader.Error(
                                "Expected FIRST or LAST after NULLS.",
                                reader.Peek()))
                    else
                        NullOrderingKind.Default

                items.Add(
                    OrderByItem(
                        expression,
                        descending,
                        nullOrdering,
                        reader.SpanFrom(orderStart)))

                keepReading <-
                    reader.Match(TokenType.Comma)

            items |> toImmutableArray

        member private this.ParseFunction(start: int) : SqlExpr =
            let nameToken = reader.Advance()
            let name = this.IdentifierFromToken(nameToken)

            reader.Expect(
                TokenType.LParen,
                "'(' after function name")
            |> ignore

            let distinct = reader.MatchWord("DISTINCT")

            if not distinct then
                reader.MatchWord("ALL") |> ignore

            let arguments = ResizeArray<SqlExpr>()

            if reader.Peek().Type = TokenType.Operator
               && reader.Peek().Value = "*" then

                let star = reader.Advance()
                let span = CoreTokenReader.Span(star)

                arguments.Add(
                    ColumnExpr(
                        SqlIdentifier(
                            ImmutableArray.Create(
                                IdentifierPart(
                                    "*",
                                    false,
                                    span)),
                            span),
                        span))
            elif reader.Peek().Type <> TokenType.RParen then
                let mutable keepReading = true

                while keepReading do
                    arguments.Add(this.ParseExpression())
                    keepReading <-
                        reader.Match(TokenType.Comma)

            let mutable aggregateOrderBy =
                ImmutableArray<OrderByItem>.Empty

            let mutable aggregateOrderSyntax =
                AggregateOrderSyntaxKind.None

            let mutable aggregateSeparatorClause =
                None

            if reader.MatchWord("ORDER") then
                reader.ExpectWord("BY") |> ignore
                aggregateOrderBy <-
                    this.ParseOrderByItems()

                aggregateOrderSyntax <-
                    AggregateOrderSyntaxKind.Inline

            if reader.MatchWord("SEPARATOR") then
                let separatorToken =
                    reader.Expect(
                        TokenType.String,
                        "string literal after aggregate SEPARATOR")

                aggregateSeparatorClause <-
                    Some(
                        this.DecodeString(
                            separatorToken.Value))

            reader.Expect(
                TokenType.RParen,
                "')' after function arguments")
            |> ignore

            if reader.MatchWord("WITHIN") then
                if not aggregateOrderBy.IsDefaultOrEmpty then
                    raise (CoreTokenReader.Error(
                        "Aggregate ordering cannot combine inline ORDER BY with WITHIN GROUP.",
                        reader.Peek(-1)))

                reader.ExpectWord("GROUP") |> ignore
                reader.Expect(
                    TokenType.LParen,
                    "'(' after WITHIN GROUP")
                |> ignore
                reader.ExpectWord("ORDER") |> ignore
                reader.ExpectWord("BY") |> ignore

                aggregateOrderBy <-
                    this.ParseOrderByItems()

                aggregateOrderSyntax <-
                    AggregateOrderSyntaxKind.WithinGroup

                reader.Expect(
                    TokenType.RParen,
                    "')' after WITHIN GROUP ordering")
                |> ignore

            let mutable result : SqlExpr =
                CoreParserAstClone.Function(
                    name,
                    arguments |> toImmutableArray,
                    distinct,
                    reader.SpanFrom(start),
                    aggregateOrderBy,
                    aggregateOrderSyntax,
                    Option.toObj aggregateSeparatorClause)
                :> SqlExpr

            if reader.MatchWord("FILTER") then
                reader.Expect(
                    TokenType.LParen,
                    "'(' after FILTER")
                |> ignore

                reader.ExpectWord("WHERE") |> ignore
                let predicate = this.ParseExpression()

                reader.Expect(
                    TokenType.RParen,
                    "')' after FILTER predicate")
                |> ignore

                result <-
                    FilterExpr(
                        result,
                        predicate,
                        reader.SpanFrom(start))

            if reader.MatchWord("OVER") then
                result <-
                    WindowedExpr(
                        result,
                        this.ParseWindowSpec(),
                        reader.SpanFrom(start))

            CoreParserAstClone.WithSpan(
                result,
                reader.SpanFrom(start))

        member private this.ParseWindowSpec() =
            let start = reader.Position

            reader.Expect(
                TokenType.LParen,
                "'(' after OVER")
            |> ignore

            let partitionBy =
                if reader.MatchWord("PARTITION") then
                    reader.ExpectWord("BY") |> ignore
                    let parts = ResizeArray<SqlExpr>()
                    let mutable keepReading = true

                    while keepReading do
                        parts.Add(this.ParseExpression())
                        keepReading <-
                            reader.Match(TokenType.Comma)

                    parts |> toImmutableArray
                else
                    ImmutableArray<SqlExpr>.Empty

            let orderBy =
                if reader.MatchWord("ORDER") then
                    reader.ExpectWord("BY") |> ignore
                    this.ParseOrderByItems()
                else
                    ImmutableArray<OrderByItem>.Empty

            let frame =
                if reader.PeekWord("ROWS")
                   || reader.PeekWord("RANGE") then
                    Some(this.ParseWindowFrame())
                else
                    None

            reader.Expect(
                TokenType.RParen,
                "')' after window specification")
            |> ignore

            WindowSpec(
                partitionBy,
                orderBy,
                Option.toObj frame,
                reader.SpanFrom(start))

        member private this.ParseWindowFrame() =
            let start = reader.Position
            let unitToken = reader.Advance()

            let unitKind =
                if CoreTokenReader.IsWord(
                    unitToken,
                    "ROWS") then
                    WindowFrameUnitKind.Rows
                else
                    WindowFrameUnitKind.Range

            let first, second =
                if reader.MatchWord("BETWEEN") then
                    let first =
                        this.ParseWindowBound()

                    reader.ExpectWord("AND") |> ignore

                    first,
                    Some(this.ParseWindowBound())
                else
                    this.ParseWindowBound(),
                    None

            WindowFrame(
                unitKind,
                first,
                Option.toObj second,
                reader.SpanFrom(start))

        member private this.ParseWindowBound() =
            let start = reader.Position

            if reader.MatchWord("UNBOUNDED") then
                if reader.MatchWord("PRECEDING") then
                    WindowFrameBoundCore(
                        WindowFrameBoundKindCore.UnboundedPreceding,
                        Nullable<int>(),
                        reader.SpanFrom(start))
                else
                    reader.ExpectWord("FOLLOWING")
                    |> ignore

                    WindowFrameBoundCore(
                        WindowFrameBoundKindCore.UnboundedFollowing,
                        Nullable<int>(),
                        reader.SpanFrom(start))

            elif reader.MatchWord("CURRENT") then
                reader.ExpectWord("ROW") |> ignore

                WindowFrameBoundCore(
                    WindowFrameBoundKindCore.CurrentRow,
                    Nullable<int>(),
                    reader.SpanFrom(start))

            else
                let token =
                    reader.Expect(
                        TokenType.Number,
                        "window frame offset")

                let mutable offset = 0

                if not (
                    Int32.TryParse(
                        token.Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        &offset))
                   || offset < 0 then
                    raise (CoreTokenReader.Error(
                        "Window frame offset must be a non-negative integer.",
                        token))

                if reader.MatchWord("PRECEDING") then
                    WindowFrameBoundCore(
                        WindowFrameBoundKindCore.Preceding,
                        Nullable<int>(offset),
                        reader.SpanFrom(start))
                else
                    reader.ExpectWord("FOLLOWING")
                    |> ignore

                    WindowFrameBoundCore(
                        WindowFrameBoundKindCore.Following,
                        Nullable<int>(offset),
                        reader.SpanFrom(start))

        member private this.ParseCase(start: int) : SqlExpr =
            let caseValue =
                if reader.PeekWord("WHEN") then
                    None
                else
                    Some(this.ParseExpression())

            let branches = ResizeArray<CaseBranch>()

            while reader.MatchWord("WHEN") do
                let whenExpression =
                    this.ParseExpression()

                let condition =
                    match caseValue with
                    | None ->
                        whenExpression
                    | Some value ->
                        BinaryExpr(
                            value,
                            "=",
                            whenExpression,
                            SourceSpan(
                                value.Span.Start,
                                whenExpression.Span.End))
                        :> SqlExpr

                reader.ExpectWord("THEN") |> ignore

                branches.Add(
                    CaseBranch(
                        condition,
                        this.ParseExpression()))

            if branches.Count = 0 then
                raise (CoreTokenReader.Error(
                    "CASE requires at least one WHEN branch.",
                    reader.Peek()))

            let otherwise =
                if reader.MatchWord("ELSE") then
                    Some(this.ParseExpression())
                else
                    None

            reader.ExpectWord("END") |> ignore

            match caseValue with
            | None ->
                CaseExpr(
                    branches |> toImmutableArray,
                    Option.toObj otherwise,
                    reader.SpanFrom(start))
                :> SqlExpr

            | Some _ ->
                SimpleCaseExpr(
                    branches |> toImmutableArray,
                    Option.toObj otherwise,
                    reader.SpanFrom(start))
                :> SqlExpr

        member private this.ParseCast(start: int) : SqlExpr =
            reader.Expect(
                TokenType.LParen,
                "'(' after CAST")
            |> ignore

            let expression =
                this.ParseExpression()

            reader.ExpectWord("AS") |> ignore

            let typeName =
                this.ParseCastTypeName()

            reader.Expect(
                TokenType.RParen,
                "')' after CAST")
            |> ignore

            CastExpr(
                expression,
                typeName,
                reader.SpanFrom(start))
            :> SqlExpr

        member private this.ParseCastTypeName() =
            let parts = ResizeArray<string>()
            let token = reader.Peek()

            if token.Type <> TokenType.Identifier
               && token.Type <> TokenType.Keyword then
                raise (CoreTokenReader.Error(
                    "Expected cast type.",
                    token))

            parts.Add(reader.Advance().Value)

            while reader.Match(TokenType.Dot) do
                let typeComponent = reader.Peek()

                if typeComponent.Type <> TokenType.Identifier
                   && typeComponent.Type <> TokenType.Keyword then
                    raise (CoreTokenReader.Error(
                        "Expected cast type component.",
                        typeComponent))

                let lastIndex = parts.Count - 1
                parts[lastIndex] <-
                    parts[lastIndex]
                    + "."
                    + reader.Advance().Value

            while (reader.Peek().Type = TokenType.Identifier
                   || reader.Peek().Type = TokenType.Keyword)
                  && this.IsCastTypeQualifier(
                      reader.Peek().Value) do
                parts.Add(reader.Advance().Value)

            if reader.Match(TokenType.LParen) then
                let suffix = StringBuilder("(")
                let first = reader.Peek()

                let isMax =
                    (first.Type = TokenType.Identifier
                     || first.Type = TokenType.Keyword)
                    && first.Value.Equals(
                        "MAX",
                        StringComparison.OrdinalIgnoreCase)

                if isMax then
                    reader.Advance() |> ignore
                    suffix.Append("MAX") |> ignore
                else
                    let precision =
                        reader.Expect(
                            TokenType.Number,
                            "cast type precision or MAX")

                    let mutable parsedPrecision = 0

                    if not (
                        Int32.TryParse(
                            precision.Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            &parsedPrecision)) then
                        raise (CoreTokenReader.Error(
                            "Cast type precision must be an integer or MAX.",
                            precision))

                    suffix.Append(precision.Value)
                    |> ignore

                if reader.Match(TokenType.Comma) then
                    if isMax then
                        raise (CoreTokenReader.Error(
                            "Cast type MAX does not accept a scale.",
                            reader.Peek(-1)))

                    let scale =
                        reader.Expect(
                            TokenType.Number,
                            "cast type scale")

                    let mutable parsedScale = 0

                    if not (
                        Int32.TryParse(
                            scale.Value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            &parsedScale)) then
                        raise (CoreTokenReader.Error(
                            "Cast type scale must be an integer.",
                            scale))

                    suffix
                        .Append(',')
                        .Append(scale.Value)
                    |> ignore

                reader.Expect(
                    TokenType.RParen,
                    "')' after cast type precision")
                |> ignore

                suffix.Append(')') |> ignore

                let lastIndex = parts.Count - 1
                parts[lastIndex] <-
                    parts[lastIndex] + suffix.ToString()

            while (reader.Peek().Type = TokenType.Identifier
                   || reader.Peek().Type = TokenType.Keyword)
                  && this.IsCastTypeQualifier(
                      reader.Peek().Value) do
                parts.Add(reader.Advance().Value)

            String.Join(" ", parts)

        member private this.ParseTemporalLiteral(start: int) : SqlExpr =
            let typeToken = reader.Advance()
            let temporalType =
                typeToken.Value.ToUpperInvariant()

            let withTimeZone =
                if (temporalType = "TIME"
                    || temporalType = "TIMESTAMP")
                   && (reader.PeekWord("WITH")
                       || reader.PeekWord("WITHOUT")) then

                    let value =
                        if reader.MatchWord("WITH") then
                            true
                        else
                            reader.ExpectWord("WITHOUT")
                            |> ignore
                            false

                    reader.ExpectWord("TIME") |> ignore
                    reader.ExpectWord("ZONE") |> ignore
                    Some value
                else
                    None

            let literalToken =
                reader.Expect(
                    TokenType.String,
                    $"quoted {temporalType} literal")

            let literal =
                this.DecodeString(
                    literalToken.Value)

            if temporalType = "DATE" then
                let success, date =
                    SqlTemporalLiteralParser.TryParseDate(
                        literal)

                if success then
                    LiteralExpr(
                        date,
                        reader.SpanFrom(start))
                    :> SqlExpr
                else
                    raise (CoreTokenReader.Error(
                        $"Invalid {temporalType} literal '{literal}'.",
                        literalToken))

            elif temporalType = "TIME" then
                let success, time =
                    SqlTemporalLiteralParser.TryParseTime(
                        literal)

                if success then
                    if withTimeZone = Some true then
                        raise (CoreTokenReader.Error(
                            "TIME WITH TIME ZONE is not represented by the canonical temporal model.",
                            typeToken))

                    LiteralExpr(
                        time,
                        reader.SpanFrom(start))
                    :> SqlExpr
                else
                    raise (CoreTokenReader.Error(
                        $"Invalid {temporalType} literal '{literal}'.",
                        literalToken))

            elif temporalType = "TIMESTAMP" then
                let success, timestamp =
                    SqlTemporalLiteralParser.TryParseTimestamp(
                        literal)

                if success then
                    if withTimeZone = Some true
                       && not (timestamp :? SqlOffsetDateTimeValue) then
                        raise (CoreTokenReader.Error(
                            "TIMESTAMP WITH TIME ZONE requires an explicit UTC offset or Z suffix.",
                            literalToken))

                    if withTimeZone = Some false
                       && (timestamp :? SqlOffsetDateTimeValue) then
                        raise (CoreTokenReader.Error(
                            "TIMESTAMP WITHOUT TIME ZONE must not include a UTC offset.",
                            literalToken))

                    LiteralExpr(
                        timestamp,
                        reader.SpanFrom(start))
                    :> SqlExpr
                else
                    raise (CoreTokenReader.Error(
                        $"Invalid {temporalType} literal '{literal}'.",
                        literalToken))

            else
                raise (CoreTokenReader.Error(
                    $"Invalid {temporalType} literal '{literal}'.",
                    literalToken))

        member this.ParseExpression() : SqlExpr =
            this.ParseOr()
