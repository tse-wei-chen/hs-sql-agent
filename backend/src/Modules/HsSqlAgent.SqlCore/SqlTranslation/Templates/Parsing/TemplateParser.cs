using SqlAgent.Service.Enums;
using SqlAgent.Service.SqlParsing;
using SqlAgent.Service.SqlTranslation.Templates.Ast;

namespace SqlAgent.Service.SqlTranslation.Templates.Parsing;

internal sealed class TemplateParser
{
    private readonly string _template;
    private readonly IReadOnlyList<TemplateToken> _tokens;
    private int _position;

    internal TemplateParser(string template)
    {
        _template = template.Trim();
        _tokens = string.IsNullOrWhiteSpace(_template) ? [] : new TemplateLexer(_template).Lex();
    }

    internal TemplateExpression? Parse()
    {
        if (_tokens.Count == 0) return null;
        _position = 0;
        var result = ParseOr();
        if (Current.Kind != TemplateTokenKind.End)
            throw Error($"Unexpected trailing token '{Current.Lexeme}'");
        return result;
    }

    private TemplateExpression ParseOr()
    {
        var left = ParseAnd();
        while (MatchKeyword("OR")) left = Operation(left, ArithmeticOperator.Or, ParseAnd());
        return left;
    }
    private TemplateExpression ParseAnd()
    {
        var left = ParseComparison();
        while (MatchKeyword("AND")) left = Operation(left, ArithmeticOperator.And, ParseComparison());
        return left;
    }
    private TemplateExpression ParseComparison()
    {
        var left = ParseConcat();
        while (TryComparisonOperator(out var operation)) left = Operation(left, operation, ParseConcat());
        return left;
    }
    private bool TryComparisonOperator(out ArithmeticOperator operation)
    {
        operation = Current.Kind switch
        {
            TemplateTokenKind.Equal => ArithmeticOperator.Equal,
            TemplateTokenKind.NotEqual => ArithmeticOperator.NotEqual,
            TemplateTokenKind.GreaterThan => ArithmeticOperator.GreaterThan,
            TemplateTokenKind.GreaterThanOrEqual => ArithmeticOperator.GreaterThanOrEqual,
            TemplateTokenKind.LessThan => ArithmeticOperator.LessThan,
            TemplateTokenKind.LessThanOrEqual => ArithmeticOperator.LessThanOrEqual,
            _ => default
        };
        if (Current.Kind is not (TemplateTokenKind.Equal or TemplateTokenKind.NotEqual
            or TemplateTokenKind.GreaterThan or TemplateTokenKind.GreaterThanOrEqual
            or TemplateTokenKind.LessThan or TemplateTokenKind.LessThanOrEqual)) return false;
        Advance();
        return true;
    }
    private TemplateExpression ParseConcat()
    {
        var left = ParseAdditive();
        while (Match(TemplateTokenKind.Concat)) left = Operation(left, ArithmeticOperator.Concat, ParseAdditive());
        return left;
    }
    private TemplateExpression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Kind is TemplateTokenKind.Plus or TemplateTokenKind.Minus)
        {
            var operation = Advance().Kind == TemplateTokenKind.Plus ? ArithmeticOperator.Add : ArithmeticOperator.Subtract;
            left = Operation(left, operation, ParseMultiplicative());
        }
        return left;
    }
    private TemplateExpression ParseMultiplicative()
    {
        var left = ParsePrimary();
        while (Current.Kind is TemplateTokenKind.Star or TemplateTokenKind.Slash or TemplateTokenKind.Percent)
        {
            var operation = Advance().Kind switch
            {
                TemplateTokenKind.Star => ArithmeticOperator.Multiply,
                TemplateTokenKind.Slash => ArithmeticOperator.Divide,
                _ => ArithmeticOperator.Modulo
            };
            left = Operation(left, operation, ParsePrimary());
        }
        return left;
    }

    private TemplateExpression ParsePrimary()
    {
        if (Match(TemplateTokenKind.Plus)) return ParsePrimary();
        if (Match(TemplateTokenKind.Minus))
        {
            if (Current.Kind == TemplateTokenKind.Number) return ParseNumber("-" + Advance().Value);
            return Operation(new TemplateConstantExpression(0), ArithmeticOperator.Subtract, ParsePrimary());
        }
        if (Current.Kind == TemplateTokenKind.ArgumentReference) return ParseArgumentReference();
        if (Current.Kind == TemplateTokenKind.SqlToken) return new TemplateSqlTokenExpression(Advance().Value);
        if (Current.Kind == TemplateTokenKind.String) return new TemplateConstantExpression(Advance().Value);
        if (Current.Kind == TemplateTokenKind.Number) return ParseNumber(Advance().Value);
        if (Match(TemplateTokenKind.LeftParen))
        {
            var expression = ParseOr();
            Expect(TemplateTokenKind.RightParen, "')'");
            return expression;
        }
        if (Current.Kind == TemplateTokenKind.Identifier) return ParseIdentifierExpression();
        throw Error($"Unexpected token '{Current.Lexeme}'");
    }

    private TemplateExpression ParseArgumentReference()
    {
        var reference = Advance();
        if (!int.TryParse(reference.Value, out var oneBasedIndex) || oneBasedIndex <= 0)
            throw Error("Template argument reference must be $1 or greater", reference);
        string? modifier = null;
        IReadOnlyList<TemplateExpression> modifierArguments = [];
        if (Match(TemplateTokenKind.Colon))
        {
            modifier = Expect(TemplateTokenKind.Identifier, "modifier name").Value;
            if (Match(TemplateTokenKind.LeftParen))
            {
                modifierArguments = ParseArguments();
                Expect(TemplateTokenKind.RightParen, "')'");
            }
        }
        return new TemplateArgumentReferenceExpression(oneBasedIndex - 1, modifier, modifierArguments);
    }

    private TemplateExpression ParseIdentifierExpression()
    {
        var identifier = Advance();
        var name = identifier.Value;
        if (name.Equals("CASE", StringComparison.OrdinalIgnoreCase)) return ParseCase();
        if (name.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase))
            return new TemplateIntervalExpression(Expect(TemplateTokenKind.String, "interval string literal").Value);
        if (Current.Kind == TemplateTokenKind.String
            && (name.Equals("DATE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TIME", StringComparison.OrdinalIgnoreCase)
                || name.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase)))
            return ParseTemporalLiteral(name, Expect(TemplateTokenKind.String, $"{name} string literal").Value);
        if (!Match(TemplateTokenKind.LeftParen))
            throw Error($"Bare identifier '{name}' is not allowed; use @Token for SQL grammar tokens", identifier);
        if (name.Equals("CAST", StringComparison.OrdinalIgnoreCase)) return ParseCast();
        if (name.Equals("EXTRACT", StringComparison.OrdinalIgnoreCase)) return ParseExtract();
        var arguments = ParseArguments();
        Expect(TemplateTokenKind.RightParen, "')'");
        return new TemplateFunctionExpression(name, arguments);
    }

    private List<TemplateExpression> ParseArguments()
    {
        var arguments = new List<TemplateExpression>();
        if (Current.Kind == TemplateTokenKind.RightParen) return arguments;
        arguments.Add(ParseOr());
        while (Match(TemplateTokenKind.Comma)) arguments.Add(ParseOr());
        return arguments;
    }

    private TemplateCastExpression ParseCast()
    {
        var expression = ParseOr();
        ExpectKeyword("AS");
        var typeTokens = new List<TemplateToken>();
        var depth = 0;
        while (Current.Kind != TemplateTokenKind.End)
        {
            if (Current.Kind == TemplateTokenKind.RightParen && depth == 0) break;
            var token = Advance();
            if (token.Kind == TemplateTokenKind.LeftParen) depth++;
            else if (token.Kind == TemplateTokenKind.RightParen) depth--;
            typeTokens.Add(token);
        }
        if (typeTokens.Count == 0 || depth != 0) throw Error("Invalid or unterminated CAST type");
        Expect(TemplateTokenKind.RightParen, "')'");
        return new TemplateCastExpression(expression, JoinTypeTokens(typeTokens));
    }

    private TemplateExtractExpression ParseExtract()
    {
        if (Current.Kind != TemplateTokenKind.SqlToken) throw Error("EXTRACT unit must use a controlled @Token");
        var unit = new TemplateSqlTokenExpression(Advance().Value);
        ExpectKeyword("FROM");
        var expression = ParseOr();
        Expect(TemplateTokenKind.RightParen, "')'");
        return new TemplateExtractExpression(unit, expression);
    }

    private TemplateCaseExpression ParseCase()
    {
        var cases = new List<TemplateCaseBranch>();
        while (MatchKeyword("WHEN"))
        {
            var condition = ParseOr();
            ExpectKeyword("THEN");
            var value = ParseOr();
            cases.Add(new TemplateCaseBranch(condition, value));
        }
        if (cases.Count == 0) throw Error("CASE requires at least one WHEN branch");
        var elseExpression = MatchKeyword("ELSE") ? ParseOr() : null;
        ExpectKeyword("END");
        return new TemplateCaseExpression(cases, elseExpression);
    }

    private static TemplateOperationExpression Operation(TemplateExpression left, ArithmeticOperator operation, TemplateExpression right) =>
        new(left, operation, right);
    private static TemplateConstantExpression ParseNumber(string value) =>
        new(int.TryParse(value, out var integer) ? integer : value);
    private static TemplateExpression ParseTemporalLiteral(string typeName, string text)
    {
        if (typeName.Equals("DATE", StringComparison.OrdinalIgnoreCase) && SqlTemporalLiteralParser.TryParseDate(text, out var date))
            return new TemplateConstantExpression(date);
        if (typeName.Equals("TIME", StringComparison.OrdinalIgnoreCase) && SqlTemporalLiteralParser.TryParseTime(text, out var time))
            return new TemplateConstantExpression(time);
        if (typeName.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase) && SqlTemporalLiteralParser.TryParseTimestamp(text, out var timestamp))
            return new TemplateConstantExpression(timestamp);
        throw new FormatException($"Invalid {typeName.ToUpperInvariant()} literal '{text}' in function template.");
    }
    private static string JoinTypeTokens(IReadOnlyList<TemplateToken> tokens)
    {
        var builder = new System.Text.StringBuilder();
        TemplateToken? previous = null;
        foreach (var token in tokens)
        {
            if (previous?.Kind == TemplateTokenKind.Identifier && token.Kind == TemplateTokenKind.Identifier) builder.Append(' ');
            builder.Append(token.Lexeme);
            previous = token;
        }
        return builder.ToString();
    }
    private bool MatchKeyword(string keyword)
    {
        if (Current.Kind != TemplateTokenKind.Identifier || !Current.Value.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        Advance();
        return true;
    }
    private void ExpectKeyword(string keyword) { if (!MatchKeyword(keyword)) throw Error($"Expected keyword {keyword}"); }
    private bool Match(TemplateTokenKind kind) { if (Current.Kind != kind) return false; Advance(); return true; }
    private TemplateToken Expect(TemplateTokenKind kind, string description)
    { if (Current.Kind != kind) throw Error($"Expected {description}"); return Advance(); }
    private TemplateToken Current => _tokens[_position];
    private TemplateToken Advance() => _tokens[_position++];
    private FormatException Error(string message, TemplateToken? token = null) =>
        new($"{message} at position {(token ?? Current).Position} in template: {_template}");

}
