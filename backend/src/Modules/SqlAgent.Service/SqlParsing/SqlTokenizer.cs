using SqlAgent.Service.Enums;

namespace SqlAgent.Service.SqlParsing;

public enum TokenType
{
    Keyword, Identifier, Number, String,
    Operator, Comma, Dot, Semicolon, LParen, RParen,
    Parameter, EOF
}

public class Token(TokenType type, string value, int pos, int? sourceLength = null)
{
    public TokenType Type { get; } = type;
    public string Value { get; } = value;
    public int Pos { get; } = pos;
    public int Length { get; } = sourceLength ?? value.Length;
    public int End => Pos + Length;
}

public class SqlTokenizer
{
    private readonly string _sql;
    private readonly SqlAgentToolType? _provider;
    private int _pos;

    public SqlTokenizer(string sql, SqlAgentToolType? provider = null)
    {
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _provider = provider;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "LIKE", "ILIKE",
        "BETWEEN", "IS", "NULL", "AS", "ON", "JOIN", "LEFT", "RIGHT", "INNER",
        "CROSS", "FULL", "OUTER", "ORDER", "BY", "GROUP", "HAVING", "LIMIT",
        "OFFSET", "ASC", "DESC", "DISTINCT", "ALL", "UNION", "INTERSECT",
        "EXCEPT", "WITH", "RECURSIVE", "CASE", "WHEN", "THEN", "ELSE", "END",
        "TRUE", "FALSE", "EXISTS", "OVER", "PARTITION", "FILTER", "SET",
        "ROW", "ROWS", "RANGE", "UNBOUNDED", "PRECEDING", "FOLLOWING", "CURRENT",
        "LATERAL", "USING", "NATURAL", "SOME", "ANY",
        "NULLS", "FIRST", "LAST", "INTERVAL"
    };

    private static readonly HashSet<string> ScalarFuncs = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MAX", "MIN", "ROUND", "COALESCE", "NULLIF",
        "CONCAT", "UPPER", "LOWER", "LENGTH", "TRIM", "SUBSTRING", "REPLACE",
        "ABS", "CEIL", "FLOOR", "MOD", "POWER", "SQRT", "EXP", "LN", "LOG",
        "CAST", "CONVERT", "STRING_AGG", "ARRAY_AGG", "ROW_NUMBER",
        "RANK", "DENSE_RANK", "LAG", "LEAD", "FIRST_VALUE", "LAST_VALUE",
        "NTH_VALUE", "NTILE", "CUME_DIST", "PERCENT_RANK",
        "DATE_TRUNC", "EXTRACT", "DATEADD", "DATEDIFF", "DATEPART",
        "NOW", "SYSDATE", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP",
        "FORMAT", "LEFT", "RIGHT", "REPLICATE", "CHARINDEX", "PATINDEX",
        "DATE_FORMAT", "STR_TO_DATE", "TO_DATE", "TO_CHAR",
        "STUFF", "STRING_SPLIT", "JSON_VALUE", "JSON_QUERY",
    };

    public Token[] Tokenize()
    {
        var tokens = new List<Token>();
        while (_pos < _sql.Length)
        {
            var c = _sql[_pos];
            if (char.IsWhiteSpace(c))
            {
                _pos++;
                continue;
            }

            if (StartsWith("--") && IsLineCommentStart())
            {
                SkipLineComment();
                continue;
            }

            if (StartsWith("/*"))
            {
                SkipBlockComment();
                continue;
            }

            if (c == '#' && _provider == SqlAgentToolType.MySQL)
            {
                SkipLineComment(1);
                continue;
            }

            if ((c is 'q' or 'Q') && IsOracleQuotedStringStart())
            {
                tokens.Add(ReadOracleQuotedString());
                continue;
            }

            if ((c is 'N' or 'n') && PeekChar(1) == '\'')
            {
                tokens.Add(ReadPrefixedStandardString());
                continue;
            }

            if ((c is 'E' or 'e') && PeekChar(1) == '\'')
            {
                if (_provider is not (null or SqlAgentToolType.Postgres))
                    throw Error("PostgreSQL E-string is not valid for the configured provider.", _pos, 2);
                tokens.Add(ReadPostgresEscapeString());
                continue;
            }

            if ((c is 'X' or 'x' or 'B' or 'b') && PeekChar(1) == '\'')
                throw Error("Typed hex/bit literals are not yet represented by the AST.", _pos, 2);

            if (c == '$' && IsDollarQuotedStringStart())
            {
                if (_provider is not (null or SqlAgentToolType.Postgres))
                    throw Error("PostgreSQL dollar-quoted string is not valid for the configured provider.", _pos, 1);
                tokens.Add(ReadDollarQuotedString());
                continue;
            }

            if (c == '\'')
            {
                tokens.Add(ReadDelimited(TokenType.String, '\'', "string literal"));
                continue;
            }

            if (c == '"')
            {
                if (_provider == SqlAgentToolType.MySQL)
                {
                    throw Error(
                        "MySQL double-quote semantics depend on ANSI_QUOTES sql_mode; Core rejects this delimiter because session sql_mode is not part of the compilation plan.",
                        _pos,
                        1);
                }
                tokens.Add(ReadDelimited(TokenType.Identifier, '"', "quoted identifier"));
                continue;
            }

            if (c == '`')
            {
                if (_provider is not (null or SqlAgentToolType.MySQL or SqlAgentToolType.Sqlite))
                    throw Error("Backtick-quoted identifiers are not valid for the configured provider.", _pos, 1);
                tokens.Add(ReadDelimited(TokenType.Identifier, '`', "quoted identifier"));
                continue;
            }

            if (c == '[')
            {
                if (_provider is not (null or SqlAgentToolType.MsSqlServer or SqlAgentToolType.Sqlite))
                    throw Error("Bracket-quoted identifiers are not valid for the configured provider.", _pos, 1);
                tokens.Add(ReadBracketIdentifier());
                continue;
            }

            if (StartsWith("{{"))
            {
                tokens.Add(ReadTemplateParameter());
                continue;
            }

            if (c == ':' && !StartsWith("::"))
            {
                tokens.Add(ReadNamedParameter(':'));
                continue;
            }

            if (c == '@')
            {
                tokens.Add(ReadNamedParameter('@'));
                continue;
            }

            if (c == '$')
            {
                tokens.Add(ReadDollarParameter());
                continue;
            }

            if (c == '?')
            {
                tokens.Add(new Token(TokenType.Parameter, "?", _pos));
                _pos++;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && HasNextDigit()))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            if (IsSqlIdentifierStart(c))
            {
                tokens.Add(ReadWord());
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(Single(TokenType.LParen));
                    break;
                case ')':
                    tokens.Add(Single(TokenType.RParen));
                    break;
                case ',':
                    tokens.Add(Single(TokenType.Comma));
                    break;
                case '.':
                    tokens.Add(Single(TokenType.Dot));
                    break;
                case ';':
                    tokens.Add(Single(TokenType.Semicolon));
                    break;
                case ':':
                    tokens.Add(ReadOperator("::"));
                    break;
                case '+':
                case '-':
                case '*':
                case '/':
                case '%':
                case '=':
                    tokens.Add(Single(TokenType.Operator));
                    break;
                case '<':
                    tokens.Add(StartsWith("<=") ? ReadOperator("<=")
                        : StartsWith("<>") ? ReadOperator("<>")
                        : Single(TokenType.Operator));
                    break;
                case '>':
                    tokens.Add(StartsWith(">=") ? ReadOperator(">=") : Single(TokenType.Operator));
                    break;
                case '!':
                    if (!StartsWith("!="))
                        throw Error("Unexpected character '!'. Did you mean '!='?", _pos, 1);
                    tokens.Add(ReadOperator("!="));
                    break;
                case '|':
                    if (!StartsWith("||"))
                        throw Error("Unexpected character '|'. Did you mean '||'?", _pos, 1);
                    tokens.Add(ReadOperator("||"));
                    break;
                default:
                    throw Error($"Unexpected character '{c}'.", _pos, 1);
            }
        }

        tokens.Add(new Token(TokenType.EOF, "", _sql.Length));
        return [.. tokens];
    }

    private void SkipLineComment(int prefixLength = 2)
    {
        _pos += prefixLength;
        while (_pos < _sql.Length && _sql[_pos] is not '\r' and not '\n')
            _pos++;
    }

    private void SkipBlockComment()
    {
        var start = _pos;
        _pos += 2;
        while (_pos + 1 < _sql.Length && !StartsWith("*/"))
            _pos++;
        if (_pos + 1 >= _sql.Length)
            throw Error("Unterminated block comment.", start, _sql.Length - start);
        _pos += 2;
    }

    private Token ReadDelimited(TokenType type, char delimiter, string description)
    {
        var start = _pos++;
        while (_pos < _sql.Length)
        {
            if (_sql[_pos] == delimiter)
            {
                if (_pos + 1 < _sql.Length && _sql[_pos + 1] == delimiter)
                {
                    _pos += 2;
                    continue;
                }

                _pos++;
                var raw = _sql[start.._pos];
                if (type == TokenType.String)
                {
                    var decoded = raw[1..^1].Replace(new string(delimiter, 2), delimiter.ToString(), StringComparison.Ordinal);
                    return NormalizedString(decoded, start, raw.Length);
                }

                var identifier = raw[1..^1].Replace(new string(delimiter, 2), delimiter.ToString(), StringComparison.Ordinal);
                return new Token(type, identifier, start, raw.Length);
            }

            if (_sql[_pos] == '\\' && delimiter == '\'' && _provider == SqlAgentToolType.MySQL)
                throw Error("MySQL backslash-escaped strings require provider-aware literal decoding and are not yet supported.", start, _pos - start + 1);
            _pos++;
        }

        throw Error($"Unterminated {description}.", start, _sql.Length - start);
    }

    private Token ReadBracketIdentifier()
    {
        var start = _pos++;
        while (_pos < _sql.Length)
        {
            if (_sql[_pos] != ']')
            {
                _pos++;
                continue;
            }

            if (_pos + 1 < _sql.Length && _sql[_pos + 1] == ']')
            {
                _pos += 2;
                continue;
            }

            _pos++;
            var raw = _sql[start.._pos];
            var identifier = raw[1..^1].Replace("]]", "]", StringComparison.Ordinal);
            return new Token(TokenType.Identifier, identifier, start, raw.Length);
        }

        throw Error("Unterminated quoted identifier.", start, _sql.Length - start);
    }

    private Token ReadTemplateParameter()
    {
        var start = _pos;
        _pos += 2;
        var nameStart = _pos;
        while (_pos + 1 < _sql.Length && !StartsWith("}}"))
            _pos++;
        if (_pos + 1 >= _sql.Length)
            throw Error("Unterminated template parameter.", start, _sql.Length - start);

        var name = _sql[nameStart.._pos].Trim();
        _pos += 2;
        if (!IsValidParameterName(name))
            throw Error("Invalid template parameter name.", start, _pos - start);
        return new Token(TokenType.Parameter, _sql[start.._pos], start);
    }

    private Token ReadNamedParameter(char prefix)
    {
        var start = _pos++;
        if (_pos >= _sql.Length || !IsIdentifierStart(_sql[_pos]))
            throw Error($"Invalid parameter beginning with '{prefix}'.", start, 1);
        while (_pos < _sql.Length && IsIdentifierPart(_sql[_pos]))
            _pos++;
        return new Token(TokenType.Parameter, _sql[start.._pos], start);
    }

    private Token ReadDollarParameter()
    {
        var start = _pos++;
        if (_pos >= _sql.Length || !char.IsDigit(_sql[_pos]))
            throw Error("Invalid positional parameter. Expected '$' followed by digits.", start, 1);
        while (_pos < _sql.Length && char.IsDigit(_sql[_pos]))
            _pos++;
        return new Token(TokenType.Parameter, _sql[start.._pos], start);
    }

    private Token ReadNumber()
    {
        var start = _pos;
        var hasDigits = false;

        while (_pos < _sql.Length && char.IsDigit(_sql[_pos]))
        {
            hasDigits = true;
            _pos++;
        }

        if (_pos < _sql.Length && _sql[_pos] == '.')
        {
            _pos++;
            while (_pos < _sql.Length && char.IsDigit(_sql[_pos]))
            {
                hasDigits = true;
                _pos++;
            }
        }

        if (!hasDigits)
            throw Error("Invalid numeric literal.", start, Math.Max(1, _pos - start));

        if (_pos < _sql.Length && _sql[_pos] is 'e' or 'E')
        {
            _pos++;
            if (_pos < _sql.Length && _sql[_pos] is '+' or '-')
                _pos++;
            var exponentStart = _pos;
            while (_pos < _sql.Length && char.IsDigit(_sql[_pos]))
                _pos++;
            if (_pos == exponentStart)
                throw Error("Invalid numeric exponent.", start, _pos - start);
        }

        if (_pos < _sql.Length && (_sql[_pos] == '.' || IsIdentifierStart(_sql[_pos])))
        {
            while (_pos < _sql.Length && (_sql[_pos] == '.' || IsIdentifierPart(_sql[_pos])))
                _pos++;
            throw Error("Invalid numeric literal.", start, _pos - start);
        }

        return new Token(TokenType.Number, _sql[start.._pos], start);
    }

    private Token ReadWord()
    {
        var start = _pos++;
        while (_pos < _sql.Length && IsIdentifierPart(_sql[_pos]))
            _pos++;
        var word = _sql[start.._pos];
        var type = Keywords.Contains(word) || ScalarFuncs.Contains(word)
            ? TokenType.Keyword
            : TokenType.Identifier;
        return new Token(type, word, start);
    }

    private Token Single(TokenType type)
    {
        var token = new Token(type, _sql[_pos].ToString(), _pos);
        _pos++;
        return token;
    }

    private Token ReadOperator(string value)
    {
        var token = new Token(TokenType.Operator, value, _pos);
        _pos += value.Length;
        return token;
    }

    private bool StartsWith(string value) =>
        _pos + value.Length <= _sql.Length &&
        _sql.AsSpan(_pos, value.Length).Equals(value, StringComparison.Ordinal);

    private bool HasNextDigit() => _pos + 1 < _sql.Length && char.IsDigit(_sql[_pos + 1]);
    private char? PeekChar(int offset) => _pos + offset < _sql.Length ? _sql[_pos + offset] : null;
    private bool IsSqlIdentifierStart(char c) => IsIdentifierStart(c)
        || (c == '#' && _provider == SqlAgentToolType.MsSqlServer);
    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    private static bool IsValidParameterName(string value)
    {
        if (string.IsNullOrEmpty(value) || !IsIdentifierStart(value[0]))
            return false;
        return value.Skip(1).All(IsIdentifierPart);
    }

    private static SqlParseException Error(string message, int position, int length) =>
        new($"{message} Position {position}, span [{position}..{position + Math.Max(length, 1)}).");

    private bool IsLineCommentStart()
    {
        if (_provider != SqlAgentToolType.MySQL)
            return true;
        var next = PeekChar(2);
        return next == null || char.IsWhiteSpace(next.Value) || char.IsControl(next.Value);
    }

    private bool IsOracleQuotedStringStart()
    {
        if (_provider is not (null or SqlAgentToolType.Oracle) || PeekChar(1) != '\'')
            return false;
        return PeekChar(2) is not null;
    }

    private Token ReadOracleQuotedString()
    {
        var start = _pos;
        _pos += 2;
        var opening = _sql[_pos++];
        var closing = opening switch
        {
            '[' => ']',
            '{' => '}',
            '(' => ')',
            '<' => '>',
            _ => opening
        };
        var contentStart = _pos;
        while (_pos + 1 < _sql.Length && !(_sql[_pos] == closing && _sql[_pos + 1] == '\''))
            _pos++;
        if (_pos + 1 >= _sql.Length)
            throw Error("Unterminated Oracle q-quoted string.", start, _sql.Length - start);
        var decoded = _sql[contentStart.._pos];
        _pos += 2;
        return NormalizedString(decoded, start, _pos - start);
    }

    private Token ReadPrefixedStandardString()
    {
        var start = _pos++;
        var stringToken = ReadDelimited(TokenType.String, '\'', "national string literal");
        return new Token(TokenType.String, stringToken.Value, start, stringToken.End - start);
    }

    private Token ReadPostgresEscapeString()
    {
        var start = _pos;
        _pos += 2;
        var decoded = new System.Text.StringBuilder();
        while (_pos < _sql.Length)
        {
            var c = _sql[_pos++];
            if (c == '\'')
            {
                if (_pos < _sql.Length && _sql[_pos] == '\'')
                {
                    decoded.Append('\'');
                    _pos++;
                    continue;
                }
                return NormalizedString(decoded.ToString(), start, _pos - start);
            }
            if (c != '\\')
            {
                decoded.Append(c);
                continue;
            }
            if (_pos >= _sql.Length)
                break;
            decoded.Append(_sql[_pos++] switch
            {
                '\\' => '\\',
                '\'' => '\'',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'b' => '\b',
                'f' => '\f',
                var unsupported => throw Error($"Unsupported PostgreSQL E-string escape '\\{unsupported}'.", _pos - 2, 2)
            });
        }
        throw Error("Unterminated PostgreSQL E-string.", start, _sql.Length - start);
    }

    private bool IsDollarQuotedStringStart()
    {
        if (_sql[_pos] != '$') return false;
        var end = _sql.IndexOf('$', _pos + 1);
        if (end < 0) return false;
        var tag = _sql[(_pos + 1)..end];
        return tag.Length == 0 || IsValidParameterName(tag);
    }

    private Token ReadDollarQuotedString()
    {
        var start = _pos;
        var tagEnd = _sql.IndexOf('$', _pos + 1);
        var delimiter = _sql[start..(tagEnd + 1)];
        _pos = tagEnd + 1;
        var contentStart = _pos;
        var close = _sql.IndexOf(delimiter, _pos, StringComparison.Ordinal);
        if (close < 0)
            throw Error("Unterminated PostgreSQL dollar-quoted string.", start, _sql.Length - start);
        var decoded = _sql[contentStart..close];
        _pos = close + delimiter.Length;
        return NormalizedString(decoded, start, _pos - start);
    }

    private static Token NormalizedString(string decoded, int start, int sourceLength)
    {
        var normalized = "'" + decoded.Replace("'", "''", StringComparison.Ordinal) + "'";
        return new Token(TokenType.String, normalized, start, sourceLength);
    }
}
