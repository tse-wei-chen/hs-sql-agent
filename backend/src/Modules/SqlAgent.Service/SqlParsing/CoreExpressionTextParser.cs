using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.SqlParsing;

internal sealed class CoreExpressionTextParser(
    CoreTokenReader reader,
    Func<SqlStatement> parseSubquery)
{
    internal const string MySqlPipesConcatToken = "__CORE_MYSQL_PIPES_CONCAT_TOKEN__";

    private readonly CoreTokenReader _reader = reader;
    private readonly Func<SqlStatement> _parseSubquery = parseSubquery;

    public SqlExpr ParseExpression() => ParseOr();

    private SqlExpr ParseOr()
    {
        var start = _reader.Position;
        var left = ParseAnd();
        while (_reader.MatchWord("OR"))
            left = new BinaryExpr(left, "OR", ParseAnd(), _reader.SpanFrom(start));
        return left;
    }

    private SqlExpr ParseAnd()
    {
        var start = _reader.Position;
        var left = ParseNot();
        while (_reader.MatchWord("AND"))
            left = new BinaryExpr(left, "AND", ParseNot(), _reader.SpanFrom(start));
        return left;
    }

    private SqlExpr ParseNot()
    {
        if (!_reader.PeekWord("NOT")) return ParsePredicate();
        var start = _reader.Position;
        _reader.Advance();
        return new UnaryExpr("NOT", ParseNot(), _reader.SpanFrom(start));
    }

    private SqlExpr ParsePredicate()
    {
        var start = _reader.Position;
        var left = ParseAdditive();
        var token = _reader.Peek();
        if (token.Type == TokenType.Operator && IsComparisonOperator(token.Value))
        {
            var raw = _reader.Advance().Value;
            var right = ParseAdditive();
            return new BinaryExpr(left, raw == "!=" ? "<>" : raw, right, _reader.SpanFrom(start));
        }

        if (_reader.MatchWord("IS"))
        {
            var negated = _reader.MatchWord("NOT");
            if (!_reader.MatchWord("NULL"))
                throw CoreTokenReader.Error("Core predicates currently support IS [NOT] NULL only.", _reader.Peek());
            return new IsNullExpr(left, negated, _reader.SpanFrom(start));
        }

        var negatedModifier = false;
        if (_reader.PeekWord("NOT")
            && (_reader.PeekWord(1, "IN")
                || _reader.PeekWord(1, "BETWEEN")
                || _reader.PeekWord(1, "LIKE")
                || _reader.PeekWord(1, "ILIKE")))
        {
            _reader.Advance();
            negatedModifier = true;
        }

        if (_reader.MatchWord("IN"))
        {
            _reader.Expect(TokenType.LParen, "'(' after IN");
            if (_reader.PeekWord("SELECT") || _reader.PeekWord("WITH"))
            {
                var query = _parseSubquery();
                _reader.Expect(TokenType.RParen, "')' after IN subquery");
                return new BinaryExpr(
                    left,
                    negatedModifier ? "NOT IN" : "IN",
                    new SubqueryExpr(query, query.Span),
                    _reader.SpanFrom(start));
            }

            if (_reader.Peek().Type == TokenType.RParen)
                throw CoreTokenReader.Error("IN expression list cannot be empty.", _reader.Peek());
            var items = ImmutableArray.CreateBuilder<SqlExpr>();
            do items.Add(ParseExpression());
            while (_reader.Match(TokenType.Comma));
            _reader.Expect(TokenType.RParen, "')' after IN expression list");
            return new InExpr(left, items.ToImmutable(), negatedModifier, _reader.SpanFrom(start));
        }

        if (_reader.MatchWord("BETWEEN"))
        {
            var lower = ParseAdditive();
            _reader.ExpectWord("AND");
            var upper = ParseAdditive();
            return new BetweenExpr(left, lower, upper, negatedModifier, _reader.SpanFrom(start));
        }

        if (_reader.MatchWord("LIKE") || _reader.MatchWord("ILIKE"))
        {
            var op = _reader.Peek(-1).Value.ToUpperInvariant();
            var right = ParseAdditive();
            var binary = new BinaryExpr(left, op, right, _reader.SpanFrom(start));
            return negatedModifier
                ? new UnaryExpr("NOT", binary, _reader.SpanFrom(start))
                : binary;
        }

        if (negatedModifier)
            throw CoreTokenReader.Error("NOT must be followed by IN, BETWEEN, LIKE, or ILIKE in this predicate position.", _reader.Peek());
        return left;
    }

    private SqlExpr ParseAdditive()
    {
        var start = _reader.Position;
        var left = ParseMultiplicative();
        while (_reader.Peek().Type == TokenType.Operator && _reader.Peek().Value is "+" or "-" or "||")
        {
            var op = _reader.Advance().Value;
            left = new BinaryExpr(left, op, ParseMultiplicative(), _reader.SpanFrom(start));
        }
        return left;
    }

    private SqlExpr ParseMultiplicative()
    {
        var start = _reader.Position;
        var left = ParseProfiledConcat();
        while (_reader.Peek().Type == TokenType.Operator && _reader.Peek().Value is "*" or "/" or "%")
        {
            var op = _reader.Advance().Value;
            left = new BinaryExpr(left, op, ParseProfiledConcat(), _reader.SpanFrom(start));
        }
        return left;
    }

    private SqlExpr ParseProfiledConcat()
    {
        var start = _reader.Position;
        var left = ParsePostfix();
        while (_reader.Peek().Type == TokenType.Operator
               && _reader.Peek().Value == MySqlPipesConcatToken)
        {
            _reader.Advance();
            left = new BinaryExpr(left, "||", ParsePostfix(), _reader.SpanFrom(start));
        }
        return left;
    }

    private SqlExpr ParsePostfix()
    {
        var start = _reader.Position;
        var expression = ParseUnaryNumeric();
        while (_reader.Peek().Type == TokenType.Operator && _reader.Peek().Value == "::")
        {
            _reader.Advance();
            expression = new CastExpr(expression, ParseCastTypeName(), _reader.SpanFrom(start));
        }
        return expression;
    }

    private SqlExpr ParseUnaryNumeric()
    {
        if (_reader.Peek().Type != TokenType.Operator || _reader.Peek().Value is not ("+" or "-"))
            return ParsePrimary();
        var start = _reader.Position;
        var sign = _reader.Advance();
        var token = _reader.Peek();
        if (token.Type != TokenType.Number)
        {
            throw CoreTokenReader.Error(
                $"Unary '{sign.Value}' is accepted only for numeric literals; general unary arithmetic is not represented by the Core lowerer.",
                token);
        }
        var value = ParseNumber(_reader.Advance().Value);
        if (sign.Value == "-")
        {
            value = value switch
            {
                int integer => -integer,
                decimal number => -number,
                _ => throw CoreTokenReader.Error("Unsupported signed numeric literal.", sign)
            };
        }
        return new LiteralExpr(value, _reader.SpanFrom(start));
    }

    private SqlExpr ParsePrimary()
    {
        var start = _reader.Position;
        var token = _reader.Peek();

        if (_reader.MatchWord("CASE")) return ParseCase(start);
        if (_reader.MatchWord("CAST")) return ParseCast(start);
        if (_reader.MatchWord("EXISTS"))
        {
            _reader.Expect(TokenType.LParen, "'(' after EXISTS");
            var query = _parseSubquery();
            _reader.Expect(TokenType.RParen, "')' after EXISTS subquery");
            return new ExistsExpr(query, false, _reader.SpanFrom(start));
        }
        if (_reader.MatchWord("NULL")) return new LiteralExpr(null, _reader.SpanFrom(start));
        if (_reader.MatchWord("TRUE")) return new LiteralExpr(true, _reader.SpanFrom(start));
        if (_reader.MatchWord("FALSE")) return new LiteralExpr(false, _reader.SpanFrom(start));

        if (_reader.Match(TokenType.LParen))
        {
            if (_reader.PeekWord("SELECT") || _reader.PeekWord("WITH"))
            {
                var query = _parseSubquery();
                _reader.Expect(TokenType.RParen, "')' after scalar subquery");
                return new SubqueryExpr(query, _reader.SpanFrom(start));
            }
            var inner = ParseExpression();
            _reader.Expect(TokenType.RParen, "')' after expression");
            return inner with { Span = _reader.SpanFrom(start) };
        }

        if (token.Type == TokenType.Number)
        {
            _reader.Advance();
            return new LiteralExpr(ParseNumber(token.Value), _reader.SpanFrom(start));
        }
        if (token.Type == TokenType.String)
        {
            _reader.Advance();
            return new LiteralExpr(DecodeString(token.Value), _reader.SpanFrom(start));
        }
        if (token.Type == TokenType.Parameter)
            throw CoreTokenReader.Error($"Unbound SQL parameter '{token.Value}'.", token);

        if (IsTemporalLiteralStart(token))
            return ParseTemporalLiteral(start);

        if (_reader.PeekWord("INTERVAL") && _reader.Peek(1).Type == TokenType.String)
        {
            _reader.Advance();
            return new IntervalExpr(DecodeString(_reader.Advance().Value), _reader.SpanFrom(start));
        }

        if (_reader.PeekWord("CURRENT_DATE")
            || _reader.PeekWord("CURRENT_TIME")
            || _reader.PeekWord("CURRENT_TIMESTAMP"))
        {
            var nameToken = _reader.Advance();
            if (_reader.Match(TokenType.LParen))
                _reader.Expect(TokenType.RParen, "')' after current temporal function");
            return new FunctionCallExpr(
                IdentifierFromToken(nameToken),
                ImmutableArray<SqlExpr>.Empty,
                false,
                _reader.SpanFrom(start));
        }

        if (_reader.PeekWord("EXTRACT") && _reader.Peek(1).Type == TokenType.LParen)
            return ParseExtract(start);

        if ((token.Type is TokenType.Identifier or TokenType.Keyword) && _reader.Peek(1).Type == TokenType.LParen)
            return ParseFunction(start);

        if (token.Type == TokenType.Operator && token.Value == "*")
        {
            _reader.Advance();
            return new ColumnExpr(
                new SqlIdentifier(
                    [new IdentifierPart("*", false, CoreTokenReader.Span(token))],
                    CoreTokenReader.Span(token)),
                _reader.SpanFrom(start));
        }

        if (token.Type == TokenType.Identifier)
        {
            var identifier = _reader.ParseIdentifierPath("column identifier", allowStarTail: true);
            return new ColumnExpr(identifier, _reader.SpanFrom(start));
        }

        throw CoreTokenReader.Error($"Unexpected token '{token.Value}' in SQL expression.", token);
    }

    private SqlExpr ParseExtract(int start)
    {
        _reader.ExpectWord("EXTRACT");
        _reader.Expect(TokenType.LParen, "'(' after EXTRACT");
        var partToken = _reader.Peek();
        if (partToken.Type is not (TokenType.Identifier or TokenType.Keyword))
            throw CoreTokenReader.Error("EXTRACT requires a date-part keyword.", partToken);
        var part = _reader.Advance().Value.ToUpperInvariant();
        _reader.ExpectWord("FROM");
        var value = ParseExpression();
        _reader.Expect(TokenType.RParen, "')' after EXTRACT expression");

        // YEAR/MONTH/DAY already have canonical portable semantics in CoreSqlNormalizer. Other
        // units remain fail-closed until the canonical date-part family accepts them directly.
        if (part is not ("YEAR" or "MONTH" or "DAY"))
            throw CoreTokenReader.Error($"EXTRACT({part} ...) is not yet represented by the canonical date-part family.", partToken);

        return new FunctionCallExpr(
            Identifier(part, CoreTokenReader.Span(partToken)),
            [value],
            false,
            _reader.SpanFrom(start));
    }

    private SqlExpr ParseFunction(int start)
    {
        var nameToken = _reader.Advance();
        var name = IdentifierFromToken(nameToken);
        _reader.Expect(TokenType.LParen, "'(' after function name");
        var distinct = _reader.MatchWord("DISTINCT");
        if (!distinct) _reader.MatchWord("ALL");
        var arguments = ImmutableArray.CreateBuilder<SqlExpr>();

        if (_reader.Peek().Type == TokenType.Operator && _reader.Peek().Value == "*")
        {
            var star = _reader.Advance();
            arguments.Add(new ColumnExpr(
                new SqlIdentifier(
                    [new IdentifierPart("*", false, CoreTokenReader.Span(star))],
                    CoreTokenReader.Span(star)),
                CoreTokenReader.Span(star)));
        }
        else if (_reader.Peek().Type != TokenType.RParen)
        {
            do arguments.Add(ParseExpression());
            while (_reader.Match(TokenType.Comma));
        }
        _reader.Expect(TokenType.RParen, "')' after function arguments");

        SqlExpr result = new FunctionCallExpr(name, arguments.ToImmutable(), distinct, _reader.SpanFrom(start));
        if (_reader.MatchWord("FILTER"))
        {
            _reader.Expect(TokenType.LParen, "'(' after FILTER");
            _reader.ExpectWord("WHERE");
            var predicate = ParseExpression();
            _reader.Expect(TokenType.RParen, "')' after FILTER predicate");
            result = new FilterExpr(result, predicate, _reader.SpanFrom(start));
        }
        if (_reader.MatchWord("OVER"))
            result = new WindowedExpr(result, ParseWindowSpec(), _reader.SpanFrom(start));
        return result with { Span = _reader.SpanFrom(start) };
    }

    private WindowSpec ParseWindowSpec()
    {
        var start = _reader.Position;
        _reader.Expect(TokenType.LParen, "'(' after OVER");
        var partitionBy = ImmutableArray<SqlExpr>.Empty;
        if (_reader.MatchWord("PARTITION"))
        {
            _reader.ExpectWord("BY");
            var parts = ImmutableArray.CreateBuilder<SqlExpr>();
            do parts.Add(ParseExpression());
            while (_reader.Match(TokenType.Comma));
            partitionBy = parts.ToImmutable();
        }

        var orderBy = ImmutableArray<OrderByItem>.Empty;
        if (_reader.MatchWord("ORDER"))
        {
            _reader.ExpectWord("BY");
            var items = ImmutableArray.CreateBuilder<OrderByItem>();
            do
            {
                var orderStart = _reader.Position;
                var expression = ParseExpression();
                var descending = false;
                if (_reader.MatchWord("DESC")) descending = true;
                else _reader.MatchWord("ASC");
                var nullOrdering = NullOrderingKind.Default;
                if (_reader.MatchWord("NULLS"))
                {
                    if (_reader.MatchWord("FIRST")) nullOrdering = NullOrderingKind.First;
                    else if (_reader.MatchWord("LAST")) nullOrdering = NullOrderingKind.Last;
                    else throw CoreTokenReader.Error("Expected FIRST or LAST after NULLS.", _reader.Peek());
                }
                items.Add(new OrderByItem(expression, descending, nullOrdering, _reader.SpanFrom(orderStart)));
            } while (_reader.Match(TokenType.Comma));
            orderBy = items.ToImmutable();
        }

        WindowFrame? frame = null;
        if (_reader.PeekWord("ROWS") || _reader.PeekWord("RANGE")) frame = ParseWindowFrame();
        _reader.Expect(TokenType.RParen, "')' after window specification");
        return new WindowSpec(partitionBy, orderBy, frame, _reader.SpanFrom(start));
    }

    private WindowFrame ParseWindowFrame()
    {
        var start = _reader.Position;
        var unitToken = _reader.Advance();
        var unit = CoreTokenReader.IsWord(unitToken, "ROWS") ? WindowFrameUnitKind.Rows : WindowFrameUnitKind.Range;
        WindowFrameBoundCore first;
        WindowFrameBoundCore? second = null;
        if (_reader.MatchWord("BETWEEN"))
        {
            first = ParseWindowBound();
            _reader.ExpectWord("AND");
            second = ParseWindowBound();
        }
        else first = ParseWindowBound();
        return new WindowFrame(unit, first, second, _reader.SpanFrom(start));
    }

    private WindowFrameBoundCore ParseWindowBound()
    {
        var start = _reader.Position;
        if (_reader.MatchWord("UNBOUNDED"))
        {
            if (_reader.MatchWord("PRECEDING"))
                return new WindowFrameBoundCore(WindowFrameBoundKindCore.UnboundedPreceding, null, _reader.SpanFrom(start));
            _reader.ExpectWord("FOLLOWING");
            return new WindowFrameBoundCore(WindowFrameBoundKindCore.UnboundedFollowing, null, _reader.SpanFrom(start));
        }
        if (_reader.MatchWord("CURRENT"))
        {
            _reader.ExpectWord("ROW");
            return new WindowFrameBoundCore(WindowFrameBoundKindCore.CurrentRow, null, _reader.SpanFrom(start));
        }
        var token = _reader.Expect(TokenType.Number, "window frame offset");
        if (!int.TryParse(token.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
            throw CoreTokenReader.Error("Window frame offset must be a non-negative integer.", token);
        if (_reader.MatchWord("PRECEDING"))
            return new WindowFrameBoundCore(WindowFrameBoundKindCore.Preceding, offset, _reader.SpanFrom(start));
        _reader.ExpectWord("FOLLOWING");
        return new WindowFrameBoundCore(WindowFrameBoundKindCore.Following, offset, _reader.SpanFrom(start));
    }

    private SqlExpr ParseCase(int start)
    {
        SqlExpr? caseValue = null;
        if (!_reader.PeekWord("WHEN"))
            caseValue = ParseExpression();

        var branches = ImmutableArray.CreateBuilder<CaseBranch>();
        while (_reader.MatchWord("WHEN"))
        {
            var when = ParseExpression();
            var condition = caseValue is null
                ? when
                : new BinaryExpr(caseValue, "=", when, new SourceSpan(caseValue.Span.Start, when.Span.End));
            _reader.ExpectWord("THEN");
            branches.Add(new CaseBranch(condition, ParseExpression()));
        }
        if (branches.Count == 0)
            throw CoreTokenReader.Error("CASE requires at least one WHEN branch.", _reader.Peek());
        SqlExpr? otherwise = null;
        if (_reader.MatchWord("ELSE")) otherwise = ParseExpression();
        _reader.ExpectWord("END");
        var result = branches.ToImmutable();
        return caseValue is null
            ? new CaseExpr(result, otherwise, _reader.SpanFrom(start))
            : new SimpleCaseExpr(result, otherwise, _reader.SpanFrom(start));
    }

    private SqlExpr ParseCast(int start)
    {
        _reader.Expect(TokenType.LParen, "'(' after CAST");
        var expression = ParseExpression();
        _reader.ExpectWord("AS");
        var typeName = ParseCastTypeName();
        _reader.Expect(TokenType.RParen, "')' after CAST");
        return new CastExpr(expression, typeName, _reader.SpanFrom(start));
    }

    private string ParseCastTypeName()
    {
        var parts = new List<string>();
        var token = _reader.Peek();
        if (token.Type is not (TokenType.Identifier or TokenType.Keyword))
            throw CoreTokenReader.Error("Expected cast type.", token);
        parts.Add(_reader.Advance().Value);
        while (_reader.Match(TokenType.Dot))
        {
            var component = _reader.Peek();
            if (component.Type is not (TokenType.Identifier or TokenType.Keyword))
                throw CoreTokenReader.Error("Expected cast type component.", component);
            parts[^1] += "." + _reader.Advance().Value;
        }
        while ((_reader.Peek().Type is TokenType.Identifier or TokenType.Keyword)
               && IsCastTypeQualifier(_reader.Peek().Value))
            parts.Add(_reader.Advance().Value);

        if (_reader.Match(TokenType.LParen))
        {
            var suffix = new StringBuilder("(");
            var first = _reader.Peek();
            var isMax = (first.Type is TokenType.Identifier or TokenType.Keyword)
                        && first.Value.Equals("MAX", StringComparison.OrdinalIgnoreCase);
            if (isMax)
            {
                _reader.Advance();
                suffix.Append("MAX");
            }
            else
            {
                first = _reader.Expect(TokenType.Number, "cast type precision or MAX");
                if (!int.TryParse(first.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw CoreTokenReader.Error("Cast type precision must be an integer or MAX.", first);
                suffix.Append(first.Value);
            }

            if (_reader.Match(TokenType.Comma))
            {
                if (isMax)
                    throw CoreTokenReader.Error("Cast type MAX does not accept a scale.", _reader.Peek(-1));
                var second = _reader.Expect(TokenType.Number, "cast type scale");
                if (!int.TryParse(second.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw CoreTokenReader.Error("Cast type scale must be an integer.", second);
                suffix.Append(',').Append(second.Value);
            }
            _reader.Expect(TokenType.RParen, "')' after cast type precision");
            suffix.Append(')');
            parts[^1] += suffix;
        }

        // Standard temporal types put WITH/WITHOUT TIME ZONE after the precision, e.g.
        // TIMESTAMP(6) WITH TIME ZONE. Retain support for qualifier-before-precision spellings too.
        while ((_reader.Peek().Type is TokenType.Identifier or TokenType.Keyword)
               && IsCastTypeQualifier(_reader.Peek().Value))
            parts.Add(_reader.Advance().Value);

        return string.Join(' ', parts);
    }

    private SqlExpr ParseTemporalLiteral(int start)
    {
        var typeToken = _reader.Advance();
        var type = typeToken.Value.ToUpperInvariant();
        bool? withTimeZone = null;
        if (type is "TIME" or "TIMESTAMP" && (_reader.PeekWord("WITH") || _reader.PeekWord("WITHOUT")))
        {
            if (_reader.MatchWord("WITH")) withTimeZone = true;
            else
            {
                _reader.ExpectWord("WITHOUT");
                withTimeZone = false;
            }
            _reader.ExpectWord("TIME");
            _reader.ExpectWord("ZONE");
        }
        var literalToken = _reader.Expect(TokenType.String, $"quoted {type} literal");
        var literal = DecodeString(literalToken.Value);
        if (type == "DATE" && SqlTemporalLiteralParser.TryParseDate(literal, out var date))
            return new LiteralExpr(date, _reader.SpanFrom(start));
        if (type == "TIME" && SqlTemporalLiteralParser.TryParseTime(literal, out var time))
        {
            if (withTimeZone == true)
                throw CoreTokenReader.Error("TIME WITH TIME ZONE is not represented by the canonical temporal model.", typeToken);
            return new LiteralExpr(time, _reader.SpanFrom(start));
        }
        if (type == "TIMESTAMP" && SqlTemporalLiteralParser.TryParseTimestamp(literal, out var timestamp))
        {
            if (withTimeZone == true && timestamp is not SqlOffsetDateTimeValue)
                throw CoreTokenReader.Error("TIMESTAMP WITH TIME ZONE requires an explicit UTC offset or Z suffix.", literalToken);
            if (withTimeZone == false && timestamp is SqlOffsetDateTimeValue)
                throw CoreTokenReader.Error("TIMESTAMP WITHOUT TIME ZONE must not include a UTC offset.", literalToken);
            return new LiteralExpr(timestamp, _reader.SpanFrom(start));
        }
        throw CoreTokenReader.Error($"Invalid {type} literal '{literal}'.", literalToken);
    }

    private bool IsTemporalLiteralStart(Token token)
    {
        if (CoreTokenReader.IsQuotedIdentifier(token) || !IsTemporalType(token.Value)) return false;
        if (_reader.Peek(1).Type == TokenType.String) return true;
        return token.Value is "TIME" or "TIMESTAMP"
            && (_reader.PeekWord(1, "WITH") || _reader.PeekWord(1, "WITHOUT"));
    }

    private static SqlIdentifier IdentifierFromToken(Token token) =>
        Identifier(token.Value, CoreTokenReader.Span(token), CoreTokenReader.IsQuotedIdentifier(token));

    private static SqlIdentifier Identifier(string value, SourceSpan span, bool wasQuoted = false) =>
        new([new IdentifierPart(value, wasQuoted, span)], span);

    private static bool IsTemporalType(string value) =>
        value.Equals("DATE", StringComparison.OrdinalIgnoreCase)
        || value.Equals("TIME", StringComparison.OrdinalIgnoreCase)
        || value.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase);

    private static bool IsCastTypeQualifier(string value) =>
        value.Equals("PRECISION", StringComparison.OrdinalIgnoreCase)
        || value.Equals("VARYING", StringComparison.OrdinalIgnoreCase)
        || value.Equals("WITH", StringComparison.OrdinalIgnoreCase)
        || value.Equals("WITHOUT", StringComparison.OrdinalIgnoreCase)
        || value.Equals("TIME", StringComparison.OrdinalIgnoreCase)
        || value.Equals("ZONE", StringComparison.OrdinalIgnoreCase)
        || value.Equals("SIGNED", StringComparison.OrdinalIgnoreCase)
        || value.Equals("UNSIGNED", StringComparison.OrdinalIgnoreCase);

    private static bool IsComparisonOperator(string value) =>
        value is "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=";

    internal static object ParseNumber(string value)
    {
        if (!value.Contains('.') && !value.Contains('e', StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer;
        return decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    internal static string DecodeString(string token) =>
        token[1..^1].Replace("''", "'", StringComparison.Ordinal);
}
