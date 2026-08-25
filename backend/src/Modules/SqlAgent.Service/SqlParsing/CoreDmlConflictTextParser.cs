using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Enums;

namespace SqlAgent.Service.SqlParsing;

/// <summary>
/// Extracts the deliberately small portable INSERT conflict grammar before the ordinary DML parser
/// consumes the statement. Keeping this clause separate avoids teaching the general expression
/// parser about the special EXCLUDED row scope.
/// </summary>
internal static class CoreDmlConflictTextParser
{
    public static (Token[] Tokens, InsertConflictClause? Conflict) Extract(
        Token[] tokens,
        SqlAgentToolType sourceDialect,
        Version? sourceServerVersion)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Length == 0 || !CoreTokenReader.IsWord(tokens[0], "INSERT"))
            return (tokens, null);

        var onIndex = FindRootConflictStartAfterValues(tokens);
        if (onIndex < 0)
            return (tokens, null);

        var slice = tokens[onIndex..];
        var reader = new CoreTokenReader(slice);
        var start = reader.Position;
        var onToken = reader.ExpectWord("ON");

        if (!reader.MatchWord("CONFLICT"))
        {
            if (sourceDialect == SqlAgentToolType.MySQL && reader.PeekWord("DUPLICATE"))
            {
                throw CoreTokenReader.Error(
                    "MySQL ON DUPLICATE KEY UPDATE has no explicit conflict target, so Core cannot translate it to the deterministic portable ON CONFLICT contract.",
                    onToken);
            }

            throw CoreTokenReader.Error(
                "Portable INSERT conflict handling requires an explicit ON CONFLICT clause.",
                onToken);
        }

        ValidateSourceContract(sourceDialect, sourceServerVersion, onToken);
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
        var normalized = new Token[tokens.Length - consumed];
        Array.Copy(tokens, 0, normalized, 0, onIndex);
        Array.Copy(
            tokens,
            onIndex + consumed,
            normalized,
            onIndex,
            tokens.Length - onIndex - consumed);
        return (normalized, conflict);
    }

    private static int FindRootConflictStartAfterValues(Token[] tokens)
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
            if (sawValues && CoreTokenReader.IsWord(token, "ON"))
                return i;
            if (sawValues && (CoreTokenReader.IsWord(token, "RETURNING")
                || token.Type is TokenType.Semicolon or TokenType.EOF))
                return -1;
        }
        return -1;
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
            var excluded = reader.ExpectWord("EXCLUDED");
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
            "Portable ON CONFLICT supports only DO NOTHING or assignments of the exact form target = EXCLUDED.source; arbitrary update expressions and predicates remain fail-closed.",
            token);
    }

    private static void ValidateSourceContract(
        SqlAgentToolType sourceDialect,
        Version? sourceServerVersion,
        Token token)
    {
        switch (sourceDialect)
        {
            case SqlAgentToolType.Postgres:
                return;
            case SqlAgentToolType.Sqlite when IsAtLeast(sourceServerVersion, 3, 24):
                return;
            case SqlAgentToolType.Sqlite:
                throw CoreTokenReader.Error(
                    "Raw SQLite UPSERT requires a source capability profile with ServerVersion 3.24 or newer.",
                    token);
            case SqlAgentToolType.MySQL:
                throw CoreTokenReader.Error(
                    "MySQL ON DUPLICATE KEY UPDATE has no explicit conflict target and is not represented by the deterministic portable upsert contract.",
                    token);
            case SqlAgentToolType.MsSqlServer:
            case SqlAgentToolType.Oracle:
            case SqlAgentToolType.Firebird:
                throw CoreTokenReader.Error(
                    $"Source dialect {sourceDialect} uses MERGE-style upsert semantics, which require a separate source-row cardinality contract and remain fail-closed.",
                    token);
            default:
                throw CoreTokenReader.Error(
                    $"Portable INSERT conflict handling is not represented for source dialect {sourceDialect}.",
                    token);
        }
    }

    private static bool IsAtLeast(Version? actual, int major, int minor) =>
        actual is not null && actual.CompareTo(new Version(major, minor)) >= 0;
}
