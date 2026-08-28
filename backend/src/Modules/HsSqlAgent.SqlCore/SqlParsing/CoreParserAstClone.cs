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


    internal static SqlExpr WithSpan(
        SqlExpr source,
        SourceSpan span) =>
        source with { Span = span };

    internal static FunctionCallExpr Function(
        SqlIdentifier name,
        System.Collections.Immutable.ImmutableArray<SqlExpr> arguments,
        bool distinct,
        SourceSpan span,
        System.Collections.Immutable.ImmutableArray<OrderByItem> aggregateOrderBy,
        AggregateOrderSyntaxKind aggregateOrderSyntax,
        string? aggregateSeparatorClause) =>
        new(name, arguments, distinct, span)
        {
            AggregateOrderBy = aggregateOrderBy,
            AggregateOrderSyntax = aggregateOrderSyntax,
            AggregateSeparatorClause = aggregateSeparatorClause
        };

    internal static InsertStatement Insert(
        NamedTableSource target,
        System.Collections.Immutable.ImmutableArray<SqlIdentifier> columns,
        InsertSource source,
        System.Collections.Immutable.ImmutableArray<DmlReturningItem> returning,
        SourceSpan span) =>
        new(target, columns, source, span)
        {
            Returning = returning
        };

    internal static UpdateStatement Update(
        NamedTableSource target,
        System.Collections.Immutable.ImmutableArray<Assignment> assignments,
        SqlExpr? predicate,
        System.Collections.Immutable.ImmutableArray<NamedTableSource> from,
        System.Collections.Immutable.ImmutableArray<DmlReturningItem> returning,
        SourceSpan span) =>
        new(target, assignments, predicate, span)
        {
            From = from,
            Returning = returning
        };

    internal static DeleteStatement Delete(
        NamedTableSource target,
        SqlExpr? predicate,
        System.Collections.Immutable.ImmutableArray<NamedTableSource> usingSources,
        System.Collections.Immutable.ImmutableArray<DmlReturningItem> returning,
        SourceSpan span) =>
        new(target, predicate, span)
        {
            Using = usingSources,
            Returning = returning
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
