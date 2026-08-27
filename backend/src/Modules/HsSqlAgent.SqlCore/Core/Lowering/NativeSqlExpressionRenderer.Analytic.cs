using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Aggregate FILTER, aggregate-local ordering, and window-function rendering. This module owns the
/// analytic SQL shape while reusing the core renderer's canonical expression, binding, and literal
/// safety helpers.
/// </summary>
internal static partial class NativeSqlExpressionRenderer
{
    private static NativeSqlFragment RenderStringAggregate(
        FunctionCallExpr function,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        RequireArguments(function, 2);
        if (function.IsDistinct)
        {
            throw new SqlCompilationException(
                "Canonical CORE_STRING_AGG DISTINCT semantics are not enabled.");
        }

        var value = Render(
            function.Arguments[0],
            provider,
            renderSubquery);
        var separator = provider == SqlAgentToolType.Postgres
            ? Bind(StringLiteralValue(
                function.Arguments[1],
                "string aggregate separator"))
            : new NativeSqlFragment(
                SqlStringLiteral(
                    function.Arguments[1],
                    "string aggregate separator",
                    provider),
                ImmutableArray<object?>.Empty);

        if (!function.AggregateOrderBy.IsDefaultOrEmpty)
        {
            var ordering = RenderOrderByClause(
                function.AggregateOrderBy,
                provider,
                renderSubquery,
                "aggregate");
            var orderedSql = provider switch
            {
                SqlAgentToolType.Postgres =>
                    "STRING_AGG(" + value.Sql + ", " + separator.Sql + " " +
                    ordering.Sql + ")",
                SqlAgentToolType.Sqlite =>
                    "GROUP_CONCAT(" + value.Sql + ", " + separator.Sql + " " +
                    ordering.Sql + ")",
                SqlAgentToolType.MsSqlServer =>
                    "STRING_AGG(" + value.Sql + ", " + separator.Sql +
                    ") WITHIN GROUP (" + ordering.Sql + ")",
                SqlAgentToolType.Oracle =>
                    "LISTAGG(" + value.Sql + ", " + separator.Sql +
                    ") WITHIN GROUP (" + ordering.Sql + ")",
                SqlAgentToolType.MySQL =>
                    "GROUP_CONCAT(" + value.Sql + " " + ordering.Sql +
                    " SEPARATOR " + separator.Sql + ")",
                _ => throw new SqlCompilationException(
                    "Aggregate-local ORDER BY lowering is not supported by " +
                    provider + ".")
            };

            return new NativeSqlFragment(
                orderedSql,
                value.Bindings
                    .Concat(separator.Bindings)
                    .Concat(ordering.Bindings)
                    .ToImmutableArray());
        }

        var sql = provider switch
        {
            SqlAgentToolType.MsSqlServer or SqlAgentToolType.Postgres =>
                "STRING_AGG(" + value.Sql + ", " + separator.Sql + ")",
            SqlAgentToolType.MySQL =>
                "GROUP_CONCAT(" + value.Sql + " SEPARATOR " + separator.Sql + ")",
            SqlAgentToolType.Sqlite =>
                "GROUP_CONCAT(" + value.Sql + ", " + separator.Sql + ")",
            SqlAgentToolType.Oracle =>
                "LISTAGG(" + value.Sql + ", " + separator.Sql + ")",
            SqlAgentToolType.Firebird =>
                "LIST(" + value.Sql + ", " + separator.Sql + ")",
            _ => throw new SqlCompilationException(
                "Unsupported string aggregate provider.")
        };

        return new NativeSqlFragment(
            sql,
            value.Bindings
                .Concat(separator.Bindings)
                .ToImmutableArray());
    }

    private static NativeSqlFragment RenderFilter(
        FilterExpr filter,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        if (provider is not (
            SqlAgentToolType.Postgres or
            SqlAgentToolType.Sqlite or
            SqlAgentToolType.Oracle or
            SqlAgentToolType.Firebird))
        {
            throw new SqlCompilationException(
                "FILTER lowering is not supported by " + provider + ".");
        }

        var expression = Render(
            filter.Expression,
            provider,
            renderSubquery);
        var predicate = RenderPredicate(
            filter.Predicate,
            provider,
            renderSubquery);
        return new NativeSqlFragment(
            expression.Sql + " FILTER (WHERE " + predicate.Sql + ")",
            expression.Bindings
                .Concat(predicate.Bindings)
                .ToImmutableArray());
    }

    private static NativeSqlFragment RenderOrderByClause(
        ImmutableArray<OrderByItem> orderBy,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery,
        string context)
    {
        var orderParts = new List<string>();
        var bindings = ImmutableArray.CreateBuilder<object?>();

        foreach (var item in orderBy)
        {
            var rendered = Render(
                item.Expression,
                provider,
                renderSubquery);
            var sql = rendered.Sql + (item.Descending ? " DESC" : " ASC");
            sql += item.NullOrdering switch
            {
                NullOrderingKind.Default => string.Empty,
                NullOrderingKind.First => " NULLS FIRST",
                NullOrderingKind.Last => " NULLS LAST",
                _ => throw new SqlCompilationException(
                    "Unsupported NULL ordering '" + item.NullOrdering +
                    "' in " + context + ".")
            };

            orderParts.Add(sql);
            bindings.AddRange(rendered.Bindings);
        }

        return new NativeSqlFragment(
            "ORDER BY " + string.Join(", ", orderParts),
            bindings.ToImmutable());
    }

    private static NativeSqlFragment RenderWindowed(
        WindowedExpr windowed,
        SqlAgentToolType provider,
        Func<SqlStatement, NativeSqlFragment> renderSubquery)
    {
        var capabilityError = SqlWindowCapabilityRules.WindowValidationError(
            windowed,
            provider);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var expression = Render(
            windowed.Expression,
            provider,
            renderSubquery);
        var parts = new List<string>();
        var bindings = ImmutableArray.CreateBuilder<object?>();
        bindings.AddRange(expression.Bindings);

        if (!windowed.Window.PartitionBy.IsDefaultOrEmpty)
        {
            var partition = windowed.Window.PartitionBy
                .Select(item => Render(item, provider, renderSubquery))
                .ToArray();
            parts.Add(
                "PARTITION BY " +
                string.Join(", ", partition.Select(item => item.Sql)));
            foreach (var item in partition)
                bindings.AddRange(item.Bindings);
        }

        if (!windowed.Window.OrderBy.IsDefaultOrEmpty)
        {
            var ordering = RenderOrderByClause(
                windowed.Window.OrderBy,
                provider,
                renderSubquery,
                "window");
            parts.Add(ordering.Sql);
            bindings.AddRange(ordering.Bindings);
        }

        if (windowed.Window.Frame is not null)
            parts.Add(RenderWindowFrame(windowed.Window.Frame));

        return new NativeSqlFragment(
            expression.Sql + " OVER (" + string.Join(" ", parts) + ")",
            bindings.ToImmutable());
    }

    private static string RenderWindowFrame(WindowFrame frame)
    {
        var unit = frame.Unit switch
        {
            WindowFrameUnitKind.Rows => "ROWS",
            WindowFrameUnitKind.Range => "RANGE",
            _ => throw new SqlCompilationException(
                "Unsupported window frame unit '" + frame.Unit + "'.")
        };

        var start = RenderWindowBound(frame.Start);
        return frame.End is null
            ? unit + " " + start
            : unit + " BETWEEN " + start + " AND " +
              RenderWindowBound(frame.End);
    }

    private static string RenderWindowBound(
        WindowFrameBoundCore bound) => bound.Kind switch
    {
        WindowFrameBoundKindCore.UnboundedPreceding =>
            "UNBOUNDED PRECEDING",
        WindowFrameBoundKindCore.Preceding when bound.Offset is >= 0 =>
            bound.Offset.Value + " PRECEDING",
        WindowFrameBoundKindCore.CurrentRow =>
            "CURRENT ROW",
        WindowFrameBoundKindCore.Following when bound.Offset is >= 0 =>
            bound.Offset.Value + " FOLLOWING",
        WindowFrameBoundKindCore.UnboundedFollowing =>
            "UNBOUNDED FOLLOWING",
        _ => throw new SqlCompilationException(
            "Invalid window frame bound '" + bound.Kind + "'.")
    };
}
