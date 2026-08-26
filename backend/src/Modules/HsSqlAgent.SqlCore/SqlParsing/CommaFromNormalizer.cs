namespace HsSqlAgent.SqlCore.SqlParsing;

/// <summary>
/// Normalizes SQL's legacy comma-separated FROM syntax into explicit CROSS JOIN tokens before
/// the DTO parser consumes the stream. State is tracked per parenthesis depth so commas in select
/// lists, function arguments, CTE bodies, and nested expressions are not rewritten accidentally.
/// </summary>
internal static class CommaFromNormalizer
{
    public static Token[] Normalize(Token[] tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var result = new List<Token>(tokens.Length);
        var fromByDepth = new Dictionary<int, bool>();
        var depth = 0;

        foreach (var token in tokens)
        {
            if (token.Type == TokenType.LParen)
            {
                result.Add(token);
                depth++;
                continue;
            }

            if (token.Type == TokenType.RParen)
            {
                fromByDepth.Remove(depth);
                depth = Math.Max(0, depth - 1);
                result.Add(token);
                continue;
            }

            if (IsWord(token, "FROM"))
            {
                fromByDepth[depth] = true;
                result.Add(token);
                continue;
            }

            if (IsClauseBoundary(token))
                fromByDepth[depth] = false;

            if (token.Type == TokenType.Comma
                && fromByDepth.TryGetValue(depth, out var inFrom)
                && inFrom)
            {
                result.Add(new Token(TokenType.Keyword, "CROSS", token.Pos, token.Length));
                result.Add(new Token(TokenType.Keyword, "JOIN", token.Pos, token.Length));
                continue;
            }

            result.Add(token);
        }

        return [.. result];
    }

    private static bool IsClauseBoundary(Token token) =>
        token.Type == TokenType.Semicolon || token.Type == TokenType.EOF
        || IsWord(token, "WHERE") || IsWord(token, "GROUP") || IsWord(token, "HAVING")
        || IsWord(token, "ORDER") || IsWord(token, "LIMIT") || IsWord(token, "OFFSET")
        || IsWord(token, "UNION") || IsWord(token, "INTERSECT") || IsWord(token, "EXCEPT");

    private static bool IsWord(Token token, string value) =>
        (token.Type is TokenType.Keyword or TokenType.Identifier)
        && token.Value.Equals(value, StringComparison.OrdinalIgnoreCase);
}
