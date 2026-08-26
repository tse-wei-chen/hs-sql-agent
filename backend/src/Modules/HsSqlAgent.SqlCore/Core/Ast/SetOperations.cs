using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Ast;

public enum SetOperationKind
{
    Union,
    UnionAll,
    Intersect,
    Except
}

public sealed record SetOperation(
    SetOperationKind Kind,
    SqlStatement Query,
    SourceSpan Span) : SqlNode(Span);

public sealed record QueryStatement(
    SelectStatement Head,
    ImmutableArray<SetOperation> SetOperations,
    ImmutableArray<OrderByItem> OrderBy,
    int? Limit,
    int? Offset,
    SourceSpan Span) : SqlStatement(Span);
