namespace HsSqlAgent.SqlCore.Core.Binding;

public sealed record TableSymbol(
    string Name,
    string? Alias,
    bool IsDerived,
    bool IsCte,
    SourceSpan Span)
{
    public string VisibleName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;
}

public sealed record BoundColumnExpr(
    SqlIdentifier Name,
    TableSymbol? Source,
    SourceSpan Span) : SqlExpr(Span)
{
    /// <summary>
    /// True when this column resolved through a parent query scope rather than the scope that owns
    /// the expression. The marker preserves correlation provenance after binding so provider-specific
    /// capability validation can distinguish local columns from outer references without re-resolving
    /// identifiers from rendered SQL text.
    /// </summary>
    public bool IsOuterReference { get; init; }
}
