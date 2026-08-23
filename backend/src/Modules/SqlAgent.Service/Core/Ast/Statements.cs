using System.Collections.Immutable;

namespace SqlAgent.Service.Core.Ast;

public abstract record TableSource(SourceSpan Span) : SqlNode(Span);

public sealed record NamedTableSource(
    SqlIdentifier Name,
    IdentifierPart? Alias,
    SourceSpan Span) : TableSource(Span);

public sealed record DerivedTableSource(
    SqlStatement Query,
    IdentifierPart Alias,
    SourceSpan Span) : TableSource(Span)
{
    /// <summary>
    /// Structured DTOs and programmatic callers that supply a plain alias string have no source
    /// quote metadata. Represent that alias explicitly as unquoted rather than flowing a nullable
    /// user-defined conversion into this required-alias boundary.
    /// </summary>
    public DerivedTableSource(SqlStatement query, string alias, SourceSpan span)
        : this(
            query,
            new IdentifierPart(
                string.IsNullOrWhiteSpace(alias)
                    ? throw new ArgumentException("Derived table alias cannot be empty.", nameof(alias))
                    : alias.Trim(),
                WasQuoted: false,
                span),
            span)
    {
    }
}

public sealed record SelectItem(SqlExpr Expression, IdentifierPart? Alias, SourceSpan Span) : SqlNode(Span)
{
    /// <summary>
    /// A plain programmatic projection alias is an API/result-set name rather than source SQL text.
    /// Keep it unquoted in the AST while preserving its requested output spelling during lowering.
    /// Parser-native aliases continue to pass an IdentifierPart with lexical quote metadata.
    /// </summary>
    public SelectItem(SqlExpr expression, string? alias, SourceSpan span)
        : this(
            expression,
            string.IsNullOrWhiteSpace(alias)
                ? null
                : new IdentifierPart(
                    alias.Trim(),
                    WasQuoted: false,
                    span,
                    PreserveSpelling: true),
            span)
    {
    }
}

public sealed record OrderByItem(
    SqlExpr Expression,
    bool Descending,
    NullOrderingKind NullOrdering,
    SourceSpan Span) : SqlNode(Span);

public enum NullOrderingKind
{
    Default,
    First,
    Last
}

public sealed record JoinSource(
    string Kind,
    TableSource Source,
    SqlExpr? Predicate,
    SourceSpan Span) : SqlNode(Span);

public sealed record CteDefinition(
    SqlIdentifier Name,
    ImmutableArray<SqlIdentifier> ColumnAliases,
    SqlStatement Query,
    SourceSpan Span) : SqlNode(Span);

public sealed record SelectStatement(
    ImmutableArray<CteDefinition> Ctes,
    bool Distinct,
    ImmutableArray<SelectItem> Select,
    TableSource? From,
    ImmutableArray<JoinSource> Joins,
    SqlExpr? Where,
    ImmutableArray<SqlExpr> GroupBy,
    SqlExpr? Having,
    ImmutableArray<OrderByItem> OrderBy,
    int? Limit,
    int? Offset,
    SourceSpan Span) : SqlStatement(Span);

public sealed record Assignment(
    SqlIdentifier Column,
    SqlExpr Value,
    SourceSpan Span) : SqlNode(Span);

public sealed record UpdateStatement(
    NamedTableSource Target,
    ImmutableArray<Assignment> Assignments,
    SqlExpr? Predicate,
    SourceSpan Span) : SqlStatement(Span);

public sealed record DeleteStatement(
    NamedTableSource Target,
    SqlExpr? Predicate,
    SourceSpan Span) : SqlStatement(Span);

public abstract record InsertSource(SourceSpan Span) : SqlNode(Span);

public sealed record InsertValuesSource(
    ImmutableArray<ImmutableArray<SqlExpr>> Rows,
    SourceSpan Span) : InsertSource(Span);

public sealed record InsertQuerySource(
    SqlStatement Query,
    SourceSpan Span) : InsertSource(Span);

public sealed record InsertStatement(
    NamedTableSource Target,
    ImmutableArray<SqlIdentifier> Columns,
    InsertSource Source,
    SourceSpan Span) : SqlStatement(Span);
