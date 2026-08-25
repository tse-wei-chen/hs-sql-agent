using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Ast;

namespace HsSqlAgent.SqlCore.SqlParsing;

internal sealed class CoreTokenReader(Token[] tokens)
{
    private readonly Token[] _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private int _position;

    public int Position => _position;

    public Token Peek(int offset = 0)
    {
        var index = _position + offset;
        if (index < 0) index = 0;
        return index < _tokens.Length ? _tokens[index] : _tokens[^1];
    }

    public Token Advance()
    {
        var token = Peek();
        if (_position < _tokens.Length) _position++;
        return token;
    }

    public bool PeekWord(string value) => IsWord(Peek(), value);
    public bool PeekWord(int offset, string value) => IsWord(Peek(offset), value);

    public bool MatchWord(string value)
    {
        if (!PeekWord(value)) return false;
        Advance();
        return true;
    }

    public Token ExpectWord(string value)
    {
        var token = Peek();
        if (!IsWord(token, value))
            throw Error($"Expected keyword '{value}' but got '{token.Value}'.", token);
        return Advance();
    }

    public bool Match(TokenType type)
    {
        if (Peek().Type != type) return false;
        Advance();
        return true;
    }

    public Token Expect(TokenType type, string? description = null)
    {
        var token = Peek();
        if (token.Type != type)
            throw Error($"Expected {description ?? type.ToString()} but got {token.Type} ('{token.Value}').", token);
        return Advance();
    }

    public Token ExpectIdentifier(string description)
    {
        var token = Peek();
        if (token.Type != TokenType.Identifier)
            throw Error($"Expected {description} but got {token.Type} ('{token.Value}').", token);
        return Advance();
    }

    public SourceSpan SpanFrom(int startPosition)
    {
        if (startPosition < 0 || startPosition >= _tokens.Length)
            return SourceSpan.Unknown;
        var first = _tokens[startPosition];
        var lastIndex = Math.Clamp(_position - 1, startPosition, _tokens.Length - 1);
        var last = _tokens[lastIndex];
        return new SourceSpan(first.Pos, Math.Max(first.End, last.End));
    }

    public SqlIdentifier ParseIdentifierPath(string description, bool allowStarTail = false)
    {
        var start = Position;
        var parts = ImmutableArray.CreateBuilder<IdentifierPart>();
        var first = ExpectIdentifier(description);
        parts.Add(ToIdentifierPart(first));

        while (Match(TokenType.Dot))
        {
            if (allowStarTail && Peek().Type == TokenType.Operator && Peek().Value == "*")
            {
                var star = Advance();
                parts.Add(new IdentifierPart("*", false, Span(star)));
                break;
            }
            parts.Add(ToIdentifierPart(ExpectIdentifier(description)));
        }
        return new SqlIdentifier(parts.ToImmutable(), SpanFrom(start));
    }

    public static bool IsWord(Token token, string value) =>
        (token.Type == TokenType.Keyword
            || (token.Type == TokenType.Identifier && !IsQuotedIdentifier(token)))
        && token.Value.Equals(value, StringComparison.OrdinalIgnoreCase);

    public static SourceSpan Span(Token token) => new(token.Pos, token.End);

    public static bool IsQuotedIdentifier(Token token) =>
        token.Type == TokenType.Identifier && token.Length > token.Value.Length;

    public static IdentifierPart ToIdentifierPart(Token token) =>
        new(token.Value, IsQuotedIdentifier(token), Span(token));

    public static SqlParseException Error(string message, Token token) =>
        new($"{message} Position {token.Pos}, span [{token.Pos}..{Math.Max(token.End, token.Pos + 1)}).");
}
