using System.Collections.Immutable;

namespace SqlAgent.Service.Core.Ast;

public enum SetOperationKind
{
    Union,
    UnionAll,
    Intersect,
    Except
}

public sealed record SetOperation(
    SetOperationKind Kind,
    SelectStatement Query,
    SourceSpan Span) : SqlNode(Span);

public sealed record QueryStatement(
    SelectStatement Head,
    ImmutableArray<SetOperation> SetOperations,
    ImmutableArray<OrderByItem> OrderBy,
    int? Limit,
    int? Offset,
    SourceSpan Span) : SqlStatement(Span);
