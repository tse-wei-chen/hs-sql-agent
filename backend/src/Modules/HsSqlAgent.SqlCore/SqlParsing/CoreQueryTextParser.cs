using System.Collections.Immutable;
using System.Globalization;

namespace HsSqlAgent.SqlCore.SqlParsing;

internal sealed class CoreQueryTextParser
{
    private readonly CoreTokenReader _reader;
    private readonly SqlAgentToolType _sourceDialect;
    private readonly CoreExpressionTextParser _expressions;

    public CoreQueryTextParser(
        CoreTokenReader reader,
        SqlAgentToolType sourceDialect,
        bool requireExplicitLikeEscape = false)
    {
        _reader = reader;
        _sourceDialect = sourceDialect;
        _expressions = new CoreExpressionTextParser(
            reader,
            ParseQueryExpression,
            requireExplicitLikeEscape);
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
        var (limit, offset) = ParseLimitOffsetIfPresent(orderBy.Length > 0);
        if (topLimit is not null)
        {
            if (operations.Count > 0)
                throw new SqlParseException("SQL Server TOP with set operations is not represented losslessly by the Core AST.");
            if (limit is not null || offset is not null)
                throw new SqlParseException("SQL Server TOP cannot be combined with OFFSET/FETCH in the same canonical query scope.");
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
                do columnAliases.Add(ParseSingleIdentifier("CTE column alias"));
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
        if (_reader.MatchWord("WHERE")) where = _expressions.ParseExpression();

        var groupBy = ImmutableArray<SqlExpr>.Empty;
        if (_reader.MatchWord("GROUP"))
        {
            _reader.ExpectWord("BY");
            groupBy = ParseExpressionList();
        }

        SqlExpr? having = null;
        if (_reader.MatchWord("HAVING")) having = _expressions.ParseExpression();

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
            IdentifierPart? alias = null;
            if (_reader.MatchWord("AS"))
                alias = CoreTokenReader.ToIdentifierPart(_reader.ExpectIdentifier("projection alias"));
            else if (CanConsumeImplicitAlias())
                alias = CoreTokenReader.ToIdentifierPart(_reader.Advance());
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
            if (requireDerivedAlias && alias is null)
                throw CoreTokenReader.Error("A derived table requires an explicit alias.", _reader.Peek());
            return new DerivedTableSource(query, alias!, _reader.SpanFrom(start));
        }

        var name = _reader.ParseIdentifierPath("table name");
        return new NamedTableSource(name, ParseOptionalAlias(), _reader.SpanFrom(start));
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
        else if (_reader.MatchWord("CROSS")) kind = "CROSS";
        else _reader.MatchWord("INNER");

        _reader.ExpectWord("JOIN");
        var source = ParseTableSource(requireDerivedAlias: true);
        SqlExpr? predicate = null;
        if (kind == "CROSS")
        {
            if (_reader.PeekWord("ON") || _reader.PeekWord("USING"))
                throw CoreTokenReader.Error("CROSS JOIN must not have ON/USING predicates.", _reader.Peek());
        }
        else if (_reader.MatchWord("ON")) predicate = _expressions.ParseExpression();
        else if (_reader.PeekWord("USING"))
        {
            throw CoreTokenReader.Error(
                "JOIN USING is rejected until using-column semantics are represented explicitly in the Core AST.",
                _reader.Peek());
        }
        else throw CoreTokenReader.Error($"{kind} JOIN requires an ON predicate.", _reader.Peek());

        return new JoinSource(kind, source, predicate, _reader.SpanFrom(start));
    }

    private IdentifierPart? ParseOptionalAlias()
    {
        if (_reader.MatchWord("AS"))
            return CoreTokenReader.ToIdentifierPart(_reader.ExpectIdentifier("table alias"));
        if (CanConsumeImplicitAlias())
            return CoreTokenReader.ToIdentifierPart(_reader.Advance());
        return null;
    }

    private bool CanConsumeImplicitAlias() =>
        _reader.Peek().Type == TokenType.Identifier && !IsFetchClauseStart();

    private bool IsFetchClauseStart() =>
        _reader.PeekWord("FETCH")
        && (_reader.PeekWord(1, "FIRST") || _reader.PeekWord(1, "NEXT"));

    private ImmutableArray<SqlExpr> ParseExpressionList()
    {
        var result = ImmutableArray.CreateBuilder<SqlExpr>();
        do result.Add(_expressions.ParseExpression());
        while (_reader.Match(TokenType.Comma));
        return result.ToImmutable();
    }

    private ImmutableArray<OrderByItem> ParseOrderByIfPresent()
    {
        if (!_reader.MatchWord("ORDER")) return ImmutableArray<OrderByItem>.Empty;
        _reader.ExpectWord("BY");
        var result = ImmutableArray.CreateBuilder<OrderByItem>();
        do
        {
            var start = _reader.Position;
            var firstToken = _reader.Peek();
            var expressionStart = _reader.Position;
            var expression = _expressions.ParseExpression();
            var expressionEnd = _reader.Position;

            // A bare unsigned integer in a statement-level ORDER BY denotes a 1-based output
            // position in the SQL dialects supported by Core. Preserve that semantic distinction
            // instead of parameterizing the integer as a scalar constant. Window ORDER BY is
            // parsed separately in CoreExpressionTextParser and intentionally does not use this
            // marker because it orders input rows, not SELECT-list outputs.
            if (firstToken.Type == TokenType.Number
                && expressionEnd == expressionStart + 1
                && firstToken.Value.All(char.IsDigit))
            {
                if (!int.TryParse(
                        firstToken.Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var ordinal))
                {
                    throw CoreTokenReader.Error(
                        "ORDER BY output position exceeds the supported integer range.",
                        firstToken);
                }
                expression = new LiteralExpr(
                    new OrderByOrdinalValue(ordinal),
                    expression.Span);
            }

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

    private (int? Limit, int? Offset) ParseLimitOffsetIfPresent(bool hasOrderBy)
    {
        var grammar = SqlSourceDialectGrammarRules.For(_sourceDialect);
        int? limit = null;
        int? offset = null;
        var usedLimitClause = false;
        var usedCommaLimit = false;
        if (_reader.MatchWord("LIMIT"))
        {
            usedLimitClause = true;
            if (_reader.MatchWord("ALL"))
            {
                if (!grammar.SupportsLimitAll)
                {
                    throw CoreTokenReader.Error(
                        $"LIMIT ALL raw source syntax is valid only for PostgreSQL, not {_sourceDialect}.",
                        _reader.Peek(-1));
                }
            }
            else
            {
                var first = ParseNonNegativeInt("LIMIT");
                if (_reader.Peek().Type == TokenType.Comma)
                {
                    var comma = _reader.Advance();
                    if (!grammar.SupportsCommaLimit)
                    {
                        throw CoreTokenReader.Error(
                            $"LIMIT offset,row_count raw source syntax is valid only for MySQL and SQLite, not {_sourceDialect}.",
                            comma);
                    }

                    usedCommaLimit = true;
                    offset = first;
                    limit = ParseNonNegativeInt("LIMIT comma row count");
                }
                else
                {
                    limit = first;
                }
            }
        }

        if (_reader.PeekWord("OFFSET"))
        {
            var offsetToken = _reader.Peek();
            if (usedCommaLimit)
            {
                throw CoreTokenReader.Error(
                    "LIMIT offset,row_count cannot be combined with a separate OFFSET clause.",
                    offsetToken);
            }

            if (grammar.OffsetRequiresLimit && !usedLimitClause)
            {
                throw CoreTokenReader.Error(
                    $"OFFSET without a preceding LIMIT is not valid raw source syntax for {_sourceDialect}.",
                    offsetToken);
            }
            if (grammar.OffsetRequiresOrderBy && !hasOrderBy)
            {
                throw CoreTokenReader.Error(
                    "SQL Server OFFSET/FETCH raw source syntax requires a statement-level ORDER BY clause.",
                    offsetToken);
            }

            _reader.Advance();
            offset = ParseNonNegativeInt("OFFSET");
            if (grammar.UsesStandardOffsetFetch)
                ParseOffsetRowKeyword();

            if (_reader.PeekWord("FETCH"))
            {
                if (usedLimitClause)
                {
                    throw CoreTokenReader.Error(
                        "LIMIT and FETCH cannot be combined in the same raw query tail.",
                        _reader.Peek());
                }
                limit = ParseStandardFetchCount(hasPrecedingOffset: true);
            }
        }
        else if (_reader.PeekWord("FETCH"))
        {
            if (usedLimitClause)
            {
                throw CoreTokenReader.Error(
                    "LIMIT and FETCH cannot be combined in the same raw query tail.",
                    _reader.Peek());
            }
            if (grammar.FetchRequiresPrecedingOffset)
            {
                throw CoreTokenReader.Error(
                    "SQL Server FETCH requires a preceding OFFSET clause inside ORDER BY.",
                    _reader.Peek());
            }
            if (!grammar.SupportsFetch)
            {
                throw CoreTokenReader.Error(
                    $"FETCH FIRST/NEXT is not valid raw row-limiting syntax for {_sourceDialect}; use LIMIT instead.",
                    _reader.Peek());
            }
            limit = ParseStandardFetchCount(hasPrecedingOffset: false);
        }

        return (limit, offset);
    }

    private void ParseOffsetRowKeyword()
    {
        var grammar = SqlSourceDialectGrammarRules.For(_sourceDialect);
        if (grammar.OffsetRowKeywordOptional)
        {
            if (_reader.MatchWord("ROW")) return;
            _reader.MatchWord("ROWS");
            return;
        }

        if (_reader.MatchWord("ROW") || _reader.MatchWord("ROWS")) return;
        throw CoreTokenReader.Error(
            $"{_sourceDialect} OFFSET requires ROW or ROWS after the offset count in the modeled raw Core grammar.",
            _reader.Peek());
    }

    private int ParseStandardFetchCount(bool hasPrecedingOffset)
    {
        var grammar = SqlSourceDialectGrammarRules.For(_sourceDialect);
        var fetchToken = _reader.Advance();
        if (!CoreTokenReader.IsWord(fetchToken, "FETCH"))
            throw CoreTokenReader.Error("Expected FETCH.", fetchToken);

        if (grammar.FetchRequiresPrecedingOffset && !hasPrecedingOffset)
        {
            throw CoreTokenReader.Error(
                "SQL Server FETCH requires a preceding OFFSET clause.",
                fetchToken);
        }
        if (!grammar.SupportsFetch)
        {
            throw CoreTokenReader.Error(
                $"FETCH FIRST/NEXT is not valid raw row-limiting syntax for {_sourceDialect}.",
                fetchToken);
        }

        if (!(_reader.MatchWord("FIRST") || _reader.MatchWord("NEXT")))
            throw CoreTokenReader.Error("FETCH requires FIRST or NEXT.", _reader.Peek());

        int count;
        if (_reader.Peek().Type == TokenType.Number)
        {
            count = ParseNonNegativeInt("FETCH");
        }
        else if (!grammar.FetchCountOptional)
        {
            throw CoreTokenReader.Error(
                "SQL Server FETCH requires an explicit positive integer row count.",
                _reader.Peek());
        }
        else
        {
            count = 1;
        }

        if (grammar.FetchCountMustBePositive && count == 0)
        {
            throw CoreTokenReader.Error(
                "SQL Server FETCH row count must be greater than zero.",
                _reader.Peek(-1));
        }
        if (_reader.PeekWord("PERCENT"))
        {
            throw CoreTokenReader.Error(
                "FETCH PERCENT is not represented by the canonical Core row-limit model.",
                _reader.Peek());
        }

        if (!(_reader.MatchWord("ROW") || _reader.MatchWord("ROWS")))
            throw CoreTokenReader.Error("FETCH requires ROW or ROWS after the row count.", _reader.Peek());

        if (_reader.PeekWord("WITH"))
        {
            throw CoreTokenReader.Error(
                "FETCH WITH TIES is not represented by the canonical Core row-limit model.",
                _reader.Peek());
        }
        _reader.ExpectWord("ONLY");
        return count;
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
            [new IdentifierPart(token.Value, CoreTokenReader.IsQuotedIdentifier(token), CoreTokenReader.Span(token))],
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
