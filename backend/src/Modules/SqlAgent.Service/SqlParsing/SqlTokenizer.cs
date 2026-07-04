namespace SqlAgent.Service.SqlParsing;

public enum TokenType
{
    Keyword, Identifier, Number, String,
    Operator, Comma, Dot, Semicolon, LParen, RParen,
    Parameter, EOF
}

public class Token(TokenType type, string value, int pos)
{
    public TokenType Type { get; } = type;
    public string Value { get; } = value;
    public int Pos { get; } = pos;
}

public class SqlTokenizer(string sql)
{
    private readonly string _sql = sql;
    private int _pos;

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "LIKE", "ILIKE",
        "BETWEEN", "IS", "NULL", "AS", "ON", "JOIN", "LEFT", "RIGHT", "INNER",
        "CROSS", "FULL", "OUTER", "ORDER", "BY", "GROUP", "HAVING", "LIMIT",
        "OFFSET", "ASC", "DESC", "DISTINCT", "ALL", "UNION", "INTERSECT",
        "EXCEPT", "WITH", "RECURSIVE", "CASE", "WHEN", "THEN", "ELSE", "END",
        "TRUE", "FALSE", "EXISTS", "OVER", "PARTITION", "FILTER", "SET",
        "ROW", "RANGE", "UNBOUNDED", "PRECEDING", "FOLLOWING", "CURRENT",
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
        "NOW", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP",
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
            if (char.IsWhiteSpace(c)) { _pos++; continue; }

            if (c == '-' && _pos + 1 < _sql.Length && _sql[_pos + 1] == '-')
            {
                _pos += 2;
                while (_pos < _sql.Length && _sql[_pos] != '\n') _pos++;
                continue;
            }
            if (c == '/' && _pos + 1 < _sql.Length && _sql[_pos + 1] == '*')
            {
                _pos += 2;
                while (_pos + 1 < _sql.Length && !(_sql[_pos] == '*' && _sql[_pos + 1] == '/')) _pos++;
                _pos += 2;
                continue;
            }

            if (c == '\'')
            {
                var start = _pos;
                _pos++;
                while (_pos < _sql.Length)
                {
                    if (_sql[_pos] == '\'' && _pos + 1 < _sql.Length && _sql[_pos + 1] == '\'') _pos += 2;
                    else if (_sql[_pos] == '\'') { _pos++; break; }
                    else _pos++;
                }
                tokens.Add(new Token(TokenType.String, _sql[start.._pos], start));
                continue;
            }

            if (c == '"')
            {
                var start = _pos;
                _pos++;
                while (_pos < _sql.Length && _sql[_pos] != '"') _pos++;
                if (_pos < _sql.Length) _pos++;
                tokens.Add(new Token(TokenType.Identifier, _sql[start.._pos], start));
                continue;
            }

            if (c == '`')
            {
                var start = _pos;
                _pos++;
                while (_pos < _sql.Length && _sql[_pos] != '`') _pos++;
                if (_pos < _sql.Length) _pos++;
                tokens.Add(new Token(TokenType.Identifier, _sql[start.._pos], start));
                continue;
            }

            if (c == '[')
            {
                var start = _pos;
                _pos++;
                while (_pos < _sql.Length && _sql[_pos] != ']') _pos++;
                if (_pos < _sql.Length) _pos++;
                tokens.Add(new Token(TokenType.Identifier, _sql[start.._pos], start));
                continue;
            }

            if (c == '{' && _pos + 1 < _sql.Length && _sql[_pos + 1] == '{')
            {
                var start = _pos;
                _pos += 2;
                while (_pos < _sql.Length && !(_sql[_pos] == '}' && _pos + 1 < _sql.Length && _sql[_pos + 1] == '}')) _pos++;
                if (_pos + 1 < _sql.Length) _pos += 2;
                tokens.Add(new Token(TokenType.Parameter, _sql[start.._pos], start));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && _pos + 1 < _sql.Length && char.IsDigit(_sql[_pos + 1])))
            {
                var start = _pos;
                if (c == '.') _pos++;
                while (_pos < _sql.Length && (char.IsDigit(_sql[_pos]) || _sql[_pos] == '.')) _pos++;
                tokens.Add(new Token(TokenType.Number, _sql[start.._pos], start));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var start = _pos;
                while (_pos < _sql.Length && (char.IsLetterOrDigit(_sql[_pos]) || _sql[_pos] == '_' || _sql[_pos] == '@')) _pos++;
                var word = _sql[start.._pos];
                var type = Keywords.Contains(word) ? TokenType.Keyword
                    : ScalarFuncs.Contains(word) ? TokenType.Keyword
                    : TokenType.Identifier;
                tokens.Add(new Token(type, word, start));
                continue;
            }

            switch (c)
            {
                case '(': tokens.Add(new Token(TokenType.LParen, "(", _pos)); _pos++; break;
                case ')': tokens.Add(new Token(TokenType.RParen, ")", _pos)); _pos++; break;
                case ',': tokens.Add(new Token(TokenType.Comma, ",", _pos)); _pos++; break;
                case '.': tokens.Add(new Token(TokenType.Dot, ".", _pos)); _pos++; break;
                case ';': tokens.Add(new Token(TokenType.Semicolon, ";", _pos)); _pos++; break;
                case ':':
                    if (_pos + 1 < _sql.Length && _sql[_pos + 1] == ':')
                    { tokens.Add(new Token(TokenType.Operator, "::", _pos)); _pos += 2; }
                    else _pos++;
                    break;
                case '+':
                case '-':
                case '*':
                case '/':
                case '%':
                    tokens.Add(new Token(TokenType.Operator, c.ToString(), _pos)); _pos++; break;
                case '=':
                    tokens.Add(new Token(TokenType.Operator, "=", _pos)); _pos++; break;
                case '<':
                    if (_pos + 1 < _sql.Length && _sql[_pos + 1] == '=')
                    { tokens.Add(new Token(TokenType.Operator, "<=", _pos)); _pos += 2; }
                    else if (_pos + 1 < _sql.Length && _sql[_pos + 1] == '>')
                    { tokens.Add(new Token(TokenType.Operator, "<>", _pos)); _pos += 2; }
                    else { tokens.Add(new Token(TokenType.Operator, "<", _pos)); _pos++; }
                    break;
                case '>':
                    if (_pos + 1 < _sql.Length && _sql[_pos + 1] == '=')
                    { tokens.Add(new Token(TokenType.Operator, ">=", _pos)); _pos += 2; }
                    else { tokens.Add(new Token(TokenType.Operator, ">", _pos)); _pos++; }
                    break;
                case '!':
                    if (_pos + 1 < _sql.Length && _sql[_pos + 1] == '=')
                    { tokens.Add(new Token(TokenType.Operator, "!=", _pos)); _pos += 2; }
                    else { tokens.Add(new Token(TokenType.Operator, "!", _pos)); _pos++; }
                    break;
                case '|':
                    if (_pos + 1 < _sql.Length && _sql[_pos + 1] == '|')
                    { tokens.Add(new Token(TokenType.Operator, "||", _pos)); _pos += 2; }
                    else
                        throw new SqlParseException($"Unexpected character '|' at position {_pos}. Did you mean '||' (string concatenation)?");
                    break;
                default:
                    _pos++;
                    break;
            }
        }
        tokens.Add(new Token(TokenType.EOF, "", _sql.Length));
        return [.. tokens];
    }
}
