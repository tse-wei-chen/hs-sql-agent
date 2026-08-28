namespace HsSqlAgent.SqlCore.Internal

open System
open System.Collections.Immutable
open HsSqlAgent.SqlCore.Core.Ast
open HsSqlAgent.SqlCore.SqlParsing

/// Token cursor and span/identifier helpers implemented in F#.
///
/// Token values remain the existing CLR DTOs during the migration, while all
/// parser cursor state, expectations, and identifier-path parsing move out of
/// CoreTokenReader.
type internal FunctionalTokenReader(tokens: Token array) =

    let mutable position = 0

    member _.Position = position

    member _.Peek() =
        let index = position

        if index < tokens.Length then
            tokens[index]
        else
            tokens[tokens.Length - 1]

    member _.Peek(offset: int) =
        let rawIndex = position + offset
        let index = max 0 rawIndex

        if index < tokens.Length then
            tokens[index]
        else
            tokens[tokens.Length - 1]

    member this.Advance() =
        let token = this.Peek()

        if position < tokens.Length then
            position <- position + 1

        token

    member this.PeekWord(value: string) =
        FunctionalTokenReader.IsWord(
            this.Peek(),
            value)

    member this.PeekWord(offset: int, value: string) =
        FunctionalTokenReader.IsWord(
            this.Peek(offset),
            value)

    member this.MatchWord(value: string) =
        if this.PeekWord(value) then
            this.Advance() |> ignore
            true
        else
            false

    member this.ExpectWord(value: string) =
        let token = this.Peek()

        if not (
            FunctionalTokenReader.IsWord(
                token,
                value)) then
            raise (
                FunctionalTokenReader.Error(
                    $"Expected keyword '{value}' but got '{token.Value}'.",
                    token))

        this.Advance()

    member this.Match(tokenType: TokenType) =
        if this.Peek().Type = tokenType then
            this.Advance() |> ignore
            true
        else
            false

    member this.Expect(
        tokenType: TokenType,
        description: string) =

        let token = this.Peek()

        if token.Type <> tokenType then
            raise (
                FunctionalTokenReader.Error(
                    $"Expected {description} but got {token.Type} ('{token.Value}').",
                    token))

        this.Advance()

    member this.Expect(tokenType: TokenType) =
        this.Expect(
            tokenType,
            tokenType.ToString())

    member this.ExpectIdentifier(description: string) =
        let token = this.Peek()

        if token.Type <> TokenType.Identifier then
            raise (
                FunctionalTokenReader.Error(
                    $"Expected {description} but got {token.Type} ('{token.Value}').",
                    token))

        this.Advance()

    member _.SpanFrom(startPosition: int) =
        if startPosition < 0
           || startPosition >= tokens.Length then
            SourceSpan.Unknown
        else
            let first = tokens[startPosition]

            let lastIndex =
                Math.Clamp(
                    position - 1,
                    startPosition,
                    tokens.Length - 1)

            let last = tokens[lastIndex]

            SourceSpan(
                first.Pos,
                Math.Max(
                    first.End,
                    last.End))

    member this.ParseIdentifierPath(
        description: string) =

        this.ParseIdentifierPath(
            description,
            false)

    member this.ParseIdentifierPath(
        description: string,
        allowStarTail: bool) =

        let start = this.Position
        let parts = ResizeArray<IdentifierPart>()

        let first =
            this.ExpectIdentifier(description)

        parts.Add(
            FunctionalTokenReader.ToIdentifierPart(
                first))

        let mutable keepReading = true

        while keepReading
              && this.Match(TokenType.Dot) do

            if allowStarTail
               && this.Peek().Type = TokenType.Operator
               && this.Peek().Value = "*" then

                let star = this.Advance()

                parts.Add(
                    IdentifierPart(
                        "*",
                        false,
                        FunctionalTokenReader.Span(star)))

                keepReading <- false
            else
                parts.Add(
                    FunctionalTokenReader.ToIdentifierPart(
                        this.ExpectIdentifier(description)))

        SqlIdentifier(
            ImmutableArray.CreateRange(parts),
            this.SpanFrom(start))

    static member IsWord(
        token: Token,
        value: string) =

        (token.Type = TokenType.Keyword
         || (token.Type = TokenType.Identifier
             && not (
                 FunctionalTokenReader.IsQuotedIdentifier(
                     token))))
        && token.Value.Equals(
            value,
            StringComparison.OrdinalIgnoreCase)

    static member Span(token: Token) =
        SourceSpan(
            token.Pos,
            token.End)

    static member IsQuotedIdentifier(token: Token) =
        token.Type = TokenType.Identifier
        && token.Length > token.Value.Length

    static member ToIdentifierPart(token: Token) =
        IdentifierPart(
            token.Value,
            FunctionalTokenReader.IsQuotedIdentifier(
                token),
            FunctionalTokenReader.Span(token))

    static member Error(
        message: string,
        token: Token) =

        SqlParseException(
            $"{message} Position {token.Pos}, span [{token.Pos}..{Math.Max(token.End, token.Pos + 1)}).")
