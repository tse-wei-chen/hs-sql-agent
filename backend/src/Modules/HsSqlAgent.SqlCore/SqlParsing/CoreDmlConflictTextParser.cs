using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.SqlParsing;

/// <summary>
/// Extracts the deliberately small portable INSERT conflict grammar before the ordinary DML parser
/// consumes the statement. PostgreSQL/SQLite ON CONFLICT and the metadata-gated Firebird UPDATE OR
/// INSERT ... MATCHING shape canonicalize to the same explicit conflict AST.
/// </summary>
internal static class CoreDmlConflictTextParser
{
    public static (Token[] Tokens, InsertConflictClause? Conflict) Extract(
        Token[] tokens,
        SqlAgentToolType sourceDialect,
        Version? sourceServerVersion)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (IsFirebirdUpdateOrInsertStart(tokens))
            return ExtractFirebirdUpdateOrInsert(tokens, sourceDialect);
        if (tokens.Length == 0 || !CoreTokenReader.IsWord(tokens[0], "INSERT"))
            return (tokens, null);

        var onIndex = FindRootConflictClause(tokens);
        if (onIndex < 0)
            return (tokens, null);

        var slice = tokens[onIndex..];
        var reader = new CoreTokenReader(slice);
        var start = reader.Position;
        var onToken = reader.ExpectWord("ON");

        if (!reader.MatchWord("CONFLICT"))
        {
            var sourceGrammar = SqlSourceDialectGrammarRules.For(sourceDialect);
            if (sourceGrammar.SupportsOnDuplicateKeyUpsertSyntax
                && reader.PeekWord("DUPLICATE"))
            {
                throw CoreTokenReader.Error(
                    "MySQL ON DUPLICATE KEY UPDATE has no explicit conflict target, so Core cannot translate it to the deterministic portable ON CONFLICT contract.",
                    onToken);
            }

            throw CoreTokenReader.Error(
                "Portable INSERT conflict handling requires an explicit ON CONFLICT clause.",
                onToken);
        }

        ValidateOnConflictSourceContract(sourceDialect, sourceServerVersion, onToken);
        reader.Expect(TokenType.LParen, "'(' before ON CONFLICT target column list");
        var targetColumns = ParseUniqueSinglePartColumns(reader, "ON CONFLICT target column");
        reader.Expect(TokenType.RParen, "')' after ON CONFLICT target column list");
        if (targetColumns.IsDefaultOrEmpty)
            throw CoreTokenReader.Error("ON CONFLICT requires at least one explicit target column.", onToken);

        reader.ExpectWord("DO");
        InsertConflictActionKind action;
        ImmutableArray<InsertConflictAssignment> assignments;
        if (reader.MatchWord("NOTHING"))
        {
            action = InsertConflictActionKind.DoNothing;
            assignments = ImmutableArray<InsertConflictAssignment>.Empty;
        }
        else
        {
            reader.ExpectWord("UPDATE");
            reader.ExpectWord("SET");
            action = InsertConflictActionKind.UpdateProposedValues;
            assignments = ParseAssignments(reader);
        }

        ValidateTrailer(reader);
        var consumed = reader.Position;
        var conflict = new InsertConflictClause(
            targetColumns,
            action,
            assignments,
            reader.SpanFrom(start));
        return (RemoveRange(tokens, onIndex, consumed), conflict);
    }

    private static (Token[] Tokens, InsertConflictClause? Conflict) ExtractFirebirdUpdateOrInsert(
        Token[] tokens,
        SqlAgentToolType sourceDialect)
    {
        var sourceError =
            SqlDmlUpsertCapabilityRules.FirebirdUpdateOrInsertSourceValidationError(sourceDialect);
        if (sourceError is not null)
            throw CoreTokenReader.Error(sourceError, tokens[0]);

        // Drop UPDATE OR so the ordinary INSERT parser can own target/VALUES/RETURNING parsing.
        var normalizedPrefix = new Token[tokens.Length - 2];
        Array.Copy(tokens, 2, normalizedPrefix, 0, normalizedPrefix.Length);

        var matchingIndex = FindRootClauseAfterValues(normalizedPrefix, "MATCHING");
        if (matchingIndex < 0)
        {
            throw CoreTokenReader.Error(
                "Portable Firebird UPDATE OR INSERT requires an explicit MATCHING column list; implicit primary-key matching is not canonicalized without source metadata.",
                tokens[0]);
        }

        var reader = new CoreTokenReader(normalizedPrefix[matchingIndex..]);
        var start = reader.Position;
        var matchingToken = reader.ExpectWord("MATCHING");
        reader.Expect(TokenType.LParen, "'(' before Firebird MATCHING column list");
        var targetColumns = ParseUniqueSinglePartColumns(reader, "Firebird MATCHING column");
        reader.Expect(TokenType.RParen, "')' after Firebird MATCHING column list");
        if (targetColumns.IsDefaultOrEmpty)
            throw CoreTokenReader.Error("Firebird MATCHING requires at least one explicit column.", matchingToken);
        ValidateTrailer(reader);

        var insertColumns = ParseInsertColumns(normalizedPrefix);
        var assignments = insertColumns
            .Select(column => new InsertConflictAssignment(column, column, column.Span))
            .ToImmutableArray();
        var conflict = new InsertConflictClause(
            targetColumns,
            InsertConflictActionKind.UpdateProposedValues,
            assignments,
            reader.SpanFrom(start));
        return (RemoveRange(normalizedPrefix, matchingIndex, reader.Position), conflict);
    }

    private static ImmutableArray<SqlIdentifier> ParseInsertColumns(Token[] normalizedInsertTokens)
    {
        var depth = 0;
        var listStart = -1;
        for (var i = 0; i < normalizedInsertTokens.Length; i++)
        {
            var token = normalizedInsertTokens[i];
            if (depth == 0 && token.Type == TokenType.LParen)
            {
                listStart = i;
                break;
            }
            if (CoreTokenReader.IsWord(token, "VALUES"))
                break;
            if (token.Type == TokenType.LParen) depth++;
            else if (token.Type == TokenType.RParen) depth = Math.Max(0, depth - 1);
        }

        if (listStart < 0)
        {
            throw CoreTokenReader.Error(
                "Portable Firebird UPDATE OR INSERT requires an explicit INSERT column list.",
                normalizedInsertTokens[0]);
        }

        var reader = new CoreTokenReader(normalizedInsertTokens[(listStart + 1)..]);
        var columns = ParseUniqueSinglePartColumns(reader, "Firebird UPDATE OR INSERT column");
        reader.Expect(TokenType.RParen, "')' after Firebird UPDATE OR INSERT column list");
        if (columns.IsDefaultOrEmpty)
            throw CoreTokenReader.Error("Firebird UPDATE OR INSERT requires at least one explicit column.", reader.Peek());
        return columns;
    }

    private static bool IsFirebirdUpdateOrInsertStart(Token[] tokens) =>
        tokens.Length >= 3
        && CoreTokenReader.IsWord(tokens[0], "UPDATE")
        && CoreTokenReader.IsWord(tokens[1], "OR")
        && CoreTokenReader.IsWord(tokens[2], "INSERT");

    private static int FindRootConflictClause(Token[] tokens)
    {
        var depth = 0;
        for (var i = 0; i + 1 < tokens.Length; i++)
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
            if (depth != 0 || !CoreTokenReader.IsWord(token, "ON"))
                continue;

            if (CoreTokenReader.IsWord(tokens[i + 1], "CONFLICT")
                || CoreTokenReader.IsWord(tokens[i + 1], "DUPLICATE"))
                return i;
        }
        return -1;
    }

    private static int FindRootClauseAfterValues(Token[] tokens, string word)
    {
        var depth = 0;
        var sawValues = false;
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
            if (depth != 0)
                continue;

            if (!sawValues && CoreTokenReader.IsWord(token, "VALUES"))
            {
                sawValues = true;
                continue;
            }
            if (sawValues && CoreTokenReader.IsWord(token, word))
                return i;
            if (sawValues && (CoreTokenReader.IsWord(token, "RETURNING")
                || token.Type is TokenType.Semicolon or TokenType.EOF))
                return -1;
        }
        return -1;
    }

    private static Token[] RemoveRange(Token[] tokens, int start, int count)
    {
        var normalized = new Token[tokens.Length - count];
        Array.Copy(tokens, 0, normalized, 0, start);
        Array.Copy(
            tokens,
            start + count,
            normalized,
            start,
            tokens.Length - start - count);
        return normalized;
    }

    private static ImmutableArray<SqlIdentifier> ParseUniqueSinglePartColumns(
        CoreTokenReader reader,
        string description)
    {
        var columns = ImmutableArray.CreateBuilder<SqlIdentifier>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            var token = reader.Peek();
            var column = reader.ParseIdentifierPath(description);
            if (column.Parts.Length != 1)
                throw CoreTokenReader.Error($"{description} must be unqualified.", token);
            if (!seen.Add(column.Parts[0].Value))
                throw CoreTokenReader.Error($"{description} '{column.Parts[0].Value}' is declared more than once.", token);
            columns.Add(column);
        } while (reader.Match(TokenType.Comma));
        return columns.ToImmutable();
    }

    private static ImmutableArray<InsertConflictAssignment> ParseAssignments(CoreTokenReader reader)
    {
        var assignments = ImmutableArray.CreateBuilder<InsertConflictAssignment>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            var assignmentStart = reader.Position;
            var targetToken = reader.Peek();
            var target = reader.ParseIdentifierPath("ON CONFLICT UPDATE target column");
            if (target.Parts.Length != 1)
                throw CoreTokenReader.Error("ON CONFLICT UPDATE target columns must be unqualified.", targetToken);
            if (!seenTargets.Add(target.Parts[0].Value))
                throw CoreTokenReader.Error($"ON CONFLICT UPDATE assigns column '{target.Parts[0].Value}' more than once.", targetToken);

            var equals = reader.Peek();
            if (equals.Type != TokenType.Operator || equals.Value != "=")
                throw CoreTokenReader.Error("Expected '=' in ON CONFLICT UPDATE assignment.", equals);
            reader.Advance();
            reader.ExpectWord("EXCLUDED");
            reader.Expect(TokenType.Dot, "'.' after EXCLUDED");
            var sourceToken = reader.ExpectIdentifier("proposed-row column after EXCLUDED.");
            var source = new SqlIdentifier(
                ImmutableArray.Create(CoreTokenReader.ToIdentifierPart(sourceToken)),
                CoreTokenReader.Span(sourceToken));
            assignments.Add(new InsertConflictAssignment(
                target,
                source,
                reader.SpanFrom(assignmentStart)));

            if (reader.Match(TokenType.Comma))
                continue;
            break;
        } while (true);

        if (assignments.Count == 0)
            throw CoreTokenReader.Error("ON CONFLICT DO UPDATE requires at least one assignment.", reader.Peek());
        return assignments.ToImmutable();
    }

    private static void ValidateTrailer(CoreTokenReader reader)
    {
        var token = reader.Peek();
        if (token.Type is TokenType.EOF or TokenType.Semicolon || reader.PeekWord("RETURNING"))
            return;
        throw CoreTokenReader.Error(
            "Portable conflict handling supports only the canonical conflict clause followed directly by optional RETURNING; provider-specific predicates, ORDER BY, ROWS, and extra clauses remain fail-closed.",
            token);
    }

    private static void ValidateOnConflictSourceContract(
        SqlAgentToolType sourceDialect,
        Version? sourceServerVersion,
        Token token)
    {
        var error = SqlDmlUpsertCapabilityRules.OnConflictSourceValidationError(
            sourceDialect,
            sourceServerVersion);
        if (error is not null)
            throw CoreTokenReader.Error(error, token);
    }
}
