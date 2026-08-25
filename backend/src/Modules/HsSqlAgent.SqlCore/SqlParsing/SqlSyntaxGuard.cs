namespace SqlAgent.Service.SqlParsing;

/// <summary>
/// Fail-closed checks for syntax shapes that the legacy DTO parser cannot preserve safely yet.
/// Keep these checks close to tokenization until the parser is replaced by the Core AST parser.
/// </summary>
internal static class SqlSyntaxGuard
{
    public static void ValidateQuery(Token[] tokens)
    {
        RejectCteColumnAliases(tokens);
        RejectCommaSeparatedFromSources(tokens);
        RejectNonLiteralInLists(tokens);
    }

    private static void RejectCteColumnAliases(Token[] tokens)
    {
        var i = 0;
        if (!IsWord(tokens, i, "WITH"))
            return;

        i++;
        if (IsWord(tokens, i, "RECURSIVE"))
            i++;

        // A CTE starts with: name AS (...). A '(' immediately after the CTE name is
        // the optional column-alias list, which the legacy parser currently discards.
        // Reject it rather than accepting SQL with different semantics.
        while (i < tokens.Length && tokens[i].Type != TokenType.EOF)
        {
            if (tokens[i].Type is not (TokenType.Identifier or TokenType.Keyword))
                return;
            i++;

            if (i < tokens.Length && tokens[i].Type == TokenType.LParen)
            {
                throw new SqlParseException(
                    $"CTE column alias lists are not supported at position {tokens[i].Pos}; " +
                    "the statement was rejected because those aliases cannot currently be preserved.");
            }

            if (!IsWord(tokens, i, "AS"))
                return;
            i++;
            if (i >= tokens.Length || tokens[i].Type != TokenType.LParen)
                return;

            i = SkipBalanced(tokens, i);
            if (i >= tokens.Length || tokens[i].Type != TokenType.Comma)
                return;
            i++;
        }
    }

    private static void RejectCommaSeparatedFromSources(Token[] tokens)
    {
        var depth = 0;
        var inFrom = false;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Type == TokenType.LParen)
            {
                depth++;
                continue;
            }
            if (token.Type == TokenType.RParen)
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }
            if (depth != 0)
                continue;

            if (IsWord(token, "FROM"))
            {
                inFrom = true;
                continue;
            }

            if (!inFrom)
                continue;

            if (IsClauseBoundary(token))
            {
                inFrom = false;
                continue;
            }

            if (token.Type == TokenType.Comma)
            {
                throw new SqlParseException(
                    $"Comma-separated FROM sources are not supported at position {token.Pos}; " +
                    "use an explicit CROSS JOIN instead.");
            }
        }
    }

    private static void RejectNonLiteralInLists(Token[] tokens)
    {
        for (var i = 0; i + 1 < tokens.Length; i++)
        {
            if (!IsWord(tokens, i, "IN") || tokens[i + 1].Type != TokenType.LParen)
                continue;

            var start = i + 2;
            if (IsWord(tokens, start, "SELECT") || IsWord(tokens, start, "WITH"))
                continue;

            var expectingValue = true;
            var valueCount = 0;
            for (var j = start; j < tokens.Length; j++)
            {
                var token = tokens[j];
                if (token.Type == TokenType.RParen)
                {
                    if (expectingValue)
                    {
                        var reason = valueCount == 0 ? "empty IN lists" : "a trailing comma in an IN list";
                        throw new SqlParseException(
                            $"Unsupported {reason} at position {token.Pos}; the statement was rejected to preserve semantics.");
                    }
                    break;
                }

                if (token.Type == TokenType.LParen)
                    throw UnsupportedInValue(token);

                if (token.Type == TokenType.Comma)
                {
                    if (expectingValue)
                        throw UnsupportedInValue(token);
                    expectingValue = true;
                    continue;
                }

                if (!expectingValue)
                    throw UnsupportedInValue(token);

                if (token.Type == TokenType.Operator && token.Value is "+" or "-")
                {
                    if (j + 1 >= tokens.Length || tokens[j + 1].Type != TokenType.Number)
                        throw UnsupportedInValue(token);
                    j++;
                    expectingValue = false;
                    valueCount++;
                    continue;
                }

                if (token.Type is TokenType.Number or TokenType.String
                    || IsWord(token, "NULL") || IsWord(token, "TRUE") || IsWord(token, "FALSE"))
                {
                    expectingValue = false;
                    valueCount++;
                    continue;
                }

                throw UnsupportedInValue(token);
            }
        }
    }

    private static SqlParseException UnsupportedInValue(Token token) =>
        new($"IN lists currently accept scalar literals only; unsupported expression '{token.Value}' " +
            $"at position {token.Pos}. The statement was rejected to preserve semantics.");

    private static int SkipBalanced(Token[] tokens, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < tokens.Length; i++)
        {
            if (tokens[i].Type == TokenType.LParen) depth++;
            else if (tokens[i].Type == TokenType.RParen && --depth == 0) return i + 1;
        }
        return tokens.Length;
    }

    private static bool IsClauseBoundary(Token token) =>
        token.Type == TokenType.Semicolon || token.Type == TokenType.EOF
        || IsWord(token, "WHERE") || IsWord(token, "GROUP") || IsWord(token, "HAVING")
        || IsWord(token, "ORDER") || IsWord(token, "LIMIT") || IsWord(token, "OFFSET")
        || IsWord(token, "UNION") || IsWord(token, "INTERSECT") || IsWord(token, "EXCEPT");

    private static bool IsWord(Token[] tokens, int index, string value) =>
        index >= 0 && index < tokens.Length && IsWord(tokens[index], value);

    private static bool IsWord(Token token, string value) =>
        (token.Type is TokenType.Keyword or TokenType.Identifier)
        && token.Value.Equals(value, StringComparison.OrdinalIgnoreCase);
}