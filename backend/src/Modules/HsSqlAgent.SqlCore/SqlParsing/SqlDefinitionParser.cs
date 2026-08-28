using System.Globalization;

namespace HsSqlAgent.SqlCore.SqlParsing;

public static class SqlDefinitionParser
{
    public static QueryDefinition ParseQuery(string sql, SqlAgentToolType? provider = null)
    {
        var tokens = new SqlTokenizer(sql, provider).Tokenize();
        var topLimit = NormalizeSqlServerTop(tokens, provider, out var normalizedTokens);
        tokens = CommaFromNormalizer.Normalize(normalizedTokens);
        ValidateStatementTokens(tokens);
        SqlSyntaxGuard.ValidateQuery(tokens);
        var definition = new SqlParser(tokens).Parse();
        if (topLimit is not null)
        {
            if (definition.CombineConditions is { Count: > 0 })
                throw new SqlParseException("SQL Server TOP with set operations is not yet represented losslessly by the query AST.");
            if (definition.Limit is not null)
                throw new SqlParseException("SQL Server TOP cannot be combined with LIMIT in the canonical query definition.");
            definition.Limit = topLimit.Value;
        }
        return definition;
    }

    public static DmlDefinition ParseDml(string sql, SqlAgentToolType? provider = null)
    {
        var tokens = new SqlTokenizer(sql, provider).Tokenize();
        ValidateStatementTokens(tokens);
        return new DmlTokenParser(sql, tokens, provider).Parse();
    }

    private sealed class DmlTokenParser(string sql, Token[] tokens, SqlAgentToolType? provider)
    {
        private int _pos;

        public DmlDefinition Parse()
        {
            DmlDefinition result;
            if (PeekWord("INSERT")) result = ParseInsert();
            else if (PeekWord("UPDATE")) result = ParseUpdate();
            else if (PeekWord("DELETE")) result = ParseDelete();
            else throw Error("Expected INSERT, UPDATE, or DELETE DML statement.");

            if (Peek().Type == TokenType.Semicolon) _pos++;
            if (Peek().Type != TokenType.EOF)
                throw Error($"Unexpected token '{Peek().Value}'; the complete DML statement was not consumed.");
            return result;
        }

        private DmlDefinition ParseInsert()
        {
            ExpectWord("INSERT");
            ExpectWord("INTO");
            var table = ParseQualifiedIdentifier("table name");
            Expect(TokenType.LParen);
            var columns = new List<string> { ParseQualifiedIdentifier("column name") };
            while (Match(TokenType.Comma)) columns.Add(ParseQualifiedIdentifier("column name"));
            Expect(TokenType.RParen);
            ExpectWord("VALUES");

            var rows = new List<List<object>>();
            do
            {
                Expect(TokenType.LParen);
                var row = new List<object> { ParseLiteral()! };
                while (Match(TokenType.Comma)) row.Add(ParseLiteral()!);
                Expect(TokenType.RParen);
                if (row.Count != columns.Count)
                    throw Error("INSERT column count must match value count.");
                rows.Add(row);
            } while (Match(TokenType.Comma));

            if (rows.Count == 1)
                return new DmlDefinition
                {
                    Operation = DmlOperation.Insert,
                    TableName = table,
                    Values = [.. columns.Select((column, i) => new NameValuePair { FieldName = column, Value = rows[0][i] })]
                };

            return new DmlDefinition
            {
                Operation = DmlOperation.Insert,
                TableName = table,
                Columns = columns,
                MultiValues = rows
            };
        }

        private DmlDefinition ParseUpdate()
        {
            ExpectWord("UPDATE");
            var table = ParseQualifiedIdentifier("table name");
            ExpectWord("SET");
            var values = new List<NameValuePair>();
            do
            {
                var field = ParseQualifiedIdentifier("assignment column");
                ExpectOperator("=");
                values.Add(new NameValuePair { FieldName = field, Value = ParseLiteral() });
            } while (Match(TokenType.Comma));

            return new DmlDefinition
            {
                Operation = DmlOperation.Update,
                TableName = table,
                Values = values,
                WhereConditions = ParseOptionalWhere()
            };
        }

        private DmlDefinition ParseDelete()
        {
            ExpectWord("DELETE");
            ExpectWord("FROM");
            return new DmlDefinition
            {
                Operation = DmlOperation.Delete,
                TableName = ParseQualifiedIdentifier("table name"),
                WhereConditions = ParseOptionalWhere()
            };
        }

        private List<WhereCondition>? ParseOptionalWhere()
        {
            if (!PeekWord("WHERE")) return null;
            _pos++;
            var first = Peek();
            if (first.Type is TokenType.EOF or TokenType.Semicolon)
                throw Error("WHERE must contain a predicate.");
            var end = _pos;
            while (end < tokens.Length && tokens[end].Type is not (TokenType.EOF or TokenType.Semicolon)) end++;
            var last = tokens[end - 1];
            var whereSql = sql[first.Pos..last.End];
            _pos = end;
            var parsed = ParseQuery($"SELECT * FROM __dml_source WHERE {whereSql}", provider);
            return parsed.WhereColumnsAndValues is { Count: > 0 } conditions ? conditions : null;
        }

        private object? ParseLiteral()
        {
            if (PeekWord("DATE") || PeekWord("TIME") || PeekWord("TIMESTAMP"))
            {
                var temporalType = Peek().Value.ToUpperInvariant();
                _pos++;
                bool? withTimeZone = null;
                if (temporalType is "TIME" or "TIMESTAMP"
                    && (PeekWord("WITH") || PeekWord("WITHOUT")))
                {
                    withTimeZone = PeekWord("WITH");
                    _pos++;
                    ExpectWord("TIME");
                    ExpectWord("ZONE");
                }
                var literalToken = Peek();
                if (literalToken.Type != TokenType.String)
                    throw Error($"{temporalType} must be followed by a quoted ISO temporal literal.");
                _pos++;
                var literal = literalToken.Value[1..^1].Replace("''", "'", StringComparison.Ordinal);
                if (temporalType == "DATE" && SqlTemporalLiteralParser.TryParseDate(literal, out var date))
                    return date;
                if (temporalType == "TIME" && SqlTemporalLiteralParser.TryParseTime(literal, out var time))
                {
                    if (withTimeZone == true)
                        throw Error("TIME WITH TIME ZONE is not yet supported by the canonical temporal model.");
                    return time;
                }
                if (temporalType == "TIMESTAMP" && SqlTemporalLiteralParser.TryParseTimestamp(literal, out var timestamp))
                {
                    if (withTimeZone == true && timestamp is not SqlOffsetDateTimeValue)
                        throw Error("TIMESTAMP WITH TIME ZONE requires an explicit UTC offset or Z suffix.");
                    if (withTimeZone == false && timestamp is SqlOffsetDateTimeValue)
                        throw Error("TIMESTAMP WITHOUT TIME ZONE must not include a UTC offset.");
                    return timestamp;
                }

                var expected = temporalType switch
                {
                    "DATE" => "YYYY-MM-DD",
                    "TIME" => "HH:mm[:ss[.fffffff]] without an offset",
                    _ => "YYYY-MM-DD[ T]HH:mm[:ss[.fffffff]][offset]"
                };
                throw Error($"Invalid {temporalType} literal '{literal}'. Expected {expected}.");
            }

            var sign = 1;
            if (Peek().Type == TokenType.Operator && Peek().Value is "-" or "+")
            {
                sign = Peek().Value == "-" ? -1 : 1;
                _pos++;
                if (Peek().Type != TokenType.Number)
                    throw Error("A unary sign in a DML value must be followed by a numeric literal.");
            }
            var token = Peek();
            if (token.Type == TokenType.String)
            {
                _pos++;
                return token.Value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }
            if (token.Value.Equals("NULL", StringComparison.OrdinalIgnoreCase)) { _pos++; return null; }
            if (token.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) { _pos++; return true; }
            if (token.Value.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) { _pos++; return false; }
            if (token.Type == TokenType.Number)
            {
                _pos++;
                var text = sign < 0 ? $"-{token.Value}" : token.Value;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
            }
            throw Error($"Unsupported DML value expression beginning with '{token.Value}'. Only scalar literals are accepted.");
        }

        private string ParseQualifiedIdentifier(string description)
        {
            var parts = new List<string> { ParseIdentifier(description) };
            while (Match(TokenType.Dot)) parts.Add(ParseIdentifier(description));
            return string.Join('.', parts);
        }

        private string ParseIdentifier(string description)
        {
            var token = Peek();
            if (token.Type is not (TokenType.Identifier or TokenType.Keyword))
                throw Error($"Expected {description} but got '{token.Value}'.");
            _pos++;
            return token.Value;
        }

        private bool PeekWord(string value) => Peek().Value.Equals(value, StringComparison.OrdinalIgnoreCase);
        private void ExpectWord(string value)
        {
            if (!PeekWord(value)) throw Error($"Expected keyword '{value}' but got '{Peek().Value}'.");
            _pos++;
        }

        private void ExpectOperator(string value)
        {
            if (Peek().Type != TokenType.Operator || Peek().Value != value)
                throw Error($"Expected operator '{value}' but got '{Peek().Value}'.");
            _pos++;
        }

        private void Expect(TokenType type)
        {
            if (Peek().Type != type) throw Error($"Expected {type} but got {Peek().Type} ('{Peek().Value}').");
            _pos++;
        }

        private bool Match(TokenType type)
        {
            if (Peek().Type != type) return false;
            _pos++;
            return true;
        }

        private Token Peek() => _pos < tokens.Length ? tokens[_pos] : tokens[^1];
        private SqlParseException Error(string message) => new($"{message} Position {Peek().Pos}.");
    }

    private static int? NormalizeSqlServerTop(
        Token[] tokens,
        SqlAgentToolType? provider,
        out Token[] normalizedTokens)
    {
        normalizedTokens = tokens;
        if (provider is null
            || !SqlSourceDialectGrammarRules.For(provider.Value).SupportsTop)
        {
            return null;
        }

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
            if (depth == 0
                && token.Value.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                selectIndex = i;
                break;
            }
        }

        if (selectIndex < 0)
            return null;

        var cursor = selectIndex + 1;
        if (cursor < tokens.Length
            && (tokens[cursor].Value.Equals("DISTINCT", StringComparison.OrdinalIgnoreCase)
                || tokens[cursor].Value.Equals("ALL", StringComparison.OrdinalIgnoreCase)))
        {
            cursor++;
        }

        if (cursor >= tokens.Length
            || !tokens[cursor].Value.Equals("TOP", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

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
                throw new SqlParseException(
                    $"SQL Server TOP parenthesized row count is malformed at position {tokens[topStart].Pos}.");
            cursor++;
        }

        if (cursor < tokens.Length
            && (tokens[cursor].Value.Equals("PERCENT", StringComparison.OrdinalIgnoreCase)
                || tokens[cursor].Value.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
        {
            throw new SqlParseException(
                $"SQL Server TOP PERCENT/WITH TIES is not yet represented by the canonical query AST at position {tokens[cursor].Pos}.");
        }

        normalizedTokens =
        [
            .. tokens.Take(topStart),
            .. tokens.Skip(cursor)
        ];
        return limit;
    }

    private static void ValidateStatementTokens(Token[] tokens)
    {
        var content = tokens.Where(t => t.Type != TokenType.EOF).ToArray();
        for (var i = 0; i < content.Length; i++)
        {
            var token = content[i];
            if (token.Type == TokenType.Parameter)
            {
                throw new SqlParseException(
                    $"Unbound SQL parameter '{token.Value}' at position {token.Pos}. " +
                    "Runtime SQL parameters are not accepted; use a declared Custom Tool parameter.");
            }

            if (token.Type == TokenType.Semicolon && i != content.Length - 1)
                throw new SqlParseException($"Only one SQL statement is allowed; unexpected semicolon at position {token.Pos}.");
        }
    }

}
