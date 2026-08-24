using System.Globalization;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.SqlParsing;

/// <summary>
/// Parser-native entry point for raw SQL. It produces the independent Core AST directly, preserving
/// token source spans and quoted-identifier intent instead of using public transport DTOs as a parser IR.
/// </summary>
public static class CoreSqlTextParser
{
    public static ParsedStatement ParseQuery(string sql, SqlAgentToolType sourceDialect)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = new SqlTokenizer(sql, sourceDialect).Tokenize();
        ValidateStatementTokens(tokens, sourceDialect);
        var topLimit = NormalizeSqlServerTop(tokens, sourceDialect, out var normalizedTokens);
        normalizedTokens = CommaFromNormalizer.Normalize(normalizedTokens);
        var statement = new CoreQueryTextParser(new CoreTokenReader(normalizedTokens)).ParseComplete(topLimit);
        return new ParsedStatement(statement, sourceDialect, EnforceSourceDialectSyntax: true);
    }

    public static ParsedStatement ParseDml(string sql, SqlAgentToolType sourceDialect)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = new SqlTokenizer(sql, sourceDialect).Tokenize();
        ValidateStatementTokens(tokens, sourceDialect);
        var statement = new CoreDmlTextParser(new CoreTokenReader(tokens)).ParseComplete();
        return new ParsedStatement(statement, sourceDialect, EnforceSourceDialectSyntax: true);
    }

    private static void ValidateStatementTokens(Token[] tokens, SqlAgentToolType sourceDialect)
    {
        var content = tokens.Where(token => token.Type != TokenType.EOF).ToArray();
        for (var i = 0; i < content.Length; i++)
        {
            var token = content[i];
            if (token.Type == TokenType.Parameter)
            {
                throw new SqlParseException(
                    $"Unbound SQL parameter '{token.Value}' at position {token.Pos}. " +
                    "Runtime SQL parameters are not accepted; use a declared Custom Tool parameter.");
            }
            if (token.Type == TokenType.Operator
                && token.Value == "::"
                && sourceDialect != SqlAgentToolType.Postgres)
            {
                throw new SqlParseException(
                    $"PostgreSQL '::' cast syntax is not valid for source dialect {sourceDialect} at position {token.Pos}; " +
                    "use CAST(expression AS type) for portable raw SQL.");
            }
            if (token.Type == TokenType.Keyword
                && token.Value.Equals("LIMIT", StringComparison.OrdinalIgnoreCase)
                && sourceDialect is not (SqlAgentToolType.Postgres or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite))
            {
                throw new SqlParseException(
                    $"LIMIT is not valid raw source syntax for dialect {sourceDialect} at position {token.Pos}; " +
                    "use the source provider's native row-limiting form or a structured Core row limit.");
            }
            if (token.Type == TokenType.Semicolon && i != content.Length - 1)
            {
                throw new SqlParseException(
                    $"Only one SQL statement is allowed; unexpected semicolon at position {token.Pos}.");
            }
        }
    }

    private static int? NormalizeSqlServerTop(
        Token[] tokens,
        SqlAgentToolType provider,
        out Token[] normalizedTokens)
    {
        normalizedTokens = tokens;
        if (provider != SqlAgentToolType.MsSqlServer)
            return null;

        var depth = 0;
        var selectIndex = -1;
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
            if (depth == 0 && CoreTokenReader.IsWord(token, "SELECT"))
            {
                selectIndex = i;
                break;
            }
        }

        if (selectIndex < 0)
            return null;

        var cursor = selectIndex + 1;
        if (cursor < tokens.Length
            && (CoreTokenReader.IsWord(tokens[cursor], "DISTINCT")
                || CoreTokenReader.IsWord(tokens[cursor], "ALL")))
            cursor++;

        if (cursor >= tokens.Length || !CoreTokenReader.IsWord(tokens[cursor], "TOP"))
            return null;

        var topStart = cursor++;
        var parenthesized = cursor < tokens.Length && tokens[cursor].Type == TokenType.LParen;
        if (parenthesized) cursor++;
        if (cursor >= tokens.Length || tokens[cursor].Type != TokenType.Number
            || !int.TryParse(tokens[cursor].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit < 0)
        {
            throw new SqlParseException(
                $"SQL Server TOP requires a non-negative integer row count at position {tokens[topStart].Pos}.");
        }
        cursor++;

        if (parenthesized)
        {
            if (cursor >= tokens.Length || tokens[cursor].Type != TokenType.RParen)
            {
                throw new SqlParseException(
                    $"SQL Server TOP parenthesized row count is malformed at position {tokens[topStart].Pos}.");
            }
            cursor++;
        }

        if (cursor < tokens.Length
            && (CoreTokenReader.IsWord(tokens[cursor], "PERCENT")
                || CoreTokenReader.IsWord(tokens[cursor], "WITH")))
        {
            throw new SqlParseException(
                $"SQL Server TOP PERCENT/WITH TIES is not represented by the Core AST at position {tokens[cursor].Pos}.");
        }

        normalizedTokens = [.. tokens.Take(topStart), .. tokens.Skip(cursor)];
        return limit;
    }
}
