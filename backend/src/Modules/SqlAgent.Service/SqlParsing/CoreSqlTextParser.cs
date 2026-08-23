using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.SqlParsing;

/// <summary>
/// Parser-native entry point for raw SQL. Unlike <see cref="SqlDefinitionParser"/>, this parser
/// never creates the public transport DTO model: token positions and quoted-identifier intent are
/// preserved directly in the independent Core AST consumed by the compiler pipeline.
/// </summary>
public static class CoreSqlTextParser
{
    public static ParsedStatement ParseQuery(string sql, SqlAgentToolType sourceDialect)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = new SqlTokenizer(sql, sourceDialect).Tokenize();
        ValidateStatementTokens(tokens);
        var topLimit = NormalizeSqlServerTop(tokens, sourceDialect, out var normalizedTokens);
        normalizedTokens = CommaFromNormalizer.Normalize(normalizedTokens);
        var reader = new CoreTokenReader(normalizedTokens);
        var statement = new CoreQueryTextParser(reader).ParseComplete(topLimit);
        return new ParsedStatement(statement, sourceDialect);
    }

    public static ParsedStatement ParseDml(string sql, SqlAgentToolType sourceDialect)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokens = new SqlTokenizer(sql, sourceDialect).Tokenize();
        ValidateStatementTokens(tokens);
        var reader = new CoreTokenReader(tokens);
        var statement = new CoreDmlTextParser(reader).ParseComplete();
        return new ParsedStatement(statement, sourceDialect);
    }

    private static void ValidateStatementTokens(Token[] tokens)
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

            if (token.Type == TokenType.Semicolon && i != content.Length - 1)
            {
                throw new SqlParseException(
                    $"Only one SQL statement is allowed; unexpected semicolon at position {token.Pos}.");
            }
        }
    }

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
        token.Type is TokenType.Keyword or TokenType.Identifier
        && token.Value.Equals(value, StringComparison.OrdinalIgnoreCase);

    public static SourceSpan Span(Token token) => new(token.Pos, token.End);

    private static IdentifierPart ToIdentifierPart(Token token) =>
        new(token.Value, IsQuotedIdentifier(token), Span(token));

    private static bool IsQuotedIdentifier(Token token) =>
        token.Type == TokenType.Identifier && token.Length > token.Value.Length;

    public static SqlParseException Error(string message, Token token) =>
        new($"{message} Position {token.Pos}, span [{token.Pos}..{Math.Max(token.End, token.Pos + 1)}).");
}

internal sealed class CoreQueryTextParser
{
    private readonly CoreTokenReader _reader;
    private readonly CoreExpressionTextParser _expressions;

    public CoreQueryTextParser(CoreTokenReader reader)
    {
        _reader = reader;
        _expressions = new CoreExpressionTextParser(reader, ParseQueryExpression);
    }

    public SqlStatement ParseComplete(int? topLimit = null)
    {
        var statement = ParseQueryExpression(topLimit);
        _reader.Match(TokenType.Semicolon);
        if (_reader.Peek().Type != TokenType.EOF)
        {
            var token = _reader.Peek();
            throw CoreTokenReader.Error(
                $"Unexpected token '{token.Value}'; the complete query statement was not consumed.",
                token);
        }
        return statement;
    }

    public SqlStatement ParseQueryExpression() => ParseQueryExpression(null);

    private SqlStatement ParseQueryExpression(int? topLimit)
    {
        var start = _reader.Position;
        var ctes = ParseCtesIfPresent();
        var head = ParseSelectBody(ctes);
        var operations = ImmutableArray.CreateBuilder<SetOperation>();

        while (IsSetOperation(_reader.Peek()))
        {
            var operationStart = _reader.Position;
            var kind = ParseSetOperationKind();
            SqlStatement branch;
            if (_reader.Match(TokenType.LParen))
            {
                branch = ParseQueryExpression();
                _reader.Expect(TokenType.RParen, "')' after set-operation branch");
            }
            else
            {
                var branchCtes = ParseCtesIfPresent();
                branch = ParseSelectBody(branchCtes);
            }
            operations.Add(new SetOperation(kind, branch, _reader.SpanFrom(operationStart)));
        }

        var orderBy = ParseOrderByIfPresent();
        var (limit, offset) = ParseLimitOffsetIfPresent();

        if (topLimit is not null)
        {
            if (operations.Count > 0)
                throw new SqlParseException("SQL Server TOP with set operations is not represented losslessly by the Core AST.");
            if (limit is not null)
                throw new SqlParseException("SQL Server TOP cannot be combined with LIMIT in the canonical query AST.");
            limit = topLimit;
        }

        if (operations.Count == 0)
        {
            return head with
            {
                OrderBy = orderBy,
                Limit = limit,
                Offset = offset,
                Span = _reader.SpanFrom(start)
            };
        }

        return new QueryStatement(
            head,
            operations.ToImmutable(),
            orderBy,
            limit,
            offset,
            _reader.SpanFrom(start));
    }

    private ImmutableArray<CteDefinition> ParseCtesIfPresent()
    {
        if (!_reader.MatchWord("WITH"))
            return ImmutableArray<CteDefinition>.Empty;

        if (_reader.MatchWord("RECURSIVE"))
        {
            throw CoreTokenReader.Error(
                "WITH RECURSIVE is not yet represented by the Core AST and is rejected rather than downgraded to non-recursive CTE semantics.",
                _reader.Peek(-1));
        }

        var result = ImmutableArray.CreateBuilder<CteDefinition>();
        do
        {
            var start = _reader.Position;
            var name = ParseSingleIdentifier("CTE name");
            var columnAliases = ImmutableArray.CreateBuilder<SqlIdentifier>();
            if (_reader.Match(TokenType.LParen))
            {
                if (_reader.Peek().Type == TokenType.RParen)
                    throw CoreTokenReader.Error("CTE column alias list cannot be empty.", _reader.Peek());
                do
                    columnAliases.Add(ParseSingleIdentifier("CTE column alias"));
                while (_reader.Match(TokenType.Comma));
                _reader.Expect(TokenType.RParen, "')' after CTE column aliases");
            }

            _reader.ExpectWord("AS");
            _reader.Expect(TokenType.LParen, "'(' before CTE query");
            var query = ParseQueryExpression();
            _reader.Expect(TokenType.RParen, "')' after CTE query");
            result.Add(new CteDefinition(
                name,
                columnAliases.ToImmutable(),
                query,
                _reader.SpanFrom(start)));
        } while (_reader.Match(TokenType.Comma));

        return result.ToImmutable();
    }

    private SelectStatement ParseSelectBody(ImmutableArray<CteDefinition> ctes)
    {
        var start = _reader.Position;
        _reader.ExpectWord("SELECT");
        var distinct = false;
        if (_reader.MatchWord("DISTINCT")) distinct = true;
        else _reader.MatchWord("ALL");

        var select = ParseSelectItems();
        TableSource? from = null;
        var joins = ImmutableArray.CreateBuilder<JoinSource>();
        if (_reader.MatchWord("FROM"))
        {
            from = ParseTableSource(requireDerivedAlias: true);
            while (IsJoinStart(_reader.Peek()))
                joins.Add(ParseJoin());
        }

        SqlExpr? where = null;
        if (_reader.MatchWord("WHERE"))
            where = _expressions.ParseExpression();

        var groupBy = ImmutableArray<SqlExpr>.Empty;
        if (_reader.MatchWord("GROUP"))
        {
            _reader.ExpectWord("BY");
            groupBy = ParseExpressionList();
        }

        SqlExpr? having = null;
        if (_reader.MatchWord("HAVING"))
            having = _expressions.ParseExpression();

        return new SelectStatement(
            ctes,
            distinct,
            select,
            from,
            joins.ToImmutable(),
            where,
            groupBy,
            having,
            ImmutableArray<OrderByItem>.Empty,
            null,
            null,
            _reader.SpanFrom(start));
    }

    private ImmutableArray<SelectItem> ParseSelectItems()
    {
        var items = ImmutableArray.CreateBuilder<SelectItem>();
        do
        {
            var start = _reader.Position;
            var expression = _expressions.ParseExpression();
            string? alias = null;
            if (_reader.MatchWord("AS"))
                alias = _reader.ExpectIdentifier("projection alias").Value;
            else if (_reader.Peek().Type == TokenType.Identifier)
                alias = _reader.Advance().Value;
            items.Add(new SelectItem(expression, alias, _reader.SpanFrom(start)));
        } while (_reader.Match(TokenType.Comma));
        return items.ToImmutable();
    }

    private TableSource ParseTableSource(bool requireDerivedAlias)
    {
        var start = _reader.Position;
        if (_reader.MatchWord("LATERAL"))
        {
            throw CoreTokenReader.Error(
                "LATERAL sources are not represented by the Core AST and are rejected explicitly.",
                _reader.Peek(-1));
        }

        if (_reader.Match(TokenType.LParen))
        {
            var query = ParseQueryExpression();
            _reader.Expect(TokenType.RParen, "')' after derived table query");
            var alias = ParseOptionalAlias();
            if (requireDerivedAlias && string.IsNullOrWhiteSpace(alias))
                throw CoreTokenReader.Error("A derived table requires an explicit alias.", _reader.Peek());
            return new DerivedTableSource(query, alias!, _reader.SpanFrom(start));
        }

        var name = _reader.ParseIdentifierPath("table name");
        var tableAlias = ParseOptionalAlias();
        return new NamedTableSource(name, tableAlias, _reader.SpanFrom(start));
    }

    private JoinSource ParseJoin()
    {
        var start = _reader.Position;
        if (_reader.MatchWord("NATURAL"))
        {
            throw CoreTokenReader.Error(
                "NATURAL JOIN is rejected because its schema-dependent implicit predicate is not represented in the Core AST.",
                _reader.Peek(-1));
        }

        var kind = "INNER";
        if (_reader.MatchWord("LEFT"))
        {
            kind = "LEFT";
            _reader.MatchWord("OUTER");
        }
        else if (_reader.MatchWord("RIGHT"))
        {
            kind = "RIGHT";
            _reader.MatchWord("OUTER");
        }
        else if (_reader.MatchWord("FULL"))
        {
            kind = "FULL";
            _reader.MatchWord("OUTER");
        }
        else if (_reader.MatchWord("CROSS"))
            kind = "CROSS";
        else
            _reader.MatchWord("INNER");

        _reader.ExpectWord("JOIN");
        var source = ParseTableSource(requireDerivedAlias: true);

        SqlExpr? predicate = null;
        if (kind == "CROSS")
        {
            if (_reader.PeekWord("ON") || _reader.PeekWord("USING"))
                throw CoreTokenReader.Error("CROSS JOIN must not have ON/USING predicates.", _reader.Peek());
        }
        else if (_reader.MatchWord("ON"))
            predicate = _expressions.ParseExpression();
        else if (_reader.PeekWord("USING"))
        {
            throw CoreTokenReader.Error(
                "JOIN USING is rejected until using-column semantics are represented explicitly in the Core AST.",
                _reader.Peek());
        }
        else
            throw CoreTokenReader.Error($"{kind} JOIN requires an ON predicate.", _reader.Peek());

        return new JoinSource(kind, source, predicate, _reader.SpanFrom(start));
    }

    private string? ParseOptionalAlias()
    {
        if (_reader.MatchWord("AS"))
            return _reader.ExpectIdentifier("table alias").Value;
        if (_reader.Peek().Type == TokenType.Identifier)
            return _reader.Advance().Value;
        return null;
    }

    private ImmutableArray<SqlExpr> ParseExpressionList()
    {
        var result = ImmutableArray.CreateBuilder<SqlExpr>();
        do result.Add(_expressions.ParseExpression());
        while (_reader.Match(TokenType.Comma));
        return result.ToImmutable();
    }

    private ImmutableArray<OrderByItem> ParseOrderByIfPresent()
    {
        if (!_reader.MatchWord("ORDER"))
            return ImmutableArray<OrderByItem>.Empty;
        _reader.ExpectWord("BY");
        var result = ImmutableArray.CreateBuilder<OrderByItem>();
        do
        {
            var start = _reader.Position;
            var expression = _expressions.ParseExpression();
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
            result.Add(new OrderByItem(expression, descending, nullOrdering, _reader.SpanFrom(start)));
        } while (_reader.Match(TokenType.Comma));
        return result.ToImmutable();
    }

    private (int? Limit, int? Offset) ParseLimitOffsetIfPresent()
    {
        int? limit = null;
        int? offset = null;
        if (_reader.MatchWord("LIMIT"))
            limit = ParseNonNegativeInt("LIMIT");
        if (_reader.MatchWord("OFFSET"))
            offset = ParseNonNegativeInt("OFFSET");
        return (limit, offset);
    }

    private int ParseNonNegativeInt(string description)
    {
        var token = _reader.Expect(TokenType.Number, $"non-negative integer after {description}");
        if (!int.TryParse(token.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw CoreTokenReader.Error($"{description} requires a non-negative integer.", token);
        return value;
    }

    private SqlIdentifier ParseSingleIdentifier(string description)
    {
        var token = _reader.ExpectIdentifier(description);
        return new SqlIdentifier(
            [new IdentifierPart(
                token.Value,
                token.Length > token.Value.Length,
                CoreTokenReader.Span(token))],
            CoreTokenReader.Span(token));
    }

    private SetOperationKind ParseSetOperationKind()
    {
        if (_reader.MatchWord("UNION"))
        {
            if (_reader.MatchWord("ALL")) return SetOperationKind.UnionAll;
            _reader.MatchWord("DISTINCT");
            return SetOperationKind.Union;
        }
        if (_reader.MatchWord("INTERSECT"))
        {
            if (_reader.MatchWord("ALL"))
                throw CoreTokenReader.Error("INTERSECT ALL is not represented by the Core set-operation model.", _reader.Peek(-1));
            _reader.MatchWord("DISTINCT");
            return SetOperationKind.Intersect;
        }
        if (_reader.MatchWord("EXCEPT"))
        {
            if (_reader.MatchWord("ALL"))
                throw CoreTokenReader.Error("EXCEPT ALL is not represented by the Core set-operation model.", _reader.Peek(-1));
            _reader.MatchWord("DISTINCT");
            return SetOperationKind.Except;
        }
        throw CoreTokenReader.Error("Expected set operation.", _reader.Peek());
    }

    private static bool IsSetOperation(Token token) =>
        CoreTokenReader.IsWord(token, "UNION")
        || CoreTokenReader.IsWord(token, "INTERSECT")
        || CoreTokenReader.IsWord(token, "EXCEPT");

    private static bool IsJoinStart(Token token) =>
        CoreTokenReader.IsWord(token, "JOIN")
        || CoreTokenReader.IsWord(token, "LEFT")
        || CoreTokenReader.IsWord(token, "RIGHT")
        || CoreTokenReader.IsWord(token, "INNER")
        || CoreTokenReader.IsWord(token, "FULL")
        || CoreTokenReader.IsWord(token, "CROSS")
        || CoreTokenReader.IsWord(token, "NATURAL");
}

internal sealed class CoreExpressionTextParser(
    CoreTokenReader reader,
    Func<SqlStatement> parseSubquery)
{
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
        if (!_reader.PeekWord("NOT"))
            return ParsePredicate();
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
            var op = _reader.Advance().Value == "!=" ? "<>" : token.Value;
            var right = ParseAdditive();
            return new BinaryExpr(left, op, right, _reader.SpanFrom(start));
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
            var operatorToken = _reader.Peek(-1);
            var op = operatorToken.Value.ToUpperInvariant();
            if (negatedModifier) op = "NOT " + op;
            if (op == "NOT ILIKE")
                return new UnaryExpr(
                    "NOT",
                    new BinaryExpr(left, "ILIKE", ParseAdditive(), _reader.SpanFrom(start)),
                    _reader.SpanFrom(start));
            if (op == "NOT LIKE")
                return new UnaryExpr(
                    "NOT",
                    new BinaryExpr(left, "LIKE", ParseAdditive(), _reader.SpanFrom(start)),
                    _reader.SpanFrom(start));
            return new BinaryExpr(left, op, ParseAdditive(), _reader.SpanFrom(start));
        }

        if (negatedModifier)
            throw CoreTokenReader.Error("NOT must be followed by IN, BETWEEN, LIKE, or ILIKE in this predicate position.", _reader.Peek());

        return left;
    }

    private SqlExpr ParseAdditive()
    {
        var start = _reader.Position;
        var left = ParseMultiplicative();
        while (_reader.Peek().Type == TokenType.Operator
               && _reader.Peek().Value is "+" or "-" or "||")
        {
            var op = _reader.Advance().Value;
            left = new BinaryExpr(left, op, ParseMultiplicative(), _reader.SpanFrom(start));
        }
        return left;
    }

    private SqlExpr ParseMultiplicative()
    {
        var start = _reader.Position;
        var left = ParsePostfix();
        while (_reader.Peek().Type == TokenType.Operator
               && _reader.Peek().Value is "*" or "/" or "%")
        {
            var op = _reader.Advance().Value;
            left = new BinaryExpr(left, op, ParsePostfix(), _reader.SpanFrom(start));
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
                $"Unary '{sign.Value}' is currently accepted only for numeric literals; general unary arithmetic is not represented by the Core lowerer.",
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

        if (_reader.MatchWord("CASE"))
            return ParseCase(start);
        if (_reader.MatchWord("CAST"))
            return ParseCast(start);
        if (_reader.MatchWord("EXISTS"))
        {
            _reader.Expect(TokenType.LParen, "'(' after EXISTS");
            var query = _parseSubquery();
            _reader.Expect(TokenType.RParen, "')' after EXISTS subquery");
            return new ExistsExpr(query, false, _reader.SpanFrom(start));
        }
        if (_reader.MatchWord("NULL"))
            return new LiteralExpr(null, _reader.SpanFrom(start));
        if (_reader.MatchWord("TRUE"))
            return new LiteralExpr(true, _reader.SpanFrom(start));
        if (_reader.MatchWord("FALSE"))
            return new LiteralExpr(false, _reader.SpanFrom(start));

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

        if (IsTemporalType(token.Value))
            return ParseTemporalLiteral(start);

        if (_reader.PeekWord("INTERVAL") && _reader.Peek(1).Type == TokenType.String)
        {
            _reader.Advance();
            var literal = DecodeString(_reader.Advance().Value);
            return new IntervalExpr(literal, _reader.SpanFrom(start));
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

        if ((token.Type is TokenType.Identifier or TokenType.Keyword)
            && _reader.Peek(1).Type == TokenType.LParen)
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
        if (_reader.PeekWord("ROWS") || _reader.PeekWord("RANGE"))
            frame = ParseWindowFrame();
        _reader.Expect(TokenType.RParen, "')' after window specification");
        return new WindowSpec(partitionBy, orderBy, frame, _reader.SpanFrom(start));
    }

    private WindowFrame ParseWindowFrame()
    {
        var start = _reader.Position;
        var unitToken = _reader.Advance();
        var unit = CoreTokenReader.IsWord(unitToken, "ROWS")
            ? WindowFrameUnitKind.Rows
            : WindowFrameUnitKind.Range;
        WindowFrameBoundCore first;
        WindowFrameBoundCore? second = null;
        if (_reader.MatchWord("BETWEEN"))
        {
            first = ParseWindowBound();
            _reader.ExpectWord("AND");
            second = ParseWindowBound();
        }
        else
            first = ParseWindowBound();
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
            var value = ParseExpression();
            branches.Add(new CaseBranch(condition, value));
        }
        if (branches.Count == 0)
            throw CoreTokenReader.Error("CASE requires at least one WHEN branch.", _reader.Peek());
        SqlExpr? otherwise = null;
        if (_reader.MatchWord("ELSE"))
            otherwise = ParseExpression();
        _reader.ExpectWord("END");
        return new CaseExpr(branches.ToImmutable(), otherwise, _reader.SpanFrom(start));
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
        while (_reader.Peek().Type is TokenType.Identifier or TokenType.Keyword
               && IsCastTypeQualifier(_reader.Peek().Value))
            parts.Add(_reader.Advance().Value);

        if (_reader.Match(TokenType.LParen))
        {
            var suffix = new StringBuilder("(");
            var first = _reader.Expect(TokenType.Number, "cast type precision");
            if (!int.TryParse(first.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw CoreTokenReader.Error("Cast type precision must be an integer.", first);
            suffix.Append(first.Value);
            if (_reader.Match(TokenType.Comma))
            {
                var second = _reader.Expect(TokenType.Number, "cast type scale");
                if (!int.TryParse(second.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw CoreTokenReader.Error("Cast type scale must be an integer.", second);
                suffix.Append(',').Append(second.Value);
            }
            _reader.Expect(TokenType.RParen, "')' after cast type precision");
            suffix.Append(')');
            parts[^1] += suffix;
        }
        return string.Join(' ', parts);
    }

    private SqlExpr ParseTemporalLiteral(int start)
    {
        var typeToken = _reader.Advance();
        var type = typeToken.Value.ToUpperInvariant();
        bool? withTimeZone = null;
        if (type is "TIME" or "TIMESTAMP"
            && (_reader.PeekWord("WITH") || _reader.PeekWord("WITHOUT")))
        {
            withTimeZone = _reader.MatchWord("WITH");
            if (withTimeZone != true) _reader.ExpectWord("WITHOUT");
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

    private static SqlIdentifier IdentifierFromToken(Token token) =>
        new(
            [new IdentifierPart(
                token.Value,
                token.Type == TokenType.Identifier && token.Length > token.Value.Length,
                CoreTokenReader.Span(token))],
            CoreTokenReader.Span(token));

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

    private static bool IsComparisonOperator(string value) => value is "=" or "<>" or "!=" or ">" or "<" or ">=" or "<=";

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

internal sealed class CoreDmlTextParser
{
    private readonly CoreTokenReader _reader;
    private readonly CoreExpressionTextParser _expressions;

    public CoreDmlTextParser(CoreTokenReader reader)
    {
        _reader = reader;
        _expressions = new CoreExpressionTextParser(reader, ParseNestedQuery);
    }

    public SqlStatement ParseComplete()
    {
        SqlStatement statement = _reader.PeekWord("INSERT") ? ParseInsert()
            : _reader.PeekWord("UPDATE") ? ParseUpdate()
            : _reader.PeekWord("DELETE") ? ParseDelete()
            : throw CoreTokenReader.Error("Expected INSERT, UPDATE, or DELETE DML statement.", _reader.Peek());
        _reader.Match(TokenType.Semicolon);
        if (_reader.Peek().Type != TokenType.EOF)
            throw CoreTokenReader.Error($"Unexpected token '{_reader.Peek().Value}'; the complete DML statement was not consumed.", _reader.Peek());
        return statement;
    }

    private InsertStatement ParseInsert()
    {
        var start = _reader.Position;
        _reader.ExpectWord("INSERT");
        _reader.ExpectWord("INTO");
        var target = new NamedTableSource(_reader.ParseIdentifierPath("INSERT target table"), null, _reader.SpanFrom(start));
        _reader.Expect(TokenType.LParen, "'(' before INSERT column list");
        var columns = ImmutableArray.CreateBuilder<SqlIdentifier>();
        do
        {
            var column = _reader.ParseIdentifierPath("INSERT target column");
            if (column.Parts.Length != 1)
                throw CoreTokenReader.Error("INSERT target columns must be unqualified.", _reader.Peek(-1));
            columns.Add(column);
        } while (_reader.Match(TokenType.Comma));
        _reader.Expect(TokenType.RParen, "')' after INSERT column list");
        if (columns.Count == 0)
            throw CoreTokenReader.Error("INSERT requires at least one target column.", _reader.Peek());

        InsertSource source;
        if (_reader.MatchWord("VALUES"))
        {
            var rows = ImmutableArray.CreateBuilder<ImmutableArray<SqlExpr>>();
            do
            {
                var rowStart = _reader.Position;
                _reader.Expect(TokenType.LParen, "'(' before INSERT VALUES row");
                var values = ImmutableArray.CreateBuilder<SqlExpr>();
                if (_reader.Peek().Type == TokenType.RParen)
                    throw CoreTokenReader.Error("INSERT VALUES row cannot be empty.", _reader.Peek());
                do values.Add(ParseDmlLiteral());
                while (_reader.Match(TokenType.Comma));
                _reader.Expect(TokenType.RParen, "')' after INSERT VALUES row");
                if (values.Count != columns.Count)
                    throw CoreTokenReader.Error(
                        $"INSERT row has {values.Count} values but {columns.Count} columns were declared.",
                        _reader.Peek(-1));
                rows.Add(values.ToImmutable());
                _ = rowStart;
            } while (_reader.Match(TokenType.Comma));
            source = new InsertValuesSource(rows.ToImmutable(), _reader.SpanFrom(start));
        }
        else if (_reader.PeekWord("SELECT") || _reader.PeekWord("WITH"))
            source = new InsertQuerySource(ParseNestedQuery(), _reader.SpanFrom(start));
        else
            throw CoreTokenReader.Error("INSERT requires VALUES or a SELECT source.", _reader.Peek());

        return new InsertStatement(target, columns.ToImmutable(), source, _reader.SpanFrom(start));
    }

    private UpdateStatement ParseUpdate()
    {
        var start = _reader.Position;
        _reader.ExpectWord("UPDATE");
        var targetName = _reader.ParseIdentifierPath("UPDATE target table");
        var target = new NamedTableSource(targetName, null, targetName.Span);
        if (_reader.Peek().Type == TokenType.Identifier || _reader.PeekWord("AS"))
            throw CoreTokenReader.Error("UPDATE target aliases are not represented by the Core DML AST.", _reader.Peek());
        _reader.ExpectWord("SET");
        var assignments = ImmutableArray.CreateBuilder<Assignment>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            var assignmentStart = _reader.Position;
            var column = _reader.ParseIdentifierPath("UPDATE assignment column");
            if (column.Parts.Length != 1)
                throw CoreTokenReader.Error("UPDATE assignment columns must be unqualified.", _reader.Peek(-1));
            var columnName = column.Parts[0].Value;
            if (!seen.Add(columnName))
                throw CoreTokenReader.Error($"UPDATE assigns column '{columnName}' more than once.", _reader.Peek(-1));
            var equals = _reader.Peek();
            if (equals.Type != TokenType.Operator || equals.Value != "=")
                throw CoreTokenReader.Error("Expected '=' in UPDATE assignment.", equals);
            _reader.Advance();
            assignments.Add(new Assignment(column, ParseDmlLiteral(), _reader.SpanFrom(assignmentStart)));
        } while (_reader.Match(TokenType.Comma));

        SqlExpr? predicate = null;
        if (_reader.MatchWord("WHERE"))
            predicate = _expressions.ParseExpression();
        return new UpdateStatement(target, assignments.ToImmutable(), predicate, _reader.SpanFrom(start));
    }

    private DeleteStatement ParseDelete()
    {
        var start = _reader.Position;
        _reader.ExpectWord("DELETE");
        _reader.ExpectWord("FROM");
        var name = _reader.ParseIdentifierPath("DELETE target table");
        var target = new NamedTableSource(name, null, name.Span);
        if (_reader.Peek().Type == TokenType.Identifier || _reader.PeekWord("AS"))
            throw CoreTokenReader.Error("DELETE target aliases are not represented by the Core DML AST.", _reader.Peek());
        SqlExpr? predicate = null;
        if (_reader.MatchWord("WHERE"))
            predicate = _expressions.ParseExpression();
        return new DeleteStatement(target, predicate, _reader.SpanFrom(start));
    }

    private SqlExpr ParseDmlLiteral()
    {
        var start = _reader.Position;
        var token = _reader.Peek();
        if (token.Type == TokenType.Operator && token.Value is "+" or "-")
        {
            var sign = _reader.Advance();
            var number = _reader.Expect(TokenType.Number, "numeric literal after unary sign");
            var value = CoreExpressionTextParser.ParseNumber(number.Value);
            if (sign.Value == "-")
            {
                value = value switch
                {
                    int integer => -integer,
                    decimal decimalValue => -decimalValue,
                    _ => throw CoreTokenReader.Error("Unsupported signed numeric literal.", sign)
                };
            }
            return new LiteralExpr(value, _reader.SpanFrom(start));
        }
        if (token.Type == TokenType.Number)
        {
            _reader.Advance();
            return new LiteralExpr(CoreExpressionTextParser.ParseNumber(token.Value), _reader.SpanFrom(start));
        }
        if (token.Type == TokenType.String)
        {
            _reader.Advance();
            return new LiteralExpr(CoreExpressionTextParser.DecodeString(token.Value), _reader.SpanFrom(start));
        }
        if (_reader.MatchWord("NULL")) return new LiteralExpr(null, _reader.SpanFrom(start));
        if (_reader.MatchWord("TRUE")) return new LiteralExpr(true, _reader.SpanFrom(start));
        if (_reader.MatchWord("FALSE")) return new LiteralExpr(false, _reader.SpanFrom(start));
        if (token.Value.Equals("DATE", StringComparison.OrdinalIgnoreCase)
            || token.Value.Equals("TIME", StringComparison.OrdinalIgnoreCase)
            || token.Value.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase))
        {
            return ParseTemporalDmlLiteral(start);
        }
        throw CoreTokenReader.Error(
            $"Unsupported DML value expression beginning with '{token.Value}'. Only scalar literals are accepted.",
            token);
    }

    private SqlExpr ParseTemporalDmlLiteral(int start)
    {
        var typeToken = _reader.Advance();
        var type = typeToken.Value.ToUpperInvariant();
        bool? withTimeZone = null;
        if (type is "TIME" or "TIMESTAMP"
            && (_reader.PeekWord("WITH") || _reader.PeekWord("WITHOUT")))
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
        var literal = CoreExpressionTextParser.DecodeString(literalToken.Value);
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

    private SqlStatement ParseNestedQuery() => new CoreQueryTextParser(_reader).ParseQueryExpression();
}
