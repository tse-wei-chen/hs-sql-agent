using System.Collections.Immutable;

namespace SqlAgent.Service.Core.Ast;

/// <summary>
/// Source location carried by compiler nodes. Offsets are zero-based and End is exclusive.
/// </summary>
public readonly record struct SourceSpan(int Start, int End)
{
    public static SourceSpan Unknown => new(-1, -1);
}

public abstract record SqlNode(SourceSpan Span);

public abstract record SqlStatement(SourceSpan Span) : SqlNode(Span);

public abstract record SqlExpr(SourceSpan Span) : SqlNode(Span);

public sealed record SqlIdentifier(
    ImmutableArray<IdentifierPart> Parts,
    SourceSpan Span) : SqlNode(Span)
{
    public static SqlIdentifier Unquoted(string value, SourceSpan span = default) =>
        new([new IdentifierPart(value, false, span)], span);
}

public sealed record IdentifierPart(
    string Value,
    bool WasQuoted,
    SourceSpan Span,
    bool PreserveSpelling = false)
{
    /// <summary>
    /// Compatibility conversion for structured DTOs and programmatic AST construction. A plain
    /// string has no quote-intent metadata, so it is represented explicitly as an unquoted alias.
    /// Parser-native SQL must construct IdentifierPart from the source token instead.
    /// </summary>
    public static implicit operator IdentifierPart?(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : new IdentifierPart(value.Trim(), WasQuoted: false, SourceSpan.Unknown);
}

public sealed record LiteralExpr(object? Value, SourceSpan Span) : SqlExpr(Span);

public sealed record ColumnExpr(SqlIdentifier Name, SourceSpan Span) : SqlExpr(Span);

public sealed record UnaryExpr(string Operator, SqlExpr Operand, SourceSpan Span) : SqlExpr(Span);

public sealed record BinaryExpr(SqlExpr Left, string Operator, SqlExpr Right, SourceSpan Span) : SqlExpr(Span);

public sealed record FunctionCallExpr(
    SqlIdentifier Name,
    ImmutableArray<SqlExpr> Arguments,
    bool IsDistinct,
    SourceSpan Span) : SqlExpr(Span);

/// <summary>
/// SQL aggregate FILTER modifier. Kept as a wrapper instead of a FunctionCallExpr field so the
/// canonical expression model can represent/validate modifier ordering explicitly.
/// </summary>
public sealed record FilterExpr(
    SqlExpr Expression,
    SqlExpr Predicate,
    SourceSpan Span) : SqlExpr(Span);

public sealed record WindowedExpr(
    SqlExpr Expression,
    WindowSpec Window,
    SourceSpan Span) : SqlExpr(Span);

public sealed record WindowSpec(
    ImmutableArray<SqlExpr> PartitionBy,
    ImmutableArray<OrderByItem> OrderBy,
    WindowFrame? Frame,
    SourceSpan Span) : SqlNode(Span);

public enum WindowFrameUnitKind
{
    Rows,
    Range
}

public enum WindowFrameBoundKindCore
{
    UnboundedPreceding,
    Preceding,
    CurrentRow,
    Following,
    UnboundedFollowing
}

public sealed record WindowFrameBoundCore(
    WindowFrameBoundKindCore Kind,
    int? Offset,
    SourceSpan Span) : SqlNode(Span);

public sealed record WindowFrame(
    WindowFrameUnitKind Unit,
    WindowFrameBoundCore Start,
    WindowFrameBoundCore? End,
    SourceSpan Span) : SqlNode(Span);

public sealed record CastExpr(SqlExpr Expression, string TypeName, SourceSpan Span) : SqlExpr(Span);

public sealed record IntervalExpr(string Literal, SourceSpan Span) : SqlExpr(Span);

public sealed record CaseBranch(SqlExpr Condition, SqlExpr Value);

public sealed record CaseExpr(
    ImmutableArray<CaseBranch> Branches,
    SqlExpr? ElseExpression,
    SourceSpan Span) : SqlExpr(Span);

public sealed record InExpr(
    SqlExpr Value,
    ImmutableArray<SqlExpr> Items,
    bool IsNegated,
    SourceSpan Span) : SqlExpr(Span);

public sealed record BetweenExpr(
    SqlExpr Value,
    SqlExpr Lower,
    SqlExpr Upper,
    bool IsNegated,
    SourceSpan Span) : SqlExpr(Span);

public sealed record IsNullExpr(SqlExpr Value, bool IsNegated, SourceSpan Span) : SqlExpr(Span);

public sealed record SubqueryExpr(SqlStatement Query, SourceSpan Span) : SqlExpr(Span);

public sealed record ExistsExpr(SqlStatement Query, bool IsNegated, SourceSpan Span) : SqlExpr(Span);
