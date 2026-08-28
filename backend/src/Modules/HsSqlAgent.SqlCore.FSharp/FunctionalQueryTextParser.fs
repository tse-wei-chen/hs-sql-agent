namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open System.Globalization
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.Enums
open HsSqlAgent.SqlCore.Models
open HsSqlAgent.SqlCore.SqlParsing

/// Statement-level SELECT/query-set grammar implemented in F#.
///
/// Expression precedence/function/window parsing deliberately remains in the
/// existing CoreExpressionTextParser for this migration slice. The F# grammar
/// owns CTEs, SELECT/FROM/JOIN, set operations, statement ORDER BY, and
/// provider-specific row-limiting syntax.
module internal FunctionalQueryTextParser =

    let private toImmutableArray<'T> (items: seq<'T>) =
        ImmutableArray.CreateRange<'T>(items)

    let private nullableInt = function
        | Some value -> Nullable<int>(value)
        | None -> Nullable<int>()

    type private QueryGrammar(
        reader: CoreTokenReader,
        sourceDialect: SqlAgentToolType,
        requireExplicitLikeEscape: bool) as this =

        let expressions =
            lazy (
                FunctionalExpressionTextParser.ExpressionGrammar(
                    reader,
                    Func<SqlStatement>(fun () ->
                        this.ParseQueryExpression()),
                    requireExplicitLikeEscape))

        member private _.Expressions = expressions.Value

        member private _.IsFetchClauseStart() =
            reader.PeekWord("FETCH")
            && (reader.PeekWord(1, "FIRST")
                || reader.PeekWord(1, "NEXT"))

        member private this.CanConsumeImplicitAlias() =
            reader.Peek().Type = TokenType.Identifier
            && not (this.IsFetchClauseStart())

        member private this.ParseOptionalAlias() =
            if reader.MatchWord("AS") then
                Some(
                    CoreTokenReader.ToIdentifierPart(
                        reader.ExpectIdentifier("table alias")))
            elif this.CanConsumeImplicitAlias() then
                Some(
                    CoreTokenReader.ToIdentifierPart(
                        reader.Advance()))
            else
                None

        member private _.ParseSingleIdentifier(description: string) =
            let token = reader.ExpectIdentifier(description)
            SqlIdentifier(
                ImmutableArray.Create(
                    IdentifierPart(
                        token.Value,
                        CoreTokenReader.IsQuotedIdentifier(token),
                        CoreTokenReader.Span(token))),
                CoreTokenReader.Span(token))

        member private this.ParseExpressionList() =
            let values = ResizeArray<SqlExpr>()
            let mutable keepReading = true

            while keepReading do
                values.Add(this.Expressions.ParseExpression())
                keepReading <- reader.Match(TokenType.Comma)

            values |> toImmutableArray

        member private this.ParseSelectItems() =
            let items = ResizeArray<SelectItem>()
            let mutable keepReading = true

            while keepReading do
                let start = reader.Position
                let expression =
                    this.Expressions.ParseExpression()

                let alias =
                    if reader.MatchWord("AS") then
                        Some(
                            CoreTokenReader.ToIdentifierPart(
                                reader.ExpectIdentifier(
                                    "projection alias")))
                    elif this.CanConsumeImplicitAlias() then
                        Some(
                            CoreTokenReader.ToIdentifierPart(
                                reader.Advance()))
                    else
                        None

                items.Add(
                    SelectItem(
                        expression,
                        Option.toObj alias,
                        reader.SpanFrom(start)))

                keepReading <- reader.Match(TokenType.Comma)

            items |> toImmutableArray

        member private this.ParseTableSource(requireDerivedAlias: bool) =
            let start = reader.Position

            if reader.MatchWord("LATERAL") then
                raise (CoreTokenReader.Error(
                    "LATERAL sources are not represented by the Core AST and are rejected explicitly.",
                    reader.Peek(-1)))

            if reader.Match(TokenType.LParen) then
                let query = this.ParseQueryExpression()
                reader.Expect(
                    TokenType.RParen,
                    "')' after derived table query")
                |> ignore

                let alias = this.ParseOptionalAlias()
                if requireDerivedAlias && Option.isNone alias then
                    raise (CoreTokenReader.Error(
                        "A derived table requires an explicit alias.",
                        reader.Peek()))

                match alias with
                | Some aliasPart ->
                    DerivedTableSource(
                        query,
                        aliasPart,
                        reader.SpanFrom(start))
                    :> TableSource
                | None ->
                    // The only current caller requires aliases. Retain a
                    // fail-closed branch rather than fabricate one.
                    raise (CoreTokenReader.Error(
                        "A derived table requires an explicit alias.",
                        reader.Peek()))
            else
                let name =
                    reader.ParseIdentifierPath("table name")

                NamedTableSource(
                    name,
                    Option.toObj (this.ParseOptionalAlias()),
                    reader.SpanFrom(start))
                :> TableSource

        member private this.ParseJoin() =
            let start = reader.Position

            if reader.MatchWord("NATURAL") then
                raise (CoreTokenReader.Error(
                    "NATURAL JOIN is rejected because its schema-dependent implicit predicate is not represented in the Core AST.",
                    reader.Peek(-1)))

            let kind =
                if reader.MatchWord("LEFT") then
                    reader.MatchWord("OUTER") |> ignore
                    "LEFT"
                elif reader.MatchWord("RIGHT") then
                    reader.MatchWord("OUTER") |> ignore
                    "RIGHT"
                elif reader.MatchWord("FULL") then
                    reader.MatchWord("OUTER") |> ignore
                    "FULL"
                elif reader.MatchWord("CROSS") then
                    "CROSS"
                else
                    reader.MatchWord("INNER") |> ignore
                    "INNER"

            reader.ExpectWord("JOIN") |> ignore

            let source =
                this.ParseTableSource(true)

            let predicate =
                if kind = "CROSS" then
                    if reader.PeekWord("ON")
                       || reader.PeekWord("USING") then
                        raise (CoreTokenReader.Error(
                            "CROSS JOIN must not have ON/USING predicates.",
                            reader.Peek()))
                    None
                elif reader.MatchWord("ON") then
                    Some(this.Expressions.ParseExpression())
                elif reader.PeekWord("USING") then
                    raise (CoreTokenReader.Error(
                        "JOIN USING is rejected until using-column semantics are represented explicitly in the Core AST.",
                        reader.Peek()))
                else
                    raise (CoreTokenReader.Error(
                        $"{kind} JOIN requires an ON predicate.",
                        reader.Peek()))

            JoinSource(
                kind,
                source,
                Option.toObj predicate,
                reader.SpanFrom(start))

        member private _.IsJoinStart(token: Token) =
            CoreTokenReader.IsWord(token, "JOIN")
            || CoreTokenReader.IsWord(token, "LEFT")
            || CoreTokenReader.IsWord(token, "RIGHT")
            || CoreTokenReader.IsWord(token, "INNER")
            || CoreTokenReader.IsWord(token, "FULL")
            || CoreTokenReader.IsWord(token, "CROSS")
            || CoreTokenReader.IsWord(token, "NATURAL")

        member private _.IsSetOperation(token: Token) =
            CoreTokenReader.IsWord(token, "UNION")
            || CoreTokenReader.IsWord(token, "INTERSECT")
            || CoreTokenReader.IsWord(token, "EXCEPT")

        member private _.ParseSetOperationKind() =
            if reader.MatchWord("UNION") then
                if reader.MatchWord("ALL") then
                    SetOperationKind.UnionAll
                else
                    reader.MatchWord("DISTINCT") |> ignore
                    SetOperationKind.Union

            elif reader.MatchWord("INTERSECT") then
                if reader.MatchWord("ALL") then
                    raise (CoreTokenReader.Error(
                        "INTERSECT ALL is not represented by the Core set-operation model.",
                        reader.Peek(-1)))

                reader.MatchWord("DISTINCT") |> ignore
                SetOperationKind.Intersect

            elif reader.MatchWord("EXCEPT") then
                if reader.MatchWord("ALL") then
                    raise (CoreTokenReader.Error(
                        "EXCEPT ALL is not represented by the Core set-operation model.",
                        reader.Peek(-1)))

                reader.MatchWord("DISTINCT") |> ignore
                SetOperationKind.Except

            else
                raise (CoreTokenReader.Error(
                    "Expected set operation.",
                    reader.Peek()))

        member private _.ParseNonNegativeInt(description: string) =
            let token =
                reader.Expect(
                    TokenType.Number,
                    $"non-negative integer after {description}")

            let mutable value = 0
            if not (
                Int32.TryParse(
                    token.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    &value))
               || value < 0 then
                raise (CoreTokenReader.Error(
                    $"{description} requires a non-negative integer.",
                    token))

            value

        member private _.ParseOffsetRowKeyword() =
            let grammar =
                SqlSourceDialectGrammarRules.For(
                    sourceDialect)

            if grammar.OffsetRowKeywordOptional then
                if reader.MatchWord("ROW") then
                    ()
                else
                    reader.MatchWord("ROWS") |> ignore
            elif reader.MatchWord("ROW")
                 || reader.MatchWord("ROWS") then
                ()
            else
                raise (CoreTokenReader.Error(
                    $"{sourceDialect} OFFSET requires ROW or ROWS after the offset count in the modeled raw Core grammar.",
                    reader.Peek()))

        member private this.ParseStandardFetchCount(
            hasPrecedingOffset: bool) =

            let grammar =
                SqlSourceDialectGrammarRules.For(
                    sourceDialect)

            let fetchToken = reader.Advance()
            if not (CoreTokenReader.IsWord(fetchToken, "FETCH")) then
                raise (CoreTokenReader.Error(
                    "Expected FETCH.",
                    fetchToken))

            if grammar.FetchRequiresPrecedingOffset
               && not hasPrecedingOffset then
                raise (CoreTokenReader.Error(
                    "SQL Server FETCH requires a preceding OFFSET clause.",
                    fetchToken))

            if not grammar.SupportsFetch then
                raise (CoreTokenReader.Error(
                    $"FETCH FIRST/NEXT is not valid raw row-limiting syntax for {sourceDialect}.",
                    fetchToken))

            if not (
                reader.MatchWord("FIRST")
                || reader.MatchWord("NEXT")) then
                raise (CoreTokenReader.Error(
                    "FETCH requires FIRST or NEXT.",
                    reader.Peek()))

            let count =
                if reader.Peek().Type = TokenType.Number then
                    this.ParseNonNegativeInt("FETCH")
                elif not grammar.FetchCountOptional then
                    raise (CoreTokenReader.Error(
                        "SQL Server FETCH requires an explicit positive integer row count.",
                        reader.Peek()))
                else
                    1

            if grammar.FetchCountMustBePositive
               && count = 0 then
                raise (CoreTokenReader.Error(
                    "SQL Server FETCH row count must be greater than zero.",
                    reader.Peek(-1)))

            if reader.PeekWord("PERCENT") then
                raise (CoreTokenReader.Error(
                    "FETCH PERCENT is not represented by the canonical Core row-limit model.",
                    reader.Peek()))

            if not (
                reader.MatchWord("ROW")
                || reader.MatchWord("ROWS")) then
                raise (CoreTokenReader.Error(
                    "FETCH requires ROW or ROWS after the row count.",
                    reader.Peek()))

            if reader.PeekWord("WITH") then
                raise (CoreTokenReader.Error(
                    "FETCH WITH TIES is not represented by the canonical Core row-limit model.",
                    reader.Peek()))

            reader.ExpectWord("ONLY") |> ignore
            count

        member private this.ParseLimitOffsetIfPresent(
            hasOrderBy: bool) =

            let grammar =
                SqlSourceDialectGrammarRules.For(
                    sourceDialect)

            let mutable limit = None
            let mutable offset = None
            let mutable usedLimitClause = false
            let mutable usedCommaLimit = false

            if reader.MatchWord("LIMIT") then
                usedLimitClause <- true

                if reader.MatchWord("ALL") then
                    if not grammar.SupportsLimitAll then
                        raise (CoreTokenReader.Error(
                            $"LIMIT ALL raw source syntax is valid only for PostgreSQL, not {sourceDialect}.",
                            reader.Peek(-1)))
                else
                    let first =
                        this.ParseNonNegativeInt("LIMIT")

                    if reader.Peek().Type = TokenType.Comma then
                        let comma = reader.Advance()

                        if not grammar.SupportsCommaLimit then
                            raise (CoreTokenReader.Error(
                                $"LIMIT offset,row_count raw source syntax is valid only for MySQL and SQLite, not {sourceDialect}.",
                                comma))

                        usedCommaLimit <- true
                        offset <- Some first
                        limit <-
                            Some(
                                this.ParseNonNegativeInt(
                                    "LIMIT comma row count"))
                    else
                        limit <- Some first

            if reader.PeekWord("OFFSET") then
                let offsetToken = reader.Peek()

                if usedCommaLimit then
                    raise (CoreTokenReader.Error(
                        "LIMIT offset,row_count cannot be combined with a separate OFFSET clause.",
                        offsetToken))

                if grammar.OffsetRequiresLimit
                   && not usedLimitClause then
                    raise (CoreTokenReader.Error(
                        $"OFFSET without a preceding LIMIT is not valid raw source syntax for {sourceDialect}.",
                        offsetToken))

                if grammar.OffsetRequiresOrderBy
                   && not hasOrderBy then
                    raise (CoreTokenReader.Error(
                        "SQL Server OFFSET/FETCH raw source syntax requires a statement-level ORDER BY clause.",
                        offsetToken))

                reader.Advance() |> ignore
                offset <-
                    Some(this.ParseNonNegativeInt("OFFSET"))

                if grammar.UsesStandardOffsetFetch then
                    this.ParseOffsetRowKeyword()

                if reader.PeekWord("FETCH") then
                    if usedLimitClause then
                        raise (CoreTokenReader.Error(
                            "LIMIT and FETCH cannot be combined in the same raw query tail.",
                            reader.Peek()))

                    limit <-
                        Some(
                            this.ParseStandardFetchCount(true))

            elif reader.PeekWord("FETCH") then
                if usedLimitClause then
                    raise (CoreTokenReader.Error(
                        "LIMIT and FETCH cannot be combined in the same raw query tail.",
                        reader.Peek()))

                if grammar.FetchRequiresPrecedingOffset then
                    raise (CoreTokenReader.Error(
                        "SQL Server FETCH requires a preceding OFFSET clause inside ORDER BY.",
                        reader.Peek()))

                if not grammar.SupportsFetch then
                    raise (CoreTokenReader.Error(
                        $"FETCH FIRST/NEXT is not valid raw row-limiting syntax for {sourceDialect}; use LIMIT instead.",
                        reader.Peek()))

                limit <-
                    Some(
                        this.ParseStandardFetchCount(false))

            limit, offset

        member private this.ParseOrderByIfPresent() =
            if not (reader.MatchWord("ORDER")) then
                ImmutableArray<OrderByItem>.Empty
            else
                reader.ExpectWord("BY") |> ignore

                let items = ResizeArray<OrderByItem>()
                let mutable keepReading = true

                while keepReading do
                    let start = reader.Position
                    let firstToken = reader.Peek()
                    let expressionStart = reader.Position
                    let parsedExpression =
                        this.Expressions.ParseExpression()

                    let expressionEnd = reader.Position

                    let expression =
                        if firstToken.Type = TokenType.Number
                           && expressionEnd = expressionStart + 1
                           && firstToken.Value
                              |> Seq.forall (fun ch -> Char.IsDigit(ch)) then

                            let mutable ordinal = 0
                            if not (
                                Int32.TryParse(
                                    firstToken.Value,
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    &ordinal)) then
                                raise (CoreTokenReader.Error(
                                    "ORDER BY output position exceeds the supported integer range.",
                                    firstToken))

                            LiteralExpr(
                                OrderByOrdinalValue(ordinal),
                                parsedExpression.Span)
                            :> SqlExpr
                        else
                            parsedExpression

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
                            reader.SpanFrom(start)))

                    keepReading <-
                        reader.Match(TokenType.Comma)

                items |> toImmutableArray

        member private this.ParseCtesIfPresent() =
            if not (reader.MatchWord("WITH")) then
                ImmutableArray<CteDefinition>.Empty
            else
                if reader.MatchWord("RECURSIVE") then
                    raise (CoreTokenReader.Error(
                        "WITH RECURSIVE is not yet represented by the Core AST and is rejected rather than downgraded to non-recursive CTE semantics.",
                        reader.Peek(-1)))

                let ctes = ResizeArray<CteDefinition>()
                let mutable keepReading = true

                while keepReading do
                    let start = reader.Position
                    let name =
                        this.ParseSingleIdentifier("CTE name")

                    let aliases = ResizeArray<SqlIdentifier>()

                    if reader.Match(TokenType.LParen) then
                        if reader.Peek().Type = TokenType.RParen then
                            raise (CoreTokenReader.Error(
                                "CTE column alias list cannot be empty.",
                                reader.Peek()))

                        let mutable keepAliases = true
                        while keepAliases do
                            aliases.Add(
                                this.ParseSingleIdentifier(
                                    "CTE column alias"))

                            keepAliases <-
                                reader.Match(TokenType.Comma)

                        reader.Expect(
                            TokenType.RParen,
                            "')' after CTE column aliases")
                        |> ignore

                    reader.ExpectWord("AS") |> ignore
                    reader.Expect(
                        TokenType.LParen,
                        "'(' before CTE query")
                    |> ignore

                    let query =
                        this.ParseQueryExpression()

                    reader.Expect(
                        TokenType.RParen,
                        "')' after CTE query")
                    |> ignore

                    ctes.Add(
                        CteDefinition(
                            name,
                            aliases |> toImmutableArray,
                            query,
                            reader.SpanFrom(start)))

                    keepReading <-
                        reader.Match(TokenType.Comma)

                ctes |> toImmutableArray

        member private this.ParseSelectBody(
            ctes: ImmutableArray<CteDefinition>) =

            let start = reader.Position
            reader.ExpectWord("SELECT") |> ignore

            let distinct =
                if reader.MatchWord("DISTINCT") then
                    true
                else
                    reader.MatchWord("ALL") |> ignore
                    false

            let select = this.ParseSelectItems()

            let fromSource, joins =
                if reader.MatchWord("FROM") then
                    let source = this.ParseTableSource(true)
                    let joins = ResizeArray<JoinSource>()

                    while this.IsJoinStart(reader.Peek()) do
                        joins.Add(this.ParseJoin())

                    Some source, joins |> toImmutableArray
                else
                    None, ImmutableArray<JoinSource>.Empty

            let where =
                if reader.MatchWord("WHERE") then
                    Some(this.Expressions.ParseExpression())
                else
                    None

            let groupBy =
                if reader.MatchWord("GROUP") then
                    reader.ExpectWord("BY") |> ignore
                    this.ParseExpressionList()
                else
                    ImmutableArray<SqlExpr>.Empty

            let having =
                if reader.MatchWord("HAVING") then
                    Some(this.Expressions.ParseExpression())
                else
                    None

            SelectStatement(
                ctes,
                distinct,
                select,
                Option.toObj fromSource,
                joins,
                Option.toObj where,
                groupBy,
                Option.toObj having,
                ImmutableArray<OrderByItem>.Empty,
                Nullable<int>(),
                Nullable<int>(),
                reader.SpanFrom(start))

        member this.ParseQueryExpression() =
            this.ParseQueryExpression(None: int option)

        member private this.ParseQueryExpression(
            topLimit: int option) =

            let start = reader.Position

            let ctes =
                this.ParseCtesIfPresent()

            let head =
                this.ParseSelectBody(ctes)

            let operations =
                ResizeArray<SetOperation>()

            while this.IsSetOperation(reader.Peek()) do
                let operationStart = reader.Position
                let kind = this.ParseSetOperationKind()

                let branch =
                    if reader.Match(TokenType.LParen) then
                        let query =
                            this.ParseQueryExpression()

                        reader.Expect(
                            TokenType.RParen,
                            "')' after set-operation branch")
                        |> ignore

                        query
                    else
                        let branchCtes =
                            this.ParseCtesIfPresent()

                        this.ParseSelectBody(branchCtes)
                        :> SqlStatement

                operations.Add(
                    SetOperation(
                        kind,
                        branch,
                        reader.SpanFrom(operationStart)))

            let orderBy =
                this.ParseOrderByIfPresent()

            let limit, offset =
                this.ParseLimitOffsetIfPresent(
                    orderBy.Length > 0)

            let limit, offset =
                match topLimit with
                | None ->
                    limit, offset

                | Some top ->
                    if operations.Count > 0 then
                        raise (SqlParseException(
                            "SQL Server TOP with set operations is not represented losslessly by the Core AST."))

                    if Option.isSome limit
                       || Option.isSome offset then
                        raise (SqlParseException(
                            "SQL Server TOP cannot be combined with OFFSET/FETCH in the same canonical query scope."))

                    Some top, None

            if operations.Count = 0 then
                CoreParserAstClone.CompleteSelect(
                    head,
                    orderBy,
                    nullableInt limit,
                    nullableInt offset,
                    reader.SpanFrom(start))
                :> SqlStatement
            else
                QueryStatement(
                    head,
                    operations |> toImmutableArray,
                    orderBy,
                    nullableInt limit,
                    nullableInt offset,
                    reader.SpanFrom(start))
                :> SqlStatement

        member this.ParseComplete(topLimit: int option) =
            let statement =
                this.ParseQueryExpression(topLimit)

            reader.Match(TokenType.Semicolon)
            |> ignore

            if reader.Peek().Type <> TokenType.EOF then
                let token = reader.Peek()
                raise (CoreTokenReader.Error(
                    $"Unexpected token '{token.Value}'; the complete query statement was not consumed.",
                    token))

            statement

    let parseComplete
        (reader: CoreTokenReader)
        sourceDialect
        requireExplicitLikeEscape
        topLimit =

        QueryGrammar(
            reader,
            sourceDialect,
            requireExplicitLikeEscape)
            .ParseComplete(topLimit)
