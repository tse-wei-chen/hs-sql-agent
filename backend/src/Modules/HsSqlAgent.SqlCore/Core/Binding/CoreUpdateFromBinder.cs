using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Binding;

/// <summary>
/// Binds PostgreSQL-style UPDATE ... FROM by projecting its mutation scope through the existing
/// SELECT binder. This keeps table/alias authorization facts and column resolution centralized in
/// SqlAstBinder instead of introducing a second DML-specific scope engine.
/// </summary>
internal sealed class CoreUpdateFromBinder(SqlAstBinder queryBinder)
{
    private readonly SqlAstBinder _queryBinder = queryBinder;

    public BoundStatement Bind(ParsedStatement statement, UpdateStatement update)
    {
        var projection = ImmutableArray.CreateBuilder<SelectItem>();
        foreach (var assignment in update.Assignments)
            projection.Add(new SelectItem(assignment.Value, null, assignment.Span));
        if (update.Predicate is not null)
            projection.Add(new SelectItem(update.Predicate, null, update.Predicate.Span));

        var joins = update.From
            .Select(source => new JoinSource("CROSS", source, null, source.Span))
            .ToImmutableArray();
        var carrier = new SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            Distinct: false,
            projection.ToImmutable(),
            update.Target,
            joins,
            Where: null,
            ImmutableArray<SqlExpr>.Empty,
            Having: null,
            ImmutableArray<OrderByItem>.Empty,
            Limit: null,
            Offset: null,
            update.Span);

        var boundCarrier = _queryBinder.Bind(new ParsedStatement(carrier, statement.SourceDialect));
        var boundSelect = (SelectStatement)boundCarrier.Statement;
        var assignments = update.Assignments
            .Select((assignment, index) => assignment with
            {
                Value = boundSelect.Select[index].Expression
            })
            .ToImmutableArray();
        var predicate = update.Predicate is null
            ? null
            : boundSelect.Select[update.Assignments.Length].Expression;

        return new BoundStatement(
            update with { Assignments = assignments, Predicate = predicate },
            boundCarrier.Facts,
            statement.SourceDialect);
    }
}
