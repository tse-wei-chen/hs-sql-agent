using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Binding;

/// <summary>
/// Binds PostgreSQL-style DELETE ... USING through the existing SELECT scope engine so mutation
/// predicates see the delete target and every USING source under the same authorization rules.
/// </summary>
internal sealed class CoreDeleteUsingBinder(SqlAstBinder queryBinder)
{
    private readonly SqlAstBinder _queryBinder = queryBinder;

    public BoundStatement Bind(ParsedStatement statement, DeleteStatement delete)
    {
        var predicate = delete.Predicate
            ?? throw new InvalidOperationException("DELETE ... USING requires a predicate before binding.");
        var joins = delete.Using
            .Select(source => new JoinSource("CROSS", source, null, source.Span))
            .ToImmutableArray();
        var carrier = new SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            Distinct: false,
            ImmutableArray.Create(new SelectItem(predicate, null, predicate.Span)),
            delete.Target,
            joins,
            Where: null,
            ImmutableArray<SqlExpr>.Empty,
            Having: null,
            ImmutableArray<OrderByItem>.Empty,
            Limit: null,
            Offset: null,
            delete.Span);

        var boundCarrier = _queryBinder.Bind(new ParsedStatement(carrier, statement.SourceDialect));
        var boundSelect = (SelectStatement)boundCarrier.Statement;
        return new BoundStatement(
            delete with { Predicate = boundSelect.Select[0].Expression },
            boundCarrier.Facts,
            statement.SourceDialect);
    }
}
