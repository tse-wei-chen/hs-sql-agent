using System.Collections.Immutable;
using System.Text;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// SQL Server paging compatibility rendering. Legacy OFFSET paths that require ROW_NUMBER wrappers
/// stay isolated from provider-neutral query/set rendering while preserving validated output names,
/// ordering semantics, and hidden synthetic page keys.
/// </summary>
public sealed partial class NativeSqlRenderer
{
    private NativeSqlFragment RenderSqlServerOffsetSelect(
        SelectStatement statement)
    {
        var plan = BuildSqlServerSelectPagePlan(
            statement.Select,
            statement.OrderBy,
            statement.Distinct);

        var pageSource = RenderSelectBody(
            statement with
            {
                Select = plan.BaseProjection,
                OrderBy = ImmutableArray<OrderByItem>.Empty,
                Limit = null,
                Offset = null
            },
            QueryPosition.DerivedTable,
            includeTail: false,
            extraProjection: null);

        return RenderSqlServerPageWrapper(
            pageSource,
            plan.OutputInternalAliases,
            plan.ExternalAliases,
            plan.WindowOrderBy,
            statement.Limit,
            statement.Offset!.Value);
    }

    private NativeSqlFragment RenderSqlServerSetOffsetWrapper(
        NativeSqlFragment inner,
        ImmutableArray<OrderByItem> orderBy,
        int? limit,
        int offset,
        ImmutableArray<SelectItem> projection)
    {
        var externalAliases = ProjectionOutputNames(
            projection,
            "SQL Server set-operation OFFSET pagination");
        EnsureUniqueSqlServerOutputNames(
            externalAliases,
            "SQL Server set-operation OFFSET pagination");

        var internalAliases = externalAliases
            .Select((_, index) => InternalPageAlias(index))
            .ToImmutableArray();
        var setAlias = CoreIdentifierSqlRenderer.RenderAlias(
            new IdentifierPart("_set", false, SourceSpan.Unknown),
            Provider);

        var selectParts = new string[externalAliases.Length];
        for (var i = 0; i < externalAliases.Length; i++)
        {
            selectParts[i] =
                setAlias + "." +
                CoreIdentifierSqlRenderer.RenderAlias(externalAliases[i], Provider) +
                " AS " +
                CoreIdentifierSqlRenderer.RenderAlias(internalAliases[i], Provider);
        }

        var pageSource = new NativeSqlFragment(
            "SELECT " + string.Join(", ", selectParts) +
            " FROM (" + inner.Sql + ") AS " + setAlias,
            inner.Bindings);
        var windowOrder = RewriteSetPageOrderBy(
            orderBy,
            externalAliases,
            internalAliases);

        return RenderSqlServerPageWrapper(
            pageSource,
            internalAliases,
            externalAliases,
            windowOrder,
            limit,
            offset);
    }

    private NativeSqlFragment RenderSqlServerPageWrapper(
        NativeSqlFragment pageSource,
        ImmutableArray<IdentifierPart> outputInternalAliases,
        ImmutableArray<IdentifierPart> externalAliases,
        ImmutableArray<OrderByItem> windowOrderBy,
        int? limit,
        int offset)
    {
        var baseAlias = CoreIdentifierSqlRenderer.RenderAlias(
            new IdentifierPart("_core_page_base", false, SourceSpan.Unknown),
            Provider);
        var wrapperAlias = CoreIdentifierSqlRenderer.RenderAlias(
            new IdentifierPart("results_wrapper", false, SourceSpan.Unknown),
            Provider);
        var rowAlias = CoreIdentifierSqlRenderer.RenderAlias(
            new IdentifierPart("_core_page_row", false, SourceSpan.Unknown),
            Provider);

        var order = windowOrderBy.IsDefaultOrEmpty
            ? new NativeSqlFragment(
                "ORDER BY (SELECT 0)",
                ImmutableArray<object?>.Empty)
            : RenderOrderBy(windowOrderBy, projection: null);

        var middleOutputs = outputInternalAliases
            .Select(alias =>
                baseAlias + "." +
                CoreIdentifierSqlRenderer.RenderAlias(alias, Provider))
            .ToArray();
        var middleSql =
            "SELECT " + string.Join(", ", middleOutputs) +
            ", ROW_NUMBER() OVER (" + order.Sql + ") AS " + rowAlias +
            " FROM (" + pageSource.Sql + ") AS " + baseAlias;

        var outerOutputs = new string[outputInternalAliases.Length];
        for (var i = 0; i < outputInternalAliases.Length; i++)
        {
            outerOutputs[i] =
                wrapperAlias + "." +
                CoreIdentifierSqlRenderer.RenderAlias(outputInternalAliases[i], Provider) +
                " AS " +
                CoreIdentifierSqlRenderer.RenderAlias(externalAliases[i], Provider);
        }

        var sql = new StringBuilder()
            .Append("SELECT ")
            .Append(string.Join(", ", outerOutputs))
            .Append(" FROM (")
            .Append(middleSql)
            .Append(") AS ")
            .Append(wrapperAlias)
            .Append(" WHERE ")
            .Append(wrapperAlias)
            .Append('.')
            .Append(rowAlias)
            .Append(' ');

        var bindings = pageSource.Bindings
            .Concat(order.Bindings)
            .ToImmutableArray()
            .ToBuilder();

        if (limit is null)
        {
            sql.Append(">= ").Append(NativeSqlParameterizer.Placeholder);
            bindings.Add((long)offset + 1L);
        }
        else
        {
            sql.Append("BETWEEN ")
                .Append(NativeSqlParameterizer.Placeholder)
                .Append(" AND ")
                .Append(NativeSqlParameterizer.Placeholder);
            bindings.Add((long)offset + 1L);
            bindings.Add((long)offset + limit.Value);
        }

        // Filtering on ROW_NUMBER selects the correct page, but SQL does not preserve the window
        // ordering through the outer query unless it is stated again. Keep the synthetic row key
        // hidden from the result projection while using it to restore the requested page order.
        sql.Append(" ORDER BY ")
            .Append(wrapperAlias)
            .Append('.')
            .Append(rowAlias)
            .Append(" ASC");

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }

    private SqlServerSelectPagePlan BuildSqlServerSelectPagePlan(
        ImmutableArray<SelectItem> projection,
        ImmutableArray<OrderByItem> orderBy,
        bool distinct)
    {
        var externalAliases = ProjectionOutputNames(
            projection,
            "SQL Server OFFSET pagination");
        var outputInternalAliases = externalAliases
            .Select((_, index) => InternalPageAlias(index))
            .ToImmutableArray();

        var baseProjection = ImmutableArray.CreateBuilder<SelectItem>(
            projection.Length + orderBy.Length);
        for (var i = 0; i < projection.Length; i++)
        {
            baseProjection.Add(projection[i] with
            {
                Alias = outputInternalAliases[i]
            });
        }

        var windowOrder = ImmutableArray.CreateBuilder<OrderByItem>(orderBy.Length);
        for (var i = 0; i < orderBy.Length; i++)
        {
            var item = orderBy[i];
            if (item.NullOrdering != NullOrderingKind.Default)
            {
                throw new SqlCompilationException(
                    "SQL Server OFFSET pagination requires NULL ordering to be canonicalized before native lowering.");
            }

            var projectionIndex = TryResolveProjectionOrderIndex(
                item.Expression,
                projection);
            IdentifierPart orderAlias;
            if (projectionIndex is >= 0)
            {
                orderAlias = outputInternalAliases[projectionIndex];
            }
            else
            {
                if (distinct)
                {
                    throw new SqlCompilationException(
                        "SQL Server DISTINCT OFFSET pagination requires every ORDER BY expression to resolve to a projected output.");
                }

                orderAlias = new IdentifierPart(
                    "_core_page_order_" + i,
                    WasQuoted: false,
                    SourceSpan.Unknown);
                baseProjection.Add(new SelectItem(
                    item.Expression,
                    orderAlias,
                    item.Span));
            }

            windowOrder.Add(item with
            {
                Expression = new ColumnExpr(
                    SqlIdentifier.Unquoted(orderAlias.Value, item.Span),
                    item.Span),
                NullOrdering = NullOrderingKind.Default
            });
        }

        return new SqlServerSelectPagePlan(
            baseProjection.ToImmutable(),
            outputInternalAliases,
            externalAliases,
            windowOrder.ToImmutable());
    }

    private int TryResolveProjectionOrderIndex(
        SqlExpr expression,
        ImmutableArray<SelectItem> projection)
    {
        if (expression is LiteralExpr { Value: OrderByOrdinalValue ordinal })
        {
            return ordinal.Position > 0 && ordinal.Position <= projection.Length
                ? ordinal.Position - 1
                : -1;
        }

        var identifier = expression switch
        {
            ColumnExpr column => column.Name,
            BoundColumnExpr column => column.Name,
            _ => null
        };
        if (identifier is { Parts.Length: 1 })
        {
            var reference = identifier.Parts[0].Value;
            var aliasMatches = projection
                .Select((item, index) => new { item.Alias, index })
                .Where(entry => entry.Alias is not null
                    && string.Equals(
                        entry.Alias.Value,
                        reference,
                        StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (aliasMatches.Length > 1)
            {
                throw new SqlCompilationException(
                    "SQL Server OFFSET pagination ORDER BY alias '" +
                    reference + "' is ambiguous.");
            }
            if (aliasMatches.Length == 1)
                return aliasMatches[0].index;
        }

        for (var i = 0; i < projection.Length; i++)
        {
            if (SqlServerProjectionExpressionMatches(
                    projection[i].Expression,
                    expression))
            {
                return i;
            }
        }

        return -1;
    }

    private bool SqlServerProjectionExpressionMatches(
        SqlExpr projected,
        SqlExpr ordered)
    {
        var projectedFragment = RenderExpression(projected);
        var orderedFragment = RenderExpression(ordered);

        if (!string.Equals(
                projectedFragment.Sql,
                orderedFragment.Sql,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (projectedFragment.Bindings.Length != orderedFragment.Bindings.Length)
            return false;

        for (var i = 0; i < projectedFragment.Bindings.Length; i++)
        {
            if (!Equals(
                    projectedFragment.Bindings[i],
                    orderedFragment.Bindings[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<IdentifierPart> ProjectionOutputNames(
        ImmutableArray<SelectItem> projection,
        string context)
    {
        if (projection.IsDefaultOrEmpty)
        {
            throw new SqlCompilationException(
                context + " cannot preserve an implicit wildcard projection through the legacy ROW_NUMBER wrapper.");
        }

        var result = ImmutableArray.CreateBuilder<IdentifierPart>(projection.Length);
        foreach (var item in projection)
        {
            if (item.Alias is not null)
            {
                result.Add(item.Alias);
                continue;
            }

            var identifier = item.Expression switch
            {
                ColumnExpr column => column.Name,
                BoundColumnExpr column => column.Name,
                _ => null
            };
            if (identifier is null
                || identifier.Parts.IsDefaultOrEmpty
                || (identifier.Parts[^1].Value == "*" && !identifier.Parts[^1].WasQuoted))
            {
                throw new SqlCompilationException(
                    context + " requires every projected output to have a stable name; " +
                    "use explicit aliases for wildcard or computed expressions.");
            }

            result.Add(identifier.Parts[^1]);
        }

        return result.ToImmutable();
    }

    private static void EnsureUniqueSqlServerOutputNames(
        ImmutableArray<IdentifierPart> names,
        string context)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (!seen.Add(name.Value))
            {
                throw new SqlCompilationException(
                    context + " requires unique set-result output names before the legacy ROW_NUMBER wrapper.");
            }
        }
    }

    private static ImmutableArray<OrderByItem> RewriteSetPageOrderBy(
        ImmutableArray<OrderByItem> orderBy,
        ImmutableArray<IdentifierPart> externalAliases,
        ImmutableArray<IdentifierPart> internalAliases)
    {
        if (orderBy.IsDefaultOrEmpty)
            return ImmutableArray<OrderByItem>.Empty;

        var result = ImmutableArray.CreateBuilder<OrderByItem>(orderBy.Length);
        foreach (var item in orderBy)
        {
            if (item.NullOrdering != NullOrderingKind.Default)
            {
                throw new SqlCompilationException(
                    "SQL Server set-operation OFFSET pagination requires NULL ordering to be canonicalized before native lowering.");
            }

            int index;
            if (item.Expression is LiteralExpr { Value: OrderByOrdinalValue ordinal })
            {
                index = ordinal.Position - 1;
            }
            else
            {
                var identifier = item.Expression switch
                {
                    ColumnExpr column => column.Name,
                    BoundColumnExpr column => column.Name,
                    _ => null
                };
                if (identifier is not { Parts.Length: 1 })
                {
                    throw new SqlCompilationException(
                        "SQL Server set-operation OFFSET pagination supports ORDER BY output names or ordinals only.");
                }

                var matches = externalAliases
                    .Select((alias, aliasIndex) => new { alias, aliasIndex })
                    .Where(entry => string.Equals(
                        entry.alias.Value,
                        identifier.Parts[0].Value,
                        StringComparison.OrdinalIgnoreCase))
                    .Take(2)
                    .ToArray();
                if (matches.Length != 1)
                {
                    throw new SqlCompilationException(
                        "SQL Server set-operation OFFSET pagination ORDER BY reference '" +
                        identifier.Parts[0].Value +
                        "' is not a unique combined output name.");
                }
                index = matches[0].aliasIndex;
            }

            if (index < 0 || index >= internalAliases.Length)
            {
                throw new SqlCompilationException(
                    "SQL Server set-operation OFFSET pagination ORDER BY position is outside the projected output width.");
            }

            result.Add(item with
            {
                Expression = new ColumnExpr(
                    SqlIdentifier.Unquoted(internalAliases[index].Value, item.Span),
                    item.Span),
                NullOrdering = NullOrderingKind.Default
            });
        }

        return result.ToImmutable();
    }

    private static IdentifierPart InternalPageAlias(int index) =>
        new(
            "_core_page_" + index,
            WasQuoted: false,
            SourceSpan.Unknown);

    private sealed record SqlServerSelectPagePlan(
        ImmutableArray<SelectItem> BaseProjection,
        ImmutableArray<IdentifierPart> OutputInternalAliases,
        ImmutableArray<IdentifierPart> ExternalAliases,
        ImmutableArray<OrderByItem> WindowOrderBy);
}
