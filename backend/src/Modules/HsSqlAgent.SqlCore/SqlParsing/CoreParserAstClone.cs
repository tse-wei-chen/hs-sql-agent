namespace HsSqlAgent.SqlCore.SqlParsing;

/// <summary>
/// Temporary C# record-clone seam for the F# raw-SQL parser entry point.
/// Delete when the parser AST records themselves move to F#.
/// </summary>
internal static class CoreParserAstClone
{
    internal static SelectStatement CompleteSelect(
        SelectStatement source,
        System.Collections.Immutable.ImmutableArray<OrderByItem> orderBy,
        int? limit,
        int? offset,
        SourceSpan span) =>
        source with
        {
            OrderBy = orderBy,
            Limit = limit,
            Offset = offset,
            Span = span
        };


    internal static SqlStatement AttachInsertConflict(
        SqlStatement statement,
        InsertConflictClause? conflict)
    {
        if (conflict is null)
            return statement;

        if (statement is not InsertStatement insert)
        {
            throw new SqlParseException(
                "INSERT conflict extraction must attach to a canonical INSERT statement.");
        }

        return insert with { Conflict = conflict };
    }
}
