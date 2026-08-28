using System.Collections.Immutable;
using System.Text;
using HsSqlAgent.SqlCore.Core.Execution;

namespace HsSqlAgent.SqlCore.Core.Lowering;

/// <summary>
/// Native Core AST renderer. This is the final SQL backend for query and DML compilation and does
/// not translate through a query-builder IR. Capability/semantic decisions remain owned by the
/// earlier compiler stages; this renderer deterministically prints only validated canonical nodes.
/// </summary>
public sealed partial class NativeSqlRenderer(
    SqlAgentToolType provider,
    SqlProviderCapabilityProfile? targetProfile = null) : IProviderLowerer
{
    public SqlAgentToolType Provider { get; } = provider;

    private SqlProviderCapabilityProfile? TargetProfile { get; } =
        ValidateTargetProfile(provider, targetProfile);

    private static SqlProviderCapabilityProfile? ValidateTargetProfile(
        SqlAgentToolType provider,
        SqlProviderCapabilityProfile? targetProfile)
    {
        return SqlProviderCapabilityProfileRules.ValidationIssue(
            targetProfile,
            provider) switch
        {
            SqlProviderCapabilityProfileValidationIssue.None => targetProfile,
            SqlProviderCapabilityProfileValidationIssue.ProviderMismatch =>
                throw new ArgumentException(
                    $"Target capability profile declares provider {targetProfile!.Provider}, but renderer provider is {provider}.",
                    nameof(targetProfile)),
            SqlProviderCapabilityProfileValidationIssue.NegativeCompatibilityLevel =>
                throw new ArgumentOutOfRangeException(
                    nameof(targetProfile),
                    targetProfile!.CompatibilityLevel,
                    "Provider compatibility level must be non-negative."),
            _ => throw new InvalidOperationException(
                "Unsupported target capability profile validation issue.")
        };
    }

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

        var groupItems = statement.GroupBy.IsDefaultOrEmpty
            ? Array.Empty<NativeSqlFragment>()
            : statement.GroupBy
                .Select(item => RenderExpression(item))
                .ToArray();
        if (Provider == SqlAgentToolType.Postgres
            && groupItems.Length > 0
            && !statement.Select.IsDefaultOrEmpty
            && !statement.Span.Equals(SourceSpan.Unknown))
        {
            SharePostgresGroupingBindings(
                statement,
                projections,
                groupItems);
        }

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
            var where = RenderPredicateExpression(statement.Where);
            sql.Append(" WHERE ").Append(where.Sql);
            bindings.AddRange(where.Bindings);
        }

        if (groupItems.Length > 0)
        {
            sql.Append(" GROUP BY ")
                .Append(string.Join(", ", groupItems.Select(item => item.Sql)));
            foreach (var item in groupItems)
                bindings.AddRange(item.Bindings);
        }

        if (statement.Having is not null)
        {
            var having = RenderPredicateExpression(statement.Having);
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

        var capabilityError = SqlJoinCapabilityRules.TargetValidationError(
            join.Kind,
            Provider,
            TargetProfile);
        if (capabilityError is not null)
            throw new SqlCompilationException(capabilityError);

        var source = RenderTableSource(join.Source, QueryPosition.DerivedTable);
        if (join.Predicate is null)
        {
            return source with { Sql = keyword + " " + source.Sql };
        }

        var predicate = RenderPredicateExpression(join.Predicate);
        return new NativeSqlFragment(
            keyword + " " + source.Sql + " ON " + predicate.Sql,
            source.Bindings.Concat(predicate.Bindings).ToImmutableArray());
    }

    private void SharePostgresGroupingBindings(
        SelectStatement statement,
        List<NativeSqlFragment> projections,
        NativeSqlFragment[] groupItems)
    {
        for (var groupIndex = 0; groupIndex < groupItems.Length; groupIndex++)
        {
            var groupItem = groupItems[groupIndex];
            if (!groupItem.Bindings.Any(binding => binding is not NativeSharedSqlBinding))
                continue;

            for (var projectionIndex = 0;
                 projectionIndex < statement.Select.Length;
                 projectionIndex++)
            {
                var projectedExpression = RenderExpression(
                    statement.Select[projectionIndex].Expression);
                if (!EquivalentParameterizedExpression(
                        groupItem,
                        projectedExpression))
                {
                    continue;
                }

                var keyPrefix =
                    "postgres-group:" +
                    statement.Span.Start + ":" +
                    statement.Span.End + ":" +
                    projectionIndex + ":";

                projections[projectionIndex] = projections[projectionIndex] with
                {
                    Bindings = ShareGroupingBindings(
                        projections[projectionIndex].Bindings,
                        keyPrefix)
                };
                groupItems[groupIndex] = groupItem with
                {
                    Bindings = ShareGroupingBindings(
                        groupItem.Bindings,
                        keyPrefix)
                };
                break;
            }
        }
    }

    private static bool EquivalentParameterizedExpression(
        NativeSqlFragment left,
        NativeSqlFragment right)
    {
        if (!string.Equals(left.Sql, right.Sql, StringComparison.Ordinal)
            || left.Bindings.Length != right.Bindings.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Bindings.Length; i++)
        {
            if (!Equals(
                    SharedBindingValue(left.Bindings[i]),
                    SharedBindingValue(right.Bindings[i])))
            {
                return false;
            }
        }

        return true;
    }

    private static object? SharedBindingValue(object? binding) =>
        binding is NativeSharedSqlBinding shared
            ? shared.Value
            : binding;

    private static ImmutableArray<object?> ShareGroupingBindings(
        ImmutableArray<object?> bindings,
        string keyPrefix)
    {
        var shared = ImmutableArray.CreateBuilder<object?>(bindings.Length);
        for (var i = 0; i < bindings.Length; i++)
        {
            var binding = bindings[i];
            shared.Add(binding is NativeSharedSqlBinding
                ? binding
                : new NativeSharedSqlBinding(
                    keyPrefix + i,
                    binding));
        }
        return shared.ToImmutable();
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
                if (limit is null)
                {
                    return new NativeSqlFragment(
                        "OFFSET " +
                        NativeSqlParameterizer.Placeholder + " ROWS",
                        [offset!.Value]);
                }

                return new NativeSqlFragment(
                    "OFFSET " +
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

    private NativeSqlFragment RenderPredicateExpression(
        SqlExpr expression,
        bool dmlContext = false) =>
        NativeSqlExpressionRenderer.RenderPredicate(
            expression,
            Provider,
            statement => RenderStatement(
                statement,
                QueryPosition.ScalarSubquery),
            dmlContext);

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
