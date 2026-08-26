using System.Collections.Immutable;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;

namespace HsSqlAgent.SqlCore.Core.Pipeline;

/// <summary>
/// Adapts INSERT VALUES cells to existing Core query/update expression pipelines without changing
/// their SQL semantics. Binding/normalization use a SELECT-without-FROM carrier so scalar
/// expressions and subqueries reuse the normal expression traversal. Validation uses UPDATE
/// assignment context so aggregates/window expressions remain illegal in VALUES while ordinary
/// scalar expressions stay supported.
/// </summary>
internal static class CoreInsertValuesCarrier
{
    private const string ValidationTarget = "__core_insert_values";

    public static SelectStatement CreateExpressionCarrier(InsertValuesSource values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.Rows
            .SelectMany(row => row)
            .Select(value => new SelectItem(value, Alias: null, value.Span))
            .ToImmutableArray();

        return new SelectStatement(
            Ctes: ImmutableArray<CteDefinition>.Empty,
            Distinct: false,
            Select: items,
            From: null,
            Joins: ImmutableArray<JoinSource>.Empty,
            Where: null,
            GroupBy: ImmutableArray<SqlExpr>.Empty,
            Having: null,
            OrderBy: ImmutableArray<OrderByItem>.Empty,
            Limit: null,
            Offset: null,
            Span: values.Span);
    }

    public static UpdateStatement CreateValidationCarrier(InsertValuesSource values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var index = 0;
        var assignments = values.Rows
            .SelectMany(row => row)
            .Select(value => new Assignment(
                SqlIdentifier.Unquoted($"v{index++}", SourceSpan.Unknown),
                value,
                value.Span))
            .ToImmutableArray();

        var targetName = SqlIdentifier.Unquoted(ValidationTarget, SourceSpan.Unknown);
        return new UpdateStatement(
            new NamedTableSource(targetName, Alias: null, SourceSpan.Unknown),
            assignments,
            Predicate: null,
            values.Span);
    }

    public static InsertValuesSource RestoreFromExpressionCarrier(
        InsertValuesSource template,
        SqlStatement carrier)
    {
        if (carrier is not SelectStatement select)
        {
            throw new SqlCompilationException(
                $"INSERT VALUES expression carrier returned '{carrier.GetType().Name}' instead of SelectStatement.");
        }

        return Restore(template, select.Select.Select(item => item.Expression));
    }

    public static InsertValuesSource RestoreFromValidationCarrier(
        InsertValuesSource template,
        SqlStatement carrier)
    {
        if (carrier is not UpdateStatement update)
        {
            throw new SqlCompilationException(
                $"INSERT VALUES validation carrier returned '{carrier.GetType().Name}' instead of UpdateStatement.");
        }

        return Restore(template, update.Assignments.Select(assignment => assignment.Value));
    }

    private static InsertValuesSource Restore(
        InsertValuesSource template,
        IEnumerable<SqlExpr> expressions)
    {
        var flattened = expressions.ToArray();
        var expected = template.Rows.Sum(row => row.Length);
        if (flattened.Length != expected)
        {
            throw new SqlCompilationException(
                $"INSERT VALUES carrier returned {flattened.Length} expression(s); expected {expected}.");
        }

        var offset = 0;
        var rows = template.Rows
            .Select(row =>
            {
                var restored = flattened
                    .Skip(offset)
                    .Take(row.Length)
                    .ToImmutableArray();
                offset += row.Length;
                return restored;
            })
            .ToImmutableArray();

        return template with { Rows = rows };
    }
}
