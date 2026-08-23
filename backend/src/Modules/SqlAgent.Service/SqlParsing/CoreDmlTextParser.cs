using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.SqlParsing;

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
        {
            throw CoreTokenReader.Error(
                $"Unexpected token '{_reader.Peek().Value}'; the complete DML statement was not consumed.",
                _reader.Peek());
        }
        return statement;
    }

    private InsertStatement ParseInsert()
    {
        var start = _reader.Position;
        _reader.ExpectWord("INSERT");
        _reader.ExpectWord("INTO");
        var targetName = _reader.ParseIdentifierPath("INSERT target table");
        var target = new NamedTableSource(targetName, null, targetName.Span);
        _reader.Expect(TokenType.LParen, "'(' before INSERT column list");
        var columns = ImmutableArray.CreateBuilder<SqlIdentifier>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            var column = _reader.ParseIdentifierPath("INSERT target column");
            if (column.Parts.Length != 1)
                throw CoreTokenReader.Error("INSERT target columns must be unqualified.", _reader.Peek(-1));
            if (!seen.Add(column.Parts[0].Value))
                throw CoreTokenReader.Error($"INSERT target column '{column.Parts[0].Value}' is declared more than once.", _reader.Peek(-1));
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
                _reader.Expect(TokenType.LParen, "'(' before INSERT VALUES row");
                var values = ImmutableArray.CreateBuilder<SqlExpr>();
                if (_reader.Peek().Type == TokenType.RParen)
                    throw CoreTokenReader.Error("INSERT VALUES row cannot be empty.", _reader.Peek());
                do values.Add(ParseDmlLiteral());
                while (_reader.Match(TokenType.Comma));
                _reader.Expect(TokenType.RParen, "')' after INSERT VALUES row");
                if (values.Count != columns.Count)
                {
                    throw CoreTokenReader.Error(
                        $"INSERT row has {values.Count} values but {columns.Count} columns were declared.",
                        _reader.Peek(-1));
                }
                rows.Add(values.ToImmutable());
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
        if (_reader.MatchWord("WHERE")) predicate = _expressions.ParseExpression();
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
        if (_reader.MatchWord("WHERE")) predicate = _expressions.ParseExpression();
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
        if (IsTemporalLiteralStart(token)) return ParseTemporalDmlLiteral(start);

        throw CoreTokenReader.Error(
            $"Unsupported DML value expression beginning with '{token.Value}'. Only scalar literals are accepted.",
            token);
    }

    private bool IsTemporalLiteralStart(Token token)
    {
        if (CoreTokenReader.IsQuotedIdentifier(token)) return false;
        var temporal = token.Value.Equals("DATE", StringComparison.OrdinalIgnoreCase)
            || token.Value.Equals("TIME", StringComparison.OrdinalIgnoreCase)
            || token.Value.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase);
        if (!temporal) return false;
        if (_reader.Peek(1).Type == TokenType.String) return true;
        return (token.Value.Equals("TIME", StringComparison.OrdinalIgnoreCase)
                || token.Value.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase))
            && (_reader.PeekWord(1, "WITH") || _reader.PeekWord(1, "WITHOUT"));
    }

    private SqlExpr ParseTemporalDmlLiteral(int start)
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

    private SqlStatement ParseNestedQuery() =>
        new CoreQueryTextParser(_reader).ParseQueryExpression();
}
