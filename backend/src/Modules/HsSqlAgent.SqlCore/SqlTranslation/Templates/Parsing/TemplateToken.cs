namespace HsSqlAgent.SqlCore.SqlTranslation.Templates.Parsing;

internal enum TemplateTokenKind
{
    End, Identifier, String, Number, ArgumentReference, SqlToken,
    LeftParen, RightParen, Comma, Colon,
    Plus, Minus, Star, Slash, Percent,
    Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Concat
}

internal sealed record TemplateToken(
    TemplateTokenKind Kind,
    string Value,
    string Lexeme,
    int Position);
