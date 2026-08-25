namespace SqlAgent.Service.SqlTranslation.Templates.Parsing;

internal sealed class TemplateLexer(string template)
{
    private readonly string _template = template;
    private int _position;

    internal IReadOnlyList<TemplateToken> Lex()
    {
        var tokens = new List<TemplateToken>();
        while (_position < _template.Length)
        {
            if (char.IsWhiteSpace(Current)) { _position++; continue; }
            var start = _position;
            if (char.IsLetter(Current) || Current == '_') { tokens.Add(ReadIdentifier(start)); continue; }
            if (char.IsDigit(Current)) { tokens.Add(ReadNumber(start)); continue; }
            switch (Current)
            {
                case '$': tokens.Add(ReadPrefixed(start, TemplateTokenKind.ArgumentReference, false)); break;
                case '@': tokens.Add(ReadPrefixed(start, TemplateTokenKind.SqlToken, true)); break;
                case '\'': tokens.Add(ReadString(start)); break;
                case '(': tokens.Add(Single(TemplateTokenKind.LeftParen)); break;
                case ')': tokens.Add(Single(TemplateTokenKind.RightParen)); break;
                case ',': tokens.Add(Single(TemplateTokenKind.Comma)); break;
                case ':': tokens.Add(Single(TemplateTokenKind.Colon)); break;
                case '+': tokens.Add(Single(TemplateTokenKind.Plus)); break;
                case '-': tokens.Add(Single(TemplateTokenKind.Minus)); break;
                case '*': tokens.Add(Single(TemplateTokenKind.Star)); break;
                case '/': tokens.Add(Single(TemplateTokenKind.Slash)); break;
                case '%': tokens.Add(Single(TemplateTokenKind.Percent)); break;
                case '=': tokens.Add(Single(TemplateTokenKind.Equal)); break;
                case '>': tokens.Add(ReadPair('=', TemplateTokenKind.GreaterThanOrEqual, TemplateTokenKind.GreaterThan)); break;
                case '<': tokens.Add(ReadLessThan()); break;
                case '!': tokens.Add(ReadRequiredPair('=', TemplateTokenKind.NotEqual)); break;
                case '|': tokens.Add(ReadRequiredPair('|', TemplateTokenKind.Concat)); break;
                default: throw Error($"Unexpected character '{Current}'", start);
            }
        }
        tokens.Add(new(TemplateTokenKind.End, string.Empty, string.Empty, _template.Length));
        return tokens;
    }

    private char Current => _position < _template.Length ? _template[_position] : '\0';
    private TemplateToken ReadIdentifier(int start)
    {
        while (char.IsLetterOrDigit(Current) || Current == '_') _position++;
        var text = _template[start.._position];
        return new(TemplateTokenKind.Identifier, text, text, start);
    }
    private TemplateToken ReadNumber(int start)
    {
        while (char.IsDigit(Current)) _position++;
        if (Current == '.' && _position + 1 < _template.Length && char.IsDigit(_template[_position + 1]))
        { _position++; while (char.IsDigit(Current)) _position++; }
        var text = _template[start.._position];
        return new(TemplateTokenKind.Number, text, text, start);
    }
    private TemplateToken ReadPrefixed(int start, TemplateTokenKind kind, bool allowDot)
    {
        _position++;
        var valueStart = _position;
        while (char.IsLetterOrDigit(Current) || Current == '_' || (allowDot && Current == '.')) _position++;
        if (_position == valueStart)
            throw Error(kind == TemplateTokenKind.ArgumentReference ? "Expected digit after $ in template" : "Expected SQL token after @ in template", start);
        var value = _template[valueStart.._position];
        if (kind == TemplateTokenKind.ArgumentReference && value.Any(character => !char.IsDigit(character)))
            throw Error("Template argument reference must contain only digits", start);
        return new(kind, value, _template[start.._position], start);
    }
    private TemplateToken ReadString(int start)
    {
        _position++;
        var value = new System.Text.StringBuilder();
        while (_position < _template.Length)
        {
            if (Current != '\'') { value.Append(Current); _position++; continue; }
            _position++;
            if (Current == '\'') { value.Append('\''); _position++; continue; }
            return new(TemplateTokenKind.String, value.ToString(), _template[start.._position], start);
        }
        throw Error("Unterminated string literal", start);
    }
    private TemplateToken Single(TemplateTokenKind kind)
    {
        var start = _position++;
        var text = _template[start.._position];
        return new(kind, text, text, start);
    }
    private TemplateToken ReadPair(char expected, TemplateTokenKind pair, TemplateTokenKind single)
    {
        var start = _position++;
        if (Current == expected) { _position++; return new(pair, _template[start.._position], _template[start.._position], start); }
        return new(single, _template[start.._position], _template[start.._position], start);
    }
    private TemplateToken ReadRequiredPair(char expected, TemplateTokenKind kind)
    {
        var start = _position++;
        if (Current != expected) throw Error($"Expected '{expected}' after '{_template[start]}'", start);
        _position++;
        return new(kind, _template[start.._position], _template[start.._position], start);
    }
    private TemplateToken ReadLessThan()
    {
        var start = _position++;
        if (Current == '=') { _position++; return new(TemplateTokenKind.LessThanOrEqual, "<=", "<=", start); }
        if (Current == '>') { _position++; return new(TemplateTokenKind.NotEqual, "<>", "<>", start); }
        return new(TemplateTokenKind.LessThan, "<", "<", start);
    }
    private FormatException Error(string message, int position) => new($"{message} at position {position} in template: {_template}");
}
