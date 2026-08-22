using System.Collections.Immutable;

namespace SqlAgent.Service.Core.Ast;

public abstract record TableSource(SourceSpan Span) : SqlNode(Span);

public sealed record NamedTableSource(
    SqlIdentifier Name,
    string? Alias,
    SourceSpan Span) : TableSource(Span);

public sealed record DerivedTableSource(
    SqlStatement Query,
    string Alias,
    SourceSpan Span) : TableSource(Span);

public sealed record SelectItem(SqlExpr Expression, string? Alias, SourceSpan Span) : SqlNode(Span);

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
