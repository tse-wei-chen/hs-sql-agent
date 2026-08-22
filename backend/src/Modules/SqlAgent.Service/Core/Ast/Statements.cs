using System.Collections.Immutable;

namespace SqlAgent.Service.Core.Ast;

public abstract record TableSource(SourceSpan Span) : SqlNode(Span);

public sealed record NamedTableSource(
    SqlIdentifier Name,
    string? Alias,
    SourceSpan Span) : TableSource(Span);

public sealed record DerivedTableSource(
    SelectStatement Query,
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
    SelectStatement Query,
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
