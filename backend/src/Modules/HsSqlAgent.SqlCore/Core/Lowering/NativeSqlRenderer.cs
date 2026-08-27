using System.Collections.Immutable;
using System.Text;
using HsSqlAgent.SqlCore.Core.Execution;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Native Core AST renderer. This is the final SQL backend for query and DML compilation and does
/// not translate through a query-builder IR. Capability/semantic decisions remain owned by the
/// earlier compiler stages; this renderer deterministically prints only validated canonical nodes.
/// </summary>
public sealed class NativeSqlRenderer(SqlAgentToolType provider) : IProviderLowerer
{
    public SqlAgentToolType Provider { get; } = provider;

    public CompiledSqlCommand Lower(ExecutableSqlPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.TargetProvider != Provider)
        {
            throw new SqlCompilationException(
                "Plan targets " + plan.TargetProvider +
                ", but this native renderer targets " + Provider + ".");
        }

        var fragment = plan.Statement switch
        {
            SelectStatement or QueryStatement =>
                RenderStatement(plan.Statement, QueryPosition.Root),
            InsertStatement insert => RenderInsert(insert),
            UpdateStatement update => RenderUpdate(update),
            DeleteStatement delete => RenderDelete(delete),
            _ => throw new SqlCompilationException(
                "Unsupported statement during native lowering: " +
                plan.Statement.GetType().Name)
        };

        var finalized = NativeSqlParameterizer.Finalize(fragment, Provider);
        var kind = plan.Statement switch
        {
            SelectStatement or QueryStatement => SqlStatementKind.Select,
            InsertStatement => SqlStatementKind.Insert,
            UpdateStatement => SqlStatementKind.Update,
            DeleteStatement => SqlStatementKind.Delete,
            _ => throw new SqlCompilationException(
                "Unsupported statement kind during native lowering.")
        };

        var command = new CompiledSqlCommand(
            finalized.Sql,
            finalized.Parameters,
            kind,
            string.Empty,
            Provider);

        return command with
        {
            PlanFingerprint = DmlFingerprintService.ComputePlanFingerprint(
                command,
                plan.PolicyVersion)
        };
    }

    private NativeSqlFragment RenderStatement(
        SqlStatement statement,
        QueryPosition position) => statement switch
    {
        SelectStatement select => RenderSelect(select, position, includeTail: true),
        QueryStatement query => RenderQuery(query, position),
        _ => throw new SqlCompilationException(
            "Native query renderer requires SELECT/query-set AST, not " +
            statement.GetType().Name + ".")
    };

    private NativeSqlFragment RenderSelect(
        SelectStatement statement,
        QueryPosition position,
        bool includeTail)
    {
        var ctes = RenderCtes(statement.Ctes);
        var bodyStatement = statement with
        {
            Ctes = ImmutableArray<CteDefinition>.Empty
        };

        NativeSqlFragment body;
        if (includeTail
            && Provider == SqlAgentToolType.MsSqlServer
            && bodyStatement.Offset is > 0
            && bodyStatement.Limit is not 0)
        {
            body = RenderSqlServerOffsetSelect(bodyStatement);
        }
        else
        {
            body = RenderSelectBody(
                bodyStatement,
                position,
                includeTail,
                extraProjection: null);
        }

        if (ctes.Sql.Length == 0)
            return body;

        return new NativeSqlFragment(
            ctes.Sql + " " + body.Sql,
            ctes.Bindings.Concat(body.Bindings).ToImmutableArray());
    }

    private NativeSqlFragment RenderCtes(
        ImmutableArray<CteDefinition> ctes)
    {
        if (ctes.IsDefaultOrEmpty)
            return NativeSqlFragment.Empty;

        var parts = new List<string>(ctes.Length);
        var bindings = ImmutableArray.CreateBuilder<object?>();

        foreach (var cte in ctes)
        {
            if (!cte.ColumnAliases.IsDefaultOrEmpty)
            {
                throw new SqlCompilationException(
                    "CTE column aliases must be canonicalized to projection aliases before native lowering.");
            }

            if (cte.Name.Parts.Length != 1)
                throw new SqlCompilationException("CTE name must be unqualified.");

            var query = RenderStatement(cte.Query, QueryPosition.CteDefinition);
            var name = CoreIdentifierSqlRenderer.Render(
                cte.Name,
                Provider,
                allowWildcard: false);
            parts.Add(name + " AS (" + query.Sql + ")");
            bindings.AddRange(query.Bindings);
        }

        return new NativeSqlFragment(
            "WITH " + string.Join(", ", parts),
            bindings.ToImmutable());
    }

    private NativeSqlFragment RenderSelectBody(
        SelectStatement statement,
        QueryPosition position,
        bool includeTail,
        NativeSqlFragment? extraProjection)
    {
        var sql = new StringBuilder();
        var bindings = ImmutableArray.CreateBuilder<object?>();

        var head = RenderSelectHead(
            includeTail ? statement.Limit : null,
            includeTail ? statement.Offset : null,
            statement.Distinct);
        sql.Append(head.Sql);
        bindings.AddRange(head.Bindings);

        var projections = new List<NativeSqlFragment>();
        if (statement.Select.IsDefaultOrEmpty)
        {
            projections.Add(new NativeSqlFragment(
                "*",
                ImmutableArray<object?>.Empty));
        }
        else
        {
            foreach (var item in statement.Select)
                projections.Add(RenderSelectItem(item));
        }

        if (extraProjection is not null)
            projections.Add(extraProjection);

        for (var i = 0; i < projections.Count; i++)
        {
            if (i > 0)
                sql.Append(", ");
            sql.Append(projections[i].Sql);
            bindings.AddRange(projections[i].Bindings);
        }

        if (statement.From is not null)
        {
            var from = RenderTableSource(statement.From, QueryPosition.DerivedTable);
            sql.Append(" FROM ").Append(from.Sql);
            bindings.AddRange(from.Bindings);
        }
        else if (Provider == SqlAgentToolType.Oracle)
        {
            sql.Append(" FROM DUAL");
        }
        else if (Provider == SqlAgentToolType.Firebird)
        {
            sql.Append(" FROM RDB$DATABASE");
        }

        foreach (var join in statement.Joins)
        {
            var renderedJoin = RenderJoin(join);
            sql.Append(' ').Append(renderedJoin.Sql);
            bindings.AddRange(renderedJoin.Bindings);
        }

        if (statement.Where is not null)
        {
            var where = RenderExpression(statement.Where);
            sql.Append(" WHERE ").Append(where.Sql);
            bindings.AddRange(where.Bindings);
        }

        if (!statement.GroupBy.IsDefaultOrEmpty)
        {
            var groupItems = statement.GroupBy
                .Select(item => RenderExpression(item))
                .ToArray();
            sql.Append(" GROUP BY ")
                .Append(string.Join(", ", groupItems.Select(item => item.Sql)));
            foreach (var item in groupItems)
                bindings.AddRange(item.Bindings);
        }

        if (statement.Having is not null)
        {
            var having = RenderExpression(statement.Having);
            sql.Append(" HAVING ").Append(having.Sql);
            bindings.AddRange(having.Bindings);
        }

        if (includeTail)
        {
            var order = RenderOrderBy(statement.OrderBy, statement.Select);
            if (order.Sql.Length > 0)
            {
                sql.Append(' ').Append(order.Sql);
                bindings.AddRange(order.Bindings);
            }

            var pagination = RenderPagination(
                statement.Limit,
                statement.Offset,
                hasOrderBy: order.Sql.Length > 0);
            if (pagination.Sql.Length > 0)
            {
                sql.Append(' ').Append(pagination.Sql);
                bindings.AddRange(pagination.Bindings);
            }
        }

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }

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

    private NativeSqlFragment RenderQuery(
        QueryStatement statement,
        QueryPosition position)
    {
        if (!RequiresTail(statement))
            return RenderSetBody(statement, position);

        if (position == QueryPosition.ScalarSubquery
            && CoreNativeSetTailScope.CanRenderDirectTail(statement))
        {
            var body = RenderSetBody(
                statement with
                {
                    OrderBy = ImmutableArray<OrderByItem>.Empty,
                    Limit = null,
                    Offset = null
                },
                position);
            var tail = RenderDirectSetTail(
                statement.OrderBy,
                statement.Limit,
                statement.Offset,
                statement.Head.Select);
            return JoinFragments(body, tail, " ");
        }

        var inner = RenderSetBody(
            statement with
            {
                OrderBy = ImmutableArray<OrderByItem>.Empty,
                Limit = null,
                Offset = null
            },
            position);
        return RenderSetTailWrapper(
            inner,
            statement.OrderBy,
            statement.Limit,
            statement.Offset,
            statement.Head.Select);
    }

    private NativeSqlFragment RenderSetBody(
        QueryStatement statement,
        QueryPosition position)
    {
        var head = RenderSelect(
            statement.Head,
            position,
            includeTail: false);
        var sql = new StringBuilder(head.Sql);
        var bindings = head.Bindings.ToBuilder();

        foreach (var operation in statement.SetOperations)
        {
            sql.Append(' ').Append(operation.Kind switch
            {
                SetOperationKind.Union => "UNION",
                SetOperationKind.UnionAll => "UNION ALL",
                SetOperationKind.Intersect => "INTERSECT",
                SetOperationKind.Except => "EXCEPT",
                _ => throw new SqlCompilationException(
                    "Unsupported set operation '" + operation.Kind + "'.")
            }).Append(' ');

            var branch = RenderSetBranch(operation.Query);
            sql.Append(branch.Sql);
            bindings.AddRange(branch.Bindings);
        }

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }

    private NativeSqlFragment RenderSetBranch(SqlStatement statement)
    {
        var branch = RenderStatement(statement, QueryPosition.SetBranch);
        if (RootCtes(statement).IsDefaultOrEmpty)
            return branch;

        var alias = CoreIdentifierSqlRenderer.RenderAlias(
            new IdentifierPart(
                "_set_branch",
                WasQuoted: false,
                SourceSpan.Unknown),
            Provider);
        return new NativeSqlFragment(
            "SELECT * FROM (" + branch.Sql + ") AS " + alias,
            branch.Bindings);
    }

    private NativeSqlFragment RenderSetTailWrapper(
        NativeSqlFragment inner,
        ImmutableArray<OrderByItem> orderBy,
        int? limit,
        int? offset,
        ImmutableArray<SelectItem> projection)
    {
        if (Provider == SqlAgentToolType.MsSqlServer
            && offset is > 0
            && limit is not 0)
        {
            return RenderSqlServerSetOffsetWrapper(
                inner,
                orderBy,
                limit,
                offset.Value,
                projection);
        }

        var head = RenderSelectHead(limit, offset, distinct: false);
        var alias = CoreIdentifierSqlRenderer.RenderAlias(
            new IdentifierPart("_set", false, SourceSpan.Unknown),
            Provider);
        var asKeyword = Provider == SqlAgentToolType.Oracle
            ? " "
            : " AS ";

        var sql = new StringBuilder(head.Sql)
            .Append("* FROM (")
            .Append(inner.Sql)
            .Append(')')
            .Append(asKeyword)
            .Append(alias);
        var bindings = head.Bindings
            .Concat(inner.Bindings)
            .ToImmutableArray()
            .ToBuilder();

        var order = RenderOrderBy(orderBy, projection);
        if (order.Sql.Length > 0)
        {
            sql.Append(' ').Append(order.Sql);
            bindings.AddRange(order.Bindings);
        }

        var pagination = RenderPagination(
            limit,
            offset,
            hasOrderBy: order.Sql.Length > 0);
        if (pagination.Sql.Length > 0)
        {
            sql.Append(' ').Append(pagination.Sql);
            bindings.AddRange(pagination.Bindings);
        }

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
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

    private NativeSqlFragment RenderDirectSetTail(
        ImmutableArray<OrderByItem> orderBy,
        int? limit,
        int? offset,
        ImmutableArray<SelectItem> projection)
    {
        var sql = new StringBuilder();
        var bindings = ImmutableArray.CreateBuilder<object?>();

        var order = RenderOrderBy(orderBy, projection);
        if (order.Sql.Length > 0)
        {
            sql.Append(order.Sql);
            bindings.AddRange(order.Bindings);
        }

        var pagination = RenderPagination(
            limit,
            offset,
            hasOrderBy: order.Sql.Length > 0);
        if (pagination.Sql.Length > 0)
        {
            if (sql.Length > 0)
                sql.Append(' ');
            sql.Append(pagination.Sql);
            bindings.AddRange(pagination.Bindings);
        }

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }

    private NativeSqlFragment RenderSelectItem(SelectItem item)
    {
        var expression = RenderExpression(item.Expression);
        if (item.Alias is null)
            return expression;

        return expression with
        {
            Sql = expression.Sql + " AS " +
                  CoreIdentifierSqlRenderer.RenderAlias(item.Alias, Provider)
        };
    }

    private NativeSqlFragment RenderTableSource(
        TableSource source,
        QueryPosition position)
    {
        return source switch
        {
            NamedTableSource named => RenderNamedTableSource(named),
            DerivedTableSource derived => RenderDerivedTableSource(derived),
            _ => throw new SqlCompilationException(
                "Unsupported FROM source during native lowering: " +
                source.GetType().Name)
        };
    }

    private NativeSqlFragment RenderNamedTableSource(
        NamedTableSource source)
    {
        var table = CoreIdentifierSqlRenderer.Render(
            source.Name,
            Provider,
            allowWildcard: false);
        if (source.Alias is null)
        {
            return new NativeSqlFragment(
                table,
                ImmutableArray<object?>.Empty);
        }

        var alias = CoreIdentifierSqlRenderer.RenderAlias(
            source.Alias,
            Provider);
        var separator = Provider == SqlAgentToolType.Oracle
            ? " "
            : " AS ";

        return new NativeSqlFragment(
            table + separator + alias,
            ImmutableArray<object?>.Empty);
    }

    private NativeSqlFragment RenderDerivedTableSource(
        DerivedTableSource source)
    {
        var query = RenderStatement(source.Query, QueryPosition.DerivedTable);
        var alias = CoreIdentifierSqlRenderer.RenderAlias(
            source.Alias,
            Provider);
        var separator = Provider == SqlAgentToolType.Oracle
            ? " "
            : " AS ";

        return query with
        {
            Sql = "(" + query.Sql + ")" + separator + alias
        };
    }

    private NativeSqlFragment RenderJoin(JoinSource join)
    {
        var keyword = join.Kind switch
        {
            "INNER" => "INNER JOIN",
            "LEFT" => "LEFT JOIN",
            "RIGHT" => "RIGHT JOIN",
            "FULL" => "FULL OUTER JOIN",
            "CROSS" => "CROSS JOIN",
            _ => throw new SqlCompilationException(
                "Unsupported JOIN kind '" + join.Kind + "'.")
        };

        if (join.Kind == "CROSS" && join.Predicate is not null)
            throw new SqlCompilationException("CROSS JOIN cannot have an ON predicate.");
        if (join.Kind != "CROSS" && join.Predicate is null)
            throw new SqlCompilationException(join.Kind + " JOIN requires an ON predicate.");

        var source = RenderTableSource(join.Source, QueryPosition.DerivedTable);
        if (join.Predicate is null)
        {
            return source with { Sql = keyword + " " + source.Sql };
        }

        var predicate = RenderExpression(join.Predicate);
        return new NativeSqlFragment(
            keyword + " " + source.Sql + " ON " + predicate.Sql,
            source.Bindings.Concat(predicate.Bindings).ToImmutableArray());
    }

    private NativeSqlFragment RenderOrderBy(
        ImmutableArray<OrderByItem> orderBy,
        IEnumerable<SelectItem>? projection)
    {
        if (orderBy.IsDefaultOrEmpty)
            return NativeSqlFragment.Empty;

        var preservedAliases = projection?
            .Select(item => item.Alias)
            .Where(alias => alias is { PreserveSpelling: true })
            .Cast<IdentifierPart>()
            .ToArray() ?? [];

        var parts = new List<string>(orderBy.Length);
        var bindings = ImmutableArray.CreateBuilder<object?>();

        foreach (var item in orderBy)
        {
            NativeSqlFragment rendered;
            if (item.Expression is LiteralExpr
                {
                    Value: OrderByOrdinalValue ordinal
                })
            {
                rendered = new NativeSqlFragment(
                    ordinal.Position.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ImmutableArray<object?>.Empty);
            }
            else
            {
                rendered = TryRenderPreservedProjectionAlias(
                    item.Expression,
                    preservedAliases)
                    ?? RenderExpression(item.Expression);
            }

            var sql = rendered.Sql +
                (item.Descending ? " DESC" : " ASC");
            sql += item.NullOrdering switch
            {
                NullOrderingKind.Default => string.Empty,
                NullOrderingKind.First => " NULLS FIRST",
                NullOrderingKind.Last => " NULLS LAST",
                _ => throw new SqlCompilationException(
                    "Unsupported NULL ordering '" +
                    item.NullOrdering + "'.")
            };

            parts.Add(sql);
            bindings.AddRange(rendered.Bindings);
        }

        return new NativeSqlFragment(
            "ORDER BY " + string.Join(", ", parts),
            bindings.ToImmutable());
    }

    private NativeSqlFragment? TryRenderPreservedProjectionAlias(
        SqlExpr expression,
        IReadOnlyCollection<IdentifierPart> aliases)
    {
        var identifier = expression switch
        {
            BoundColumnExpr bound => bound.Name,
            ColumnExpr column => column.Name,
            _ => null
        };
        if (identifier is not { Parts.Length: 1 })
            return null;

        var reference = identifier.Parts[0];
        if (reference.WasQuoted)
            return null;

        var matches = aliases
            .Where(alias => string.Equals(
                alias.Value,
                reference.Value,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
        {
            throw new SqlCompilationException(
                "ORDER BY alias '" + reference.Value +
                "' is ambiguous among preserved projection aliases.");
        }

        if (matches.Length == 0)
            return null;

        return new NativeSqlFragment(
            CoreIdentifierSqlRenderer.RenderAlias(matches[0], Provider),
            ImmutableArray<object?>.Empty);
    }

    private NativeSqlFragment RenderSelectHead(
        int? limit,
        int? offset,
        bool distinct)
    {
        var bindings = ImmutableArray.CreateBuilder<object?>();
        var sql = new StringBuilder("SELECT ");

        if (Provider == SqlAgentToolType.MsSqlServer
            && limit is >= 0
            && offset is not > 0)
        {
            if (distinct)
                sql.Append("DISTINCT ");
            sql.Append("TOP (")
                .Append(NativeSqlParameterizer.Placeholder)
                .Append(") ");
            bindings.Add(limit.Value);
            return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
        }

        if (Provider == SqlAgentToolType.MsSqlServer
            && limit == 0)
        {
            if (distinct)
                sql.Append("DISTINCT ");
            sql.Append("TOP (")
                .Append(NativeSqlParameterizer.Placeholder)
                .Append(") ");
            bindings.Add(0);
            return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
        }

        if (Provider == SqlAgentToolType.Firebird)
        {
            if (limit is >= 0 && (offset is not > 0 || limit == 0))
            {
                sql.Append("FIRST ")
                    .Append(NativeSqlParameterizer.Placeholder)
                    .Append(' ');
                bindings.Add(limit.Value);
            }

            if (offset is > 0 && (limit is null || limit == 0))
            {
                sql.Append("SKIP ")
                    .Append(NativeSqlParameterizer.Placeholder)
                    .Append(' ');
                bindings.Add(offset.Value);
            }

            if (distinct)
                sql.Append("DISTINCT ");

            return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
        }

        if (distinct)
            sql.Append("DISTINCT ");

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }

    private NativeSqlFragment RenderPagination(
        int? limit,
        int? offset,
        bool hasOrderBy)
    {
        if (limit is null && offset is not > 0)
            return NativeSqlFragment.Empty;

        switch (Provider)
        {
            case SqlAgentToolType.Postgres:
            {
                var sql = new StringBuilder();
                var bindings = ImmutableArray.CreateBuilder<object?>();
                if (limit is not null)
                {
                    sql.Append("LIMIT ")
                        .Append(NativeSqlParameterizer.Placeholder);
                    bindings.Add(limit.Value);
                }

                if (offset is > 0)
                {
                    if (sql.Length > 0)
                        sql.Append(' ');
                    sql.Append("OFFSET ")
                        .Append(NativeSqlParameterizer.Placeholder);
                    bindings.Add(offset.Value);
                }

                return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
            }

            case SqlAgentToolType.MySQL:
            {
                if (limit is null)
                {
                    return new NativeSqlFragment(
                        "LIMIT 18446744073709551615 OFFSET " +
                        NativeSqlParameterizer.Placeholder,
                        [offset!.Value]);
                }

                if (offset is > 0)
                {
                    return new NativeSqlFragment(
                        "LIMIT " + NativeSqlParameterizer.Placeholder +
                        " OFFSET " + NativeSqlParameterizer.Placeholder,
                        [limit.Value, offset.Value]);
                }

                return new NativeSqlFragment(
                    "LIMIT " + NativeSqlParameterizer.Placeholder,
                    [limit.Value]);
            }

            case SqlAgentToolType.Sqlite:
            {
                if (limit is null)
                {
                    return new NativeSqlFragment(
                        "LIMIT -1 OFFSET " +
                        NativeSqlParameterizer.Placeholder,
                        [offset!.Value]);
                }

                if (offset is > 0)
                {
                    return new NativeSqlFragment(
                        "LIMIT " + NativeSqlParameterizer.Placeholder +
                        " OFFSET " + NativeSqlParameterizer.Placeholder,
                        [limit.Value, offset.Value]);
                }

                return new NativeSqlFragment(
                    "LIMIT " + NativeSqlParameterizer.Placeholder,
                    [limit.Value]);
            }

            case SqlAgentToolType.Oracle:
            {
                var safeOrder = hasOrderBy
                    ? string.Empty
                    : "ORDER BY (SELECT 0 FROM DUAL) ";
                if (limit is null)
                {
                    return new NativeSqlFragment(
                        safeOrder + "OFFSET " +
                        NativeSqlParameterizer.Placeholder + " ROWS",
                        [offset!.Value]);
                }

                return new NativeSqlFragment(
                    safeOrder + "OFFSET " +
                    NativeSqlParameterizer.Placeholder +
                    " ROWS FETCH NEXT " +
                    NativeSqlParameterizer.Placeholder +
                    " ROWS ONLY",
                    [offset ?? 0L, limit.Value]);
            }

            case SqlAgentToolType.Firebird:
            {
                if (limit is > 0 && offset is > 0)
                {
                    return new NativeSqlFragment(
                        "ROWS " + NativeSqlParameterizer.Placeholder +
                        " TO " + NativeSqlParameterizer.Placeholder,
                        [(long)offset.Value + 1L, (long)offset.Value + limit.Value]);
                }

                return NativeSqlFragment.Empty;
            }

            case SqlAgentToolType.MsSqlServer:
                // TOP handles no-offset limits, while offset queries are rendered by the
                // legacy-compatible ROW_NUMBER path before this method is reached.
                return NativeSqlFragment.Empty;

            default:
                throw new SqlCompilationException(
                    "Unsupported target provider '" + Provider + "'.");
        }
    }

    private NativeSqlFragment RenderExpression(
        SqlExpr expression,
        bool dmlContext = false) =>
        NativeSqlExpressionRenderer.Render(
            expression,
            Provider,
            statement => RenderStatement(
                statement,
                QueryPosition.ScalarSubquery),
            dmlContext);

    private NativeSqlFragment RenderInsert(InsertStatement insert)
    {
        if (insert.Columns.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT requires at least one target column.");

        return insert.Source switch
        {
            InsertValuesSource values => RenderInsertValues(insert, values),
            InsertQuerySource query => RenderInsertQuery(insert, query),
            _ => throw new SqlCompilationException(
                "Unsupported INSERT source during native lowering: " +
                insert.Source.GetType().Name)
        };
    }

    private NativeSqlFragment RenderInsertValues(
        InsertStatement insert,
        InsertValuesSource values)
    {
        if (values.Rows.IsDefaultOrEmpty)
            throw new SqlCompilationException("INSERT VALUES requires at least one row.");

        var table = CoreIdentifierSqlRenderer.Render(
            insert.Target.Name,
            Provider,
            allowWildcard: false);
        var columns = insert.Columns
            .Select(column => CoreIdentifierSqlRenderer.Render(
                column,
                Provider,
                allowWildcard: false))
            .ToArray();
        var columnSql = string.Join(", ", columns);

        var rows = new List<NativeSqlFragment>(values.Rows.Length);
        for (var rowIndex = 0; rowIndex < values.Rows.Length; rowIndex++)
        {
            var row = values.Rows[rowIndex];
            if (row.Length != columns.Length)
            {
                throw new SqlCompilationException(
                    "INSERT row " + (rowIndex + 1) + " has " +
                    row.Length + " values but " + columns.Length +
                    " columns were declared.");
            }

            var valuesSql = new List<string>(row.Length);
            var bindings = ImmutableArray.CreateBuilder<object?>();
            foreach (var expression in row)
            {
                var rendered = RenderExpression(expression, dmlContext: true);
                valuesSql.Add(rendered.Sql);
                bindings.AddRange(rendered.Bindings);
            }

            rows.Add(new NativeSqlFragment(
                string.Join(", ", valuesSql),
                bindings.ToImmutable()));
        }

        var allBindings = rows
            .SelectMany(row => row.Bindings)
            .ToImmutableArray();

        if (Provider == SqlAgentToolType.Oracle && rows.Count > 1)
        {
            var sql = new StringBuilder("INSERT ALL");
            foreach (var row in rows)
            {
                sql.Append(" INTO ")
                    .Append(table)
                    .Append(" (")
                    .Append(columnSql)
                    .Append(") VALUES (")
                    .Append(row.Sql)
                    .Append(')');
            }

            sql.Append(" SELECT 1 FROM DUAL");
            return new NativeSqlFragment(sql.ToString(), allBindings);
        }

        if (Provider == SqlAgentToolType.Firebird && rows.Count > 1)
        {
            var sql = "INSERT INTO " + table + " (" + columnSql + ") " +
                string.Join(
                    " UNION ALL ",
                    rows.Select(row =>
                        "SELECT " + row.Sql + " FROM RDB$DATABASE"));
            return new NativeSqlFragment(sql, allBindings);
        }

        return new NativeSqlFragment(
            "INSERT INTO " + table + " (" + columnSql + ") VALUES " +
            string.Join(", ", rows.Select(row => "(" + row.Sql + ")")),
            allBindings);
    }

    private NativeSqlFragment RenderInsertQuery(
        InsertStatement insert,
        InsertQuerySource source)
    {
        var table = CoreIdentifierSqlRenderer.Render(
            insert.Target.Name,
            Provider,
            allowWildcard: false);
        var columns = string.Join(
            ", ",
            insert.Columns.Select(column => CoreIdentifierSqlRenderer.Render(
                column,
                Provider,
                allowWildcard: false)));
        var insertPrefix = "INSERT INTO " + table + " (" + columns + ")";
        var ctes = RootCtes(source.Query);

        if (ctes.IsDefaultOrEmpty)
        {
            var query = RenderStatement(
                source.Query,
                QueryPosition.InsertSelectSource);
            return query with { Sql = insertPrefix + " " + query.Sql };
        }

        var withClause = RenderCtes(ctes);
        var sourceWithoutRootCtes = RemoveRootCtes(source.Query);
        var querySource = RenderStatement(
            sourceWithoutRootCtes,
            QueryPosition.InsertSelectSource);

        var sql = Provider switch
        {
            SqlAgentToolType.Postgres or
            SqlAgentToolType.MsSqlServer or
            SqlAgentToolType.Sqlite =>
                withClause.Sql + " " + insertPrefix + " " + querySource.Sql,
            SqlAgentToolType.MySQL or
            SqlAgentToolType.Oracle or
            SqlAgentToolType.Firebird =>
                insertPrefix + " " + withClause.Sql + " " + querySource.Sql,
            _ => throw new SqlCompilationException(
                "INSERT ... SELECT CTE placement is not declared for provider " +
                Provider + ".")
        };

        return new NativeSqlFragment(
            sql,
            withClause.Bindings
                .Concat(querySource.Bindings)
                .ToImmutableArray());
    }

    private NativeSqlFragment RenderUpdate(UpdateStatement update)
    {
        if (update.Assignments.IsDefaultOrEmpty)
            throw new SqlCompilationException("UPDATE requires at least one assignment.");

        var table = CoreIdentifierSqlRenderer.Render(
            update.Target.Name,
            Provider,
            allowWildcard: false);
        var assignments = new List<string>(update.Assignments.Length);
        var bindings = ImmutableArray.CreateBuilder<object?>();

        foreach (var assignment in update.Assignments)
        {
            var column = CoreIdentifierSqlRenderer.Render(
                assignment.Column,
                Provider,
                allowWildcard: false);
            var value = RenderExpression(
                assignment.Value,
                dmlContext: true);
            assignments.Add(column + " = " + value.Sql);
            bindings.AddRange(value.Bindings);
        }

        var sql = new StringBuilder("UPDATE ")
            .Append(table)
            .Append(" SET ")
            .Append(string.Join(", ", assignments));

        if (update.Predicate is not null)
        {
            var predicate = RenderExpression(
                update.Predicate,
                dmlContext: true);
            sql.Append(" WHERE ").Append(predicate.Sql);
            bindings.AddRange(predicate.Bindings);
        }

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }

    private NativeSqlFragment RenderDelete(DeleteStatement delete)
    {
        var table = CoreIdentifierSqlRenderer.Render(
            delete.Target.Name,
            Provider,
            allowWildcard: false);
        var sql = new StringBuilder("DELETE FROM ").Append(table);
        var bindings = ImmutableArray.CreateBuilder<object?>();

        if (delete.Predicate is not null)
        {
            var predicate = RenderExpression(
                delete.Predicate,
                dmlContext: true);
            sql.Append(" WHERE ").Append(predicate.Sql);
            bindings.AddRange(predicate.Bindings);
        }

        return new NativeSqlFragment(sql.ToString(), bindings.ToImmutable());
    }

    private static bool RequiresTail(QueryStatement query) =>
        !query.OrderBy.IsDefaultOrEmpty
        || query.Limit is not null
        || query.Offset is > 0;

    private static ImmutableArray<CteDefinition> RootCtes(
        SqlStatement statement) => statement switch
    {
        SelectStatement select => select.Ctes,
        QueryStatement query => query.Head.Ctes,
        _ => ImmutableArray<CteDefinition>.Empty
    };

    private static SqlStatement RemoveRootCtes(
        SqlStatement statement) => statement switch
    {
        SelectStatement select => select with
        {
            Ctes = ImmutableArray<CteDefinition>.Empty
        },
        QueryStatement query => query with
        {
            Head = query.Head with
            {
                Ctes = ImmutableArray<CteDefinition>.Empty
            }
        },
        _ => throw new SqlCompilationException(
            "INSERT ... SELECT source '" +
            statement.GetType().Name +
            "' is not a query statement.")
    };

    private static NativeSqlFragment JoinFragments(
        NativeSqlFragment left,
        NativeSqlFragment right,
        string separator)
    {
        if (right.Sql.Length == 0)
            return left;
        if (left.Sql.Length == 0)
            return right;

        return new NativeSqlFragment(
            left.Sql + separator + right.Sql,
            left.Bindings.Concat(right.Bindings).ToImmutableArray());
    }

    private enum QueryPosition
    {
        Root,
        InsertSelectSource,
        CteDefinition,
        DerivedTable,
        SetBranch,
        ScalarSubquery
    }
}
