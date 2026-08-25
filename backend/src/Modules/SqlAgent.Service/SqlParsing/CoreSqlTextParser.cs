using System.Globalization;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.SqlParsing;

/// <summary>
/// Parser-native entry point for raw SQL. It produces the independent Core AST directly, preserving
/// token source spans and quoted-identifier intent instead of using public transport DTOs as a parser IR.
/// </summary>
public static class CoreSqlTextParser
{
    public static ParsedStatement ParseQuery(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ValidateSourceProfile(sourceDialect, sourceProfile);
        var tokens = ApplySourceProfileTokens(
            new SqlTokenizer(
                sql,
                sourceDialect,
                mysqlAnsiQuotes: SupportsMySqlAnsiQuotes(sourceDialect, sourceProfile)).Tokenize(),
            sourceDialect,
            sourceProfile);
        ValidateStatementTokens(tokens, sourceDialect);
        var topLimit = NormalizeSqlServerTop(tokens, sourceDialect, out var normalizedTokens);
        normalizedTokens = CommaFromNormalizer.Normalize(normalizedTokens);
        var statement = new CoreQueryTextParser(
            new CoreTokenReader(normalizedTokens),
            sourceDialect).ParseComplete(topLimit);
        return new ParsedStatement(
            statement,
            sourceDialect,
            EnforceSourceDialectSyntax: true,
            SourceProfile: sourceProfile);
    }

    public static ParsedStatement ParseDml(
        string sql,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ValidateSourceProfile(sourceDialect, sourceProfile);
        var tokens = ApplySourceProfileTokens(
            new SqlTokenizer(
                sql,
                sourceDialect,
                mysqlAnsiQuotes: SupportsMySqlAnsiQuotes(sourceDialect, sourceProfile)).Tokenize(),
            sourceDialect,
            sourceProfile);
        ValidateStatementTokens(tokens, sourceDialect);
        var statement = new CoreDmlTextParser(
            new CoreTokenReader(tokens),
            sourceDialect).ParseComplete();
        return new ParsedStatement(
            statement,
            sourceDialect,
            EnforceSourceDialectSyntax: true,
            SourceProfile: sourceProfile);
    }

    private static void ValidateSourceProfile(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile)
    {
        if (sourceProfile is null) return;
        if (sourceProfile.Provider != sourceDialect)
        {
            throw new ArgumentException(
                $"Source capability profile declares provider {sourceProfile.Provider}, " +
                $"but parser source dialect is {sourceDialect}.",
                nameof(sourceProfile));
        }
        if (sourceProfile.CompatibilityLevel is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceProfile),
                sourceProfile.CompatibilityLevel,
                "Provider compatibility level must be non-negative.");
        }
    }

    private static bool SupportsMySqlAnsiQuotes(
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile) =>
        sourceDialect == SqlAgentToolType.MySQL
        && sourceProfile is { Provider: SqlAgentToolType.MySQL }
        && (sourceProfile.HasSessionMode("ANSI_QUOTES")
            || sourceProfile.HasSessionMode("ANSI"));

    private static Token[] ApplySourceProfileTokens(
        Token[] tokens,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile)
    {
        if (sourceDialect != SqlAgentToolType.MySQL
            || sourceProfile is null
            || (!sourceProfile.HasSessionMode("PIPES_AS_CONCAT")
                && !sourceProfile.HasSessionMode("ANSI")))
        {
            return tokens;
        }

        return tokens
            .Select(token => token.Type == TokenType.Operator && token.Value == "||"
                ? new Token(
                    TokenType.Operator,
                    CoreExpressionTextParser.MySqlPipesConcatToken,
                    token.Pos,
                    token.Length)
                : token)
            .ToArray();
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
            if (token.Type == TokenType.Keyword
                && sourceDialect == SqlAgentToolType.MsSqlServer
                && (token.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                    || token.Value.Equals("FALSE", StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlParseException(
                    $"Bare {token.Value.ToUpperInvariant()} is not valid T-SQL boolean-literal source syntax at position {token.Pos}; " +
                    "SQL Server bit constants use 0 or 1, and Core does not reinterpret bare TRUE/FALSE tokens as identifiers.");
            }
            if (TryGetTypedTemporalLiteralStart(content, i, out var temporalType, out var hasZoneQualifier)
                && !SupportsRawTypedTemporalLiteral(sourceDialect, temporalType, hasZoneQualifier))
            {
                throw new SqlParseException(
                    $"{temporalType} typed temporal literal spelling is not valid for raw source dialect {sourceDialect} in the Core source profile at position {token.Pos}; " +
                    "use source-native CAST/CONVERT/function syntax or a structured Core temporal value.");
            }
            if (token.Type == TokenType.Semicolon && i != content.Length - 1)
            {
                throw new SqlParseException(
                    $"Only one SQL statement is allowed; unexpected semicolon at position {token.Pos}.");
            }
        }
    }

    private static bool TryGetTypedTemporalLiteralStart(
        Token[] tokens,
        int index,
        out string temporalType,
        out bool hasZoneQualifier)
    {
        temporalType = string.Empty;
        hasZoneQualifier = false;
        if (index + 1 >= tokens.Length)
            return false;

        var token = tokens[index];
        if (CoreTokenReader.IsWord(token, "DATE")) temporalType = "DATE";
        else if (CoreTokenReader.IsWord(token, "TIME")) temporalType = "TIME";
        else if (CoreTokenReader.IsWord(token, "TIMESTAMP")) temporalType = "TIMESTAMP";
        else return false;

        var next = tokens[index + 1];
        if (next.Type == TokenType.String)
            return true;

        hasZoneQualifier = temporalType != "DATE"
            && (CoreTokenReader.IsWord(next, "WITH")
                || CoreTokenReader.IsWord(next, "WITHOUT"));
        return hasZoneQualifier;
    }

    private static bool SupportsRawTypedTemporalLiteral(
        SqlAgentToolType sourceDialect,
        string temporalType,
        bool hasZoneQualifier) =>
        sourceDialect switch
        {
            SqlAgentToolType.Postgres => true,
            SqlAgentToolType.MySQL => !hasZoneQualifier,
            SqlAgentToolType.MsSqlServer => false,
            SqlAgentToolType.Sqlite => false,
            SqlAgentToolType.Oracle => temporalType != "TIME" && !hasZoneQualifier,
            SqlAgentToolType.Firebird => !hasZoneQualifier,
            _ => false
        };

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