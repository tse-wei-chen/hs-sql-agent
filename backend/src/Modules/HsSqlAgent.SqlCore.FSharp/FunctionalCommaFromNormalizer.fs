namespace HsSqlAgent.SqlCore.Internal

open HsSqlAgent.SqlCore.SqlParsing

/// Normalizes legacy comma-separated FROM sources into explicit CROSS JOIN
/// tokens using an immutable per-parenthesis-depth state map.
module internal FunctionalCommaFromNormalizer =

    let private isWord
        (token: Token)
        value =

        (token.Type = TokenType.Keyword
         || token.Type = TokenType.Identifier)
        && token.Value.Equals(
            value,
            System.StringComparison.OrdinalIgnoreCase)

    let private isClauseBoundary
        (token: Token) =

        token.Type = TokenType.Semicolon
        || token.Type = TokenType.EOF
        || isWord token "WHERE"
        || isWord token "GROUP"
        || isWord token "HAVING"
        || isWord token "ORDER"
        || isWord token "LIMIT"
        || isWord token "OFFSET"
        || isWord token "UNION"
        || isWord token "INTERSECT"
        || isWord token "EXCEPT"

    let normalize (tokens: Token array) =
        let folder
            (depth, fromByDepth, reversed)
            (token: Token) =

            if token.Type = TokenType.LParen then
                depth + 1,
                fromByDepth,
                token :: reversed

            elif token.Type = TokenType.RParen then
                let nextMap = fromByDepth |> Map.remove depth
                max 0 (depth - 1),
                nextMap,
                token :: reversed

            elif isWord token "FROM" then
                depth,
                fromByDepth |> Map.add depth true,
                token :: reversed

            else
                let nextMap =
                    if isClauseBoundary token then
                        fromByDepth |> Map.add depth false
                    else
                        fromByDepth

                let inFrom =
                    nextMap
                    |> Map.tryFind depth
                    |> Option.defaultValue false

                if token.Type = TokenType.Comma
                   && inFrom then
                    depth,
                    nextMap,
                    Token(
                        TokenType.Keyword,
                        "JOIN",
                        token.Pos,
                        System.Nullable<int>(token.Length))
                    :: Token(
                        TokenType.Keyword,
                        "CROSS",
                        token.Pos,
                        System.Nullable<int>(token.Length))
                    :: reversed
                else
                    depth,
                    nextMap,
                    token :: reversed

        let _, _, reversed =
            ((0, Map.empty<int, bool>, []), tokens)
            ||> Array.fold folder

        reversed
        |> List.rev
        |> List.toArray
