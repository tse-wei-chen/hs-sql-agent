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
    SourceSpan Span) : SqlExpr(Span);
