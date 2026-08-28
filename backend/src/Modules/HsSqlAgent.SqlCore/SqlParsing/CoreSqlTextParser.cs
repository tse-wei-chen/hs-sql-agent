using System.Globalization;

namespace HsSqlAgent.SqlCore.SqlParsing;

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
                mysqlAnsiQuotes: SqlSourceDialectGrammarRules.UsesMySqlAnsiQuotedIdentifiers(sourceDialect, sourceProfile),
                mysqlNoBackslashEscapes: SqlSourceDialectGrammarRules.UsesMySqlNoBackslashEscapes(sourceDialect, sourceProfile)).Tokenize(),
            sourceDialect,
            sourceProfile);
        ValidateStatementTokens(tokens, sourceDialect);
        var topLimit = NormalizeSqlServerTop(tokens, sourceDialect, out var normalizedTokens);
        normalizedTokens = CommaFromNormalizer.Normalize(normalizedTokens);
        var statement = new CoreQueryTextParser(
            new CoreTokenReader(normalizedTokens),
            sourceDialect,
            requireExplicitLikeEscape: SqlSourceDialectGrammarRules.UsesMySqlNoBackslashEscapes(sourceDialect, sourceProfile)).ParseComplete(topLimit);
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
                mysqlAnsiQuotes: SqlSourceDialectGrammarRules.UsesMySqlAnsiQuotedIdentifiers(sourceDialect, sourceProfile),
                mysqlNoBackslashEscapes: SqlSourceDialectGrammarRules.UsesMySqlNoBackslashEscapes(sourceDialect, sourceProfile)).Tokenize(),
            sourceDialect,
            sourceProfile);
        ValidateStatementTokens(tokens, sourceDialect);
        var conflictExtraction = CoreDmlConflictTextParser.Extract(
            tokens,
            sourceDialect,
            sourceProfile?.ServerVersion);
        var statement = new CoreDmlTextParser(
            new CoreTokenReader(conflictExtraction.Tokens),
            sourceDialect,
            requireExplicitLikeEscape: SqlSourceDialectGrammarRules.UsesMySqlNoBackslashEscapes(sourceDialect, sourceProfile),
            sourceServerVersion: sourceProfile?.ServerVersion).ParseComplete();
        if (conflictExtraction.Conflict is not null)
        {
            if (statement is not InsertStatement insert)
            {
                throw new SqlParseException(
                    "INSERT conflict extraction must attach to a canonical INSERT statement.");
            }
            statement = insert with { Conflict = conflictExtraction.Conflict };
        }
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
        switch (SqlProviderCapabilityProfileRules.ValidationIssue(
                    sourceProfile,
                    sourceDialect))
        {
            case SqlProviderCapabilityProfileValidationIssue.None:
                return;
            case SqlProviderCapabilityProfileValidationIssue.ProviderMismatch:
                throw new ArgumentException(
                    $"Source capability profile declares provider {sourceProfile!.Provider}, " +
                    $"but parser source dialect is {sourceDialect}.",
                    nameof(sourceProfile));
            case SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel:
                throw new ArgumentOutOfRangeException(
                    nameof(sourceProfile),
                    sourceProfile!.CompatibilityLevel,
                    "Provider compatibility level must be non-negative.");
            default:
                throw new InvalidOperationException(
                    "Unsupported source capability profile validation issue.");
        }
    }

    private static Token[] ApplySourceProfileTokens(
        Token[] tokens,
        SqlAgentToolType sourceDialect,
        SqlProviderCapabilityProfile? sourceProfile)
    {
        if (!SqlConcatCapabilityRules.SupportsMySqlPipesAsConcat(
                sourceDialect,
                sourceProfile))
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

    private static void ValidateStatementTokens(
        Token[] tokens,
        SqlAgentToolType sourceDialect)
    {
        var content = tokens.Where(token => token.Type != TokenType.EOF).ToArray();
        var grammar = SqlSourceDialectGrammarRules.For(sourceDialect);
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
                && !grammar.SupportsDoubleColonCast)
            {
                throw new SqlParseException(
                    $"PostgreSQL '::' cast syntax is not valid for source dialect {sourceDialect} at position {token.Pos}; " +
                    "use CAST(expression AS type) for portable raw SQL.");
            }
            if (token.Type == TokenType.Keyword
                && token.Value.Equals("LIMIT", StringComparison.OrdinalIgnoreCase)
                && !grammar.SupportsLimitKeyword)
            {
                throw new SqlParseException(
                    $"LIMIT is not valid raw source syntax for dialect {sourceDialect} at position {token.Pos}; " +
                    "use the source provider's native row-limiting form or a structured Core row limit.");
            }
            if (token.Type == TokenType.Keyword
                && !grammar.SupportsBareBooleanKeywords
                && (token.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                    || token.Value.Equals("FALSE", StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlParseException(
                    $"Bare {token.Value.ToUpperInvariant()} is not valid T-SQL boolean-literal source syntax at position {token.Pos}; " +
                    "SQL Server bit constants use 0 or 1, and Core does not reinterpret bare TRUE/FALSE tokens as identifiers.");
            }
            if (TryGetTypedTemporalLiteralStart(content, i, out var temporalType, out var hasZoneQualifier)
                && !grammar.SupportsTypedTemporalLiteral(temporalType, hasZoneQualifier))
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

        if (temporalType == "DATE"
            || (!CoreTokenReader.IsWord(next, "WITH")
                && !CoreTokenReader.IsWord(next, "WITHOUT")))
        {
            return false;
        }

        // Only classify the SQL-standard typed-literal form
        // TIMESTAMP/TIME [WITH|WITHOUT] TIME ZONE '...'. A CAST target such as
        // CAST(value AS TIMESTAMP WITH TIME ZONE) has the same leading words but
        // no following string literal and must not be rejected as a typed literal.
        if (index + 4 >= tokens.Length
            || !CoreTokenReader.IsWord(tokens[index + 2], "TIME")
            || !CoreTokenReader.IsWord(tokens[index + 3], "ZONE")
            || tokens[index + 4].Type != TokenType.String)
        {
            return false;
        }

        hasZoneQualifier = true;
        return true;
    }

    private static int? NormalizeSqlServerTop(
        Token[] tokens,
        SqlAgentToolType provider,
        out Token[] normalizedTokens)
    {
        normalizedTokens = tokens;
        if (!SqlSourceDialectGrammarRules.For(provider).SupportsTop)
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
