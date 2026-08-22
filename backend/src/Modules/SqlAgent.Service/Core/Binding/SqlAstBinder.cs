using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;

namespace SqlAgent.Service.Core.Binding;

/// <summary>
/// Scope-aware binder for the independent Core AST. It resolves qualified column references to
/// table/CTE/derived-table symbols and produces authorization/audit facts in the same traversal.
/// Schema-aware resolution of ambiguous unqualified columns is intentionally deferred.
/// </summary>
public sealed class SqlAstBinder : ISqlBinder
{
    public BoundStatement Bind(ParsedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var state = new BindingState();
        var bound = BindStatement(
            statement.Statement,
            parentScope: null,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase),
            state);

        return new BoundStatement(
            bound,
            new QueryFacts(
                state.PhysicalTables.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                state.AliasFacts.ToImmutableArray(),
                state.ContainsSubquery,
                state.ContainsCte));
    }

    private static SqlStatement BindStatement(
        SqlStatement statement,
        BindingScope? parentScope,
        ImmutableHashSet<string> inheritedCtes,
        BindingState state)
    {
        return statement switch
        {
            SelectStatement select => BindSelect(select, parentScope, inheritedCtes, state),
            QueryStatement query => BindQuery(query, parentScope, inheritedCtes, state),
            _ => throw new InvalidOperationException(
                $"Unsupported SQL statement while binding: {statement.GetType().Name}")
        };
    }

    private static QueryStatement BindQuery(
        QueryStatement query,
        BindingScope? parentScope,
        ImmutableHashSet<string> inheritedCtes,
        BindingState state)
    {
        var visibleCtes = inheritedCtes;
        foreach (var cte in query.Head.Ctes)
            visibleCtes = visibleCtes.Add(Name(cte.Name));

        var head = BindSelect(query.Head, parentScope, inheritedCtes, state);
        var operations = query.SetOperations
            .Select(operation => operation with
            {
                Query = BindStatement(operation.Query, parentScope, visibleCtes, state)
            })
            .ToImmutableArray();

        // Query-level ORDER BY binds against the output projection, not an individual FROM scope.
        // Keep output-column references explicitly unresolved for a later projection binder.
        var orderBy = query.OrderBy
            .Select(item => item with { Expression = BindExpr(item.Expression, null, visibleCtes, state) })
            .ToImmutableArray();

        return query with { Head = head, SetOperations = operations, OrderBy = orderBy };
    }

    private static SelectStatement BindSelect(
        SelectStatement select,
        BindingScope? parentScope,
        ImmutableHashSet<string> inheritedCtes,
        BindingState state)
    {
        var localCtes = inheritedCtes;
        if (!select.Ctes.IsDefaultOrEmpty)
        {
            state.ContainsCte = true;
            foreach (var cte in select.Ctes)
                localCtes = localCtes.Add(Name(cte.Name));
        }

        var boundCtes = select.Ctes
            .Select(cte => cte with
            {
                Query = BindStatement(cte.Query, null, localCtes, state)
            })
            .ToImmutableArray();

        var scope = new BindingScope(state.NextScopeId++, parentScope);
        var boundFrom = select.From is null
            ? null
            : BindSource(select.From, scope, localCtes, state);

        var boundJoins = ImmutableArray.CreateBuilder<JoinSource>(select.Joins.Length);
        foreach (var join in select.Joins)
        {
            var source = BindSource(join.Source, scope, localCtes, state);
            var predicate = join.Predicate is null
                ? null
                : BindExpr(join.Predicate, scope, localCtes, state);
            boundJoins.Add(join with { Source = source, Predicate = predicate });
        }

        return select with
        {
            Ctes = boundCtes,
            From = boundFrom,
            Joins = boundJoins.ToImmutable(),
            Select = select.Select
                .Select(item => item with { Expression = BindExpr(item.Expression, scope, localCtes, state) })
                .ToImmutableArray(),
            Where = select.Where is null ? null : BindExpr(select.Where, scope, localCtes, state),
            GroupBy = select.GroupBy
                .Select(expression => BindExpr(expression, scope, localCtes, state))
                .ToImmutableArray(),
            Having = select.Having is null ? null : BindExpr(select.Having, scope, localCtes, state),
            OrderBy = select.OrderBy
                .Select(item => item with { Expression = BindExpr(item.Expression, scope, localCtes, state) })
                .ToImmutableArray()
        };
    }

    private static TableSource BindSource(
        TableSource source,
        BindingScope scope,
        ImmutableHashSet<string> visibleCtes,
        BindingState state)
    {
        switch (source)
        {
            case NamedTableSource named:
            {
                var tableName = Name(named.Name);
                var isCte = visibleCtes.Contains(tableName);
                if (!isCte)
                    state.PhysicalTables.Add(tableName);

                var symbol = new TableSymbol(
                    tableName,
                    NormalizeAlias(named.Alias),
                    IsDerived: false,
                    IsCte: isCte,
                    named.Span);
                scope.Add(symbol);
                RegisterAliasFact(symbol, scope.Id, state);
                return named;
            }
            case DerivedTableSource derived:
            {
                state.ContainsSubquery = true;
                if (string.IsNullOrWhiteSpace(derived.Alias))
                    throw new InvalidOperationException("Derived table must have an alias before binding.");

                // A non-LATERAL derived table does not inherit the surrounding table scope.
                var query = BindStatement(derived.Query, null, visibleCtes, state);
                var symbol = new TableSymbol(
                    "<subquery>",
                    derived.Alias.Trim(),
                    IsDerived: true,
                    IsCte: false,
                    derived.Span);
                scope.Add(symbol);
                RegisterAliasFact(symbol, scope.Id, state);
                return derived with { Query = query };
            }
            default:
                throw new InvalidOperationException(
                    $"Unsupported table source while binding: {source.GetType().Name}");
        }
    }

    private static SqlExpr BindExpr(
        SqlExpr expression,
        BindingScope? scope,
        ImmutableHashSet<string> visibleCtes,
        BindingState state)
    {
        return expression switch
        {
            BoundColumnExpr bound => bound,
            ColumnExpr column => BindColumn(column, scope),
            LiteralExpr literal => literal,
            IntervalExpr interval => interval,
            UnaryExpr unary => unary with
            {
                Operand = BindExpr(unary.Operand, scope, visibleCtes, state)
            },
            BinaryExpr binary => binary with
            {
                Left = BindExpr(binary.Left, scope, visibleCtes, state),
                Right = BindExpr(binary.Right, scope, visibleCtes, state)
            },
            FunctionCallExpr function => function with
            {
                Arguments = function.Arguments
                    .Select(argument => BindExpr(argument, scope, visibleCtes, state))
                    .ToImmutableArray()
            },
            CastExpr cast => cast with
            {
                Expression = BindExpr(cast.Expression, scope, visibleCtes, state)
            },
            CaseExpr @case => @case with
            {
                Branches = @case.Branches
                    .Select(branch => new CaseBranch(
                        BindExpr(branch.Condition, scope, visibleCtes, state),
                        BindExpr(branch.Value, scope, visibleCtes, state)))
                    .ToImmutableArray(),
                ElseExpression = @case.ElseExpression is null
                    ? null
                    : BindExpr(@case.ElseExpression, scope, visibleCtes, state)
            },
            InExpr @in => @in with
            {
                Value = BindExpr(@in.Value, scope, visibleCtes, state),
                Items = @in.Items
                    .Select(item => BindExpr(item, scope, visibleCtes, state))
                    .ToImmutableArray()
            },
            BetweenExpr between => between with
            {
                Value = BindExpr(between.Value, scope, visibleCtes, state),
                Lower = BindExpr(between.Lower, scope, visibleCtes, state),
                Upper = BindExpr(between.Upper, scope, visibleCtes, state)
            },
            IsNullExpr isNull => isNull with
            {
                Value = BindExpr(isNull.Value, scope, visibleCtes, state)
            },
            SubqueryExpr subquery => BindSubquery(subquery, scope, visibleCtes, state),
            ExistsExpr exists => BindExists(exists, scope, visibleCtes, state),
            _ => throw new InvalidOperationException(
                $"Unsupported SQL expression while binding: {expression.GetType().Name}")
        };
    }

    private static BoundColumnExpr BindColumn(ColumnExpr column, BindingScope? scope)
    {
        if (scope is null)
            return new BoundColumnExpr(column.Name, null, column.Span);

        var parts = column.Name.Parts;
        if (parts.IsDefaultOrEmpty)
            throw new InvalidOperationException("Column identifier has no parts.");

        if (parts.Length == 1)
        {
            var source = scope.TryResolveSingleVisibleSource();
            return new BoundColumnExpr(column.Name, source, column.Span);
        }

        var qualifier = string.Join('.', parts[..^1].Select(p => p.Value));
        var resolved = scope.ResolveQualifier(qualifier);
        if (resolved is null)
            throw new InvalidOperationException(
                $"Column '{Name(column.Name)}' references unknown table/alias qualifier '{qualifier}'.");

        return new BoundColumnExpr(column.Name, resolved, column.Span);
    }

    private static SubqueryExpr BindSubquery(
        SubqueryExpr subquery,
        BindingScope? scope,
        ImmutableHashSet<string> visibleCtes,
        BindingState state)
    {
        state.ContainsSubquery = true;
        return subquery with
        {
            Query = BindStatement(subquery.Query, scope, visibleCtes, state)
        };
    }

    private static ExistsExpr BindExists(
        ExistsExpr exists,
        BindingScope? scope,
        ImmutableHashSet<string> visibleCtes,
        BindingState state)
    {
        state.ContainsSubquery = true;
        return exists with
        {
            Query = BindStatement(exists.Query, scope, visibleCtes, state)
        };
    }

    private static void RegisterAliasFact(TableSymbol symbol, int scopeId, BindingState state)
    {
        if (string.IsNullOrWhiteSpace(symbol.Alias)) return;
        state.AliasFacts.Add(new QueryAliasFact(symbol.Alias, symbol.Name, scopeId));
    }

    private static string Name(SqlIdentifier identifier) =>
        string.Join('.', identifier.Parts.Select(part => part.Value));

    private static string? NormalizeAlias(string? alias) =>
        string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

    private sealed class BindingState
    {
        public HashSet<string> PhysicalTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<QueryAliasFact> AliasFacts { get; } = [];
        public int NextScopeId { get; set; }
        public bool ContainsSubquery { get; set; }
        public bool ContainsCte { get; set; }
    }

    private sealed class BindingScope(int id, BindingScope? parent)
    {
        private readonly List<TableSymbol> _sources = [];
        private readonly Dictionary<string, List<TableSymbol>> _qualifiers =
            new(StringComparer.OrdinalIgnoreCase);

        public int Id { get; } = id;
        public BindingScope? Parent { get; } = parent;

        public void Add(TableSymbol symbol)
        {
            _sources.Add(symbol);
            AddQualifier(symbol.Name, symbol);

            var lastDot = symbol.Name.LastIndexOf('.');
            if (lastDot >= 0)
                AddQualifier(symbol.Name[(lastDot + 1)..], symbol);

            if (!string.IsNullOrWhiteSpace(symbol.Alias))
                AddQualifier(symbol.Alias, symbol);
        }

        public TableSymbol? ResolveQualifier(string qualifier)
        {
            if (_qualifiers.TryGetValue(qualifier, out var matches))
            {
                if (matches.Count != 1)
                    throw new InvalidOperationException(
                        $"Ambiguous table/alias qualifier '{qualifier}' in SQL scope {Id}.");
                return matches[0];
            }
            return Parent?.ResolveQualifier(qualifier);
        }

        public TableSymbol? TryResolveSingleVisibleSource()
        {
            if (_sources.Count == 1) return _sources[0];
            if (_sources.Count > 1) return null;
            return Parent?.TryResolveSingleVisibleSource();
        }

        private void AddQualifier(string qualifier, TableSymbol symbol)
        {
            if (!_qualifiers.TryGetValue(qualifier, out var matches))
            {
                matches = [];
                _qualifiers[qualifier] = matches;
            }
            if (!matches.Contains(symbol)) matches.Add(symbol);
        }
    }
}
