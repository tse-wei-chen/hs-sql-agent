using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Ast;

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

public sealed record SelectItem : SqlNode
{
    public SelectItem(SqlExpr Expression, IdentifierPart? Alias, SourceSpan Span)
        : base(Span)
    {
        this.Expression = Expression;
        this.Alias = Alias is not null && Alias.Span == SourceSpan.Unknown
            ? Alias with { PreserveSpelling = true }
            : Alias;
    }

    public SqlExpr Expression { get; init; }
    public IdentifierPart? Alias { get; init; }

    public void Deconstruct(
        out SqlExpr Expression,
        out IdentifierPart? Alias,
        out SourceSpan Span)
    {
        Expression = this.Expression;
        Alias = this.Alias;
        Span = this.Span;
    }
}

public sealed record OrderByItem(
    SqlExpr Expression,
    bool Descending,
    NullOrderingKind NullOrdering,
    SourceSpan Span) : SqlNode(Span);

/// <summary>
/// Internal semantic marker for statement-level ORDER BY output positions (for example ORDER BY 2).
/// It is deliberately not a public SQL value type: the parser manufactures it only for a bare
/// unsigned integer in a query tail, and validation guarantees that it cannot escape ORDER BY.
/// </summary>
internal sealed record OrderByOrdinalValue(int Position);

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
    SourceSpan Span) : SqlStatement(Span)
{
    public ImmutableArray<SqlIdentifier> Returning { get; init; } = ImmutableArray<SqlIdentifier>.Empty;
}

public sealed record DeleteStatement(
    NamedTableSource Target,
    SqlExpr? Predicate,
    SourceSpan Span) : SqlStatement(Span)
{
    public ImmutableArray<SqlIdentifier> Returning { get; init; } = ImmutableArray<SqlIdentifier>.Empty;
}

public abstract record InsertSource(SourceSpan Span) : SqlNode(Span);

public sealed record InsertValuesSource(
    ImmutableArray<ImmutableArray<SqlExpr>> Rows,
    SourceSpan Span) : InsertSource(Span);

public sealed record InsertQuerySource(
    SqlStatement Query,
    SourceSpan Span) : InsertSource(Span);

public enum InsertConflictActionKind
{
    DoNothing,
    UpdateProposedValues
}

/// <summary>
/// One deterministic portable upsert assignment. The right-hand side is a column from the row
/// proposed for insertion (PostgreSQL/SQLite EXCLUDED), not an arbitrary SQL expression.
/// </summary>
public sealed record InsertConflictAssignment(
    SqlIdentifier Column,
    SqlIdentifier ProposedColumn,
    SourceSpan Span) : SqlNode(Span);

/// <summary>
/// Portable explicit-target INSERT conflict contract. The target is always a concrete column list;
/// provider-native any-unique-key behavior and general MERGE source semantics are deliberately not
/// represented by this node.
/// </summary>
public sealed record InsertConflictClause(
    ImmutableArray<SqlIdentifier> TargetColumns,
    InsertConflictActionKind Action,
    ImmutableArray<InsertConflictAssignment> Assignments,
    SourceSpan Span) : SqlNode(Span);

public sealed record InsertStatement(
    NamedTableSource Target,
    ImmutableArray<SqlIdentifier> Columns,
    InsertSource Source,
    SourceSpan Span) : SqlStatement(Span)
{
    public InsertConflictClause? Conflict { get; init; }
    public ImmutableArray<SqlIdentifier> Returning { get; init; } = ImmutableArray<SqlIdentifier>.Empty;
}
