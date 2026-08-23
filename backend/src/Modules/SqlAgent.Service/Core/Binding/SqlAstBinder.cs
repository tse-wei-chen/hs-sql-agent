using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Core.Pipeline;
using SqlAgent.Service.Enums;

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
        var state = new BindingState(statement.SourceDialect);
        var bound = BindStatement(
            statement.Statement,
            parentScope: null,
            ImmutableHashSet<string>.Empty.WithComparer(state.IdentifierComparer),
            state);

        return new BoundStatement(
            bound,
            new QueryFacts(
                state.PhysicalTables.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                state.AliasFacts.ToImmutableArray(),
                state.ContainsSubquery,
                state.ContainsCte),
            statement.SourceDialect);
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
            UpdateStatement update => BindUpdate(update, parentScope, inheritedCtes, state),
            DeleteStatement delete => BindDelete(delete, parentScope, inheritedCtes, state),
            _ => throw new InvalidOperationException(
                $"Unsupported SQL statement while binding: {statement.GetType().Name}")
        };
    }

    private static UpdateStatement BindUpdate(
        UpdateStatement update,
        BindingScope? parentScope,
        ImmutableHashSet<string> visibleCtes,
        BindingState state)
    {
        if (update.Assignments.IsDefaultOrEmpty)
            throw new InvalidOperationException("UPDATE requires at least one assignment.");

        var scope = new BindingScope(state.NextScopeId++, parentScope, state);
        var target = (NamedTableSource)BindSource(update.Target, scope, visibleCtes, state);
        var assignments = update.Assignments.Select(assignment =>
        {
            if (assignment.Column.Parts.Length != 1)
            {
                throw new InvalidOperationException(
                    $"UPDATE assignment column '{Name(assignment.Column)}' must be unqualified.");
            }
            return assignment with
            {
                Value = BindExpr(assignment.Value, scope, visibleCtes, state)
            };
        }).ToImmutableArray();

        return update with
        {
            Target = target,
            Assignments = assignments,
            Predicate = update.Predicate is null
                ? null
                : BindExpr(update.Predicate, scope, visibleCtes, state)
        };
    }

    private static DeleteStatement BindDelete(
        DeleteStatement delete,
        BindingScope? parentScope,
        ImmutableHashSet<string> visibleCtes,
        BindingState state)
    {
        var scope = new BindingScope(state.NextScopeId++, parentScope, state);
        var target = (NamedTableSource)BindSource(delete.Target, scope, visibleCtes, state);
        return delete with
        {
            Target = target,
            Predicate = delete.Predicate is null
                ? null
                : BindExpr(delete.Predicate, scope, visibleCtes, state)
        };
    }

    private static QueryStatement BindQuery(
        QueryStatement query,
        BindingScope? parentScope,
        ImmutableHashSet<string> inheritedCtes,
        BindingState state)
    {
        var head = BindSelect(query.Head, parentScope, inheritedCtes, state);
        var visibleCtes = inheritedCtes;
        foreach (var cte in query.Head.Ctes)
            visibleCtes = visibleCtes.Add(state.IdentifierKey(cte.Name));

        var operations = query.SetOperations
            .Select(operation => operation with
            {
                Query = BindStatement(operation.Query, parentScope, visibleCtes, state)
            })
            .ToImmutableArray();

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
        var boundCtesBuilder = ImmutableArray.CreateBuilder<CteDefinition>(select.Ctes.Length);
        if (!select.Ctes.IsDefaultOrEmpty)
        {
            state.ContainsCte = true;
            foreach (var cte in select.Ctes)
            {
                var boundQuery = BindStatement(cte.Query, null, localCtes, state);
                boundCtesBuilder.Add(cte with { Query = boundQuery });
                localCtes = localCtes.Add(state.IdentifierKey(cte.Name));
            }
        }
        var boundCtes = boundCtesBuilder.ToImmutable();

        var scope = new BindingScope(state.NextScopeId++, parentScope, state);
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
                var isCte = visibleCtes.Contains(state.IdentifierKey(named.Name));
                if (!isCte) state.PhysicalTables.Add(tableName);

                var symbol = new TableSymbol(
                    tableName,
                    AliasValue(named.Alias),
                    IsDerived: false,
                    IsCte: isCte,
                    named.Span);
                scope.AddNamed(symbol, named.Name, named.Alias);
                RegisterAliasFact(symbol, scope.Id, state);
                return named;
            }
            case DerivedTableSource derived:
            {
                state.ContainsSubquery = true;
                if (string.IsNullOrWhiteSpace(derived.Alias.Value))
                    throw new InvalidOperationException("Derived table must have an alias before binding.");

                var query = BindStatement(derived.Query, null, visibleCtes, state);
                var symbol = new TableSymbol(
                    "<subquery>",
                    derived.Alias.Value.Trim(),
                    IsDerived: true,
                    IsCte: false,
                    derived.Span);
                scope.AddDerived(symbol, derived.Alias);
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
            UnaryExpr unary => unary with { Operand = BindExpr(unary.Operand, scope, visibleCtes, state) },
            BinaryExpr binary => binary with
            {
                Left = BindExpr(binary.Left, scope, visibleCtes, state),
                Right = BindExpr(binary.Right, scope, visibleCtes, state)
            },
            FunctionCallExpr function when function.Name.Parts.Length != 1
                || function.Name.Parts[0].WasQuoted =>
                throw new InvalidOperationException(
                    $"Quoted or qualified function identifier '{Name(function.Name)}' is not supported by the portable Core function registry."),
            FunctionCallExpr function => function with
            {
                Arguments = function.Arguments.Select(argument => BindExpr(argument, scope, visibleCtes, state)).ToImmutableArray()
            },
            FilterExpr filter => filter with
            {
                Expression = BindExpr(filter.Expression, scope, visibleCtes, state),
                Predicate = BindExpr(filter.Predicate, scope, visibleCtes, state)
            },
            WindowedExpr windowed => windowed with
            {
                Expression = BindExpr(windowed.Expression, scope, visibleCtes, state),
                Window = BindWindow(windowed.Window, scope, visibleCtes, state)
            },
            CastExpr cast => cast with { Expression = BindExpr(cast.Expression, scope, visibleCtes, state) },
            CaseExpr @case => @case with
            {
                Branches = @case.Branches.Select(branch => new CaseBranch(
                    BindExpr(branch.Condition, scope, visibleCtes, state),
                    BindExpr(branch.Value, scope, visibleCtes, state))).ToImmutableArray(),
                ElseExpression = @case.ElseExpression is null ? null : BindExpr(@case.ElseExpression, scope, visibleCtes, state)
            },
            InExpr @in => @in with
            {
                Value = BindExpr(@in.Value, scope, visibleCtes, state),
                Items = @in.Items.Select(item => BindExpr(item, scope, visibleCtes, state)).ToImmutableArray()
            },
            BetweenExpr between => between with
            {
                Value = BindExpr(between.Value, scope, visibleCtes, state),
                Lower = BindExpr(between.Lower, scope, visibleCtes, state),
                Upper = BindExpr(between.Upper, scope, visibleCtes, state)
            },
            IsNullExpr isNull => isNull with { Value = BindExpr(isNull.Value, scope, visibleCtes, state) },
            SubqueryExpr subquery => BindSubquery(subquery, scope, visibleCtes, state),
            ExistsExpr exists => BindExists(exists, scope, visibleCtes, state),
            _ => throw new InvalidOperationException(
                $"Unsupported SQL expression while binding: {expression.GetType().Name}")
        };
    }

    private static WindowSpec BindWindow(
        WindowSpec window,
        BindingScope? scope,
        ImmutableHashSet<string> visibleCtes,
        BindingState state) =>
        window with
        {
            PartitionBy = window.PartitionBy.Select(expression => BindExpr(expression, scope, visibleCtes, state)).ToImmutableArray(),
            OrderBy = window.OrderBy.Select(item => item with
            {
                Expression = BindExpr(item.Expression, scope, visibleCtes, state)
            }).ToImmutableArray()
        };

    private static BoundColumnExpr BindColumn(ColumnExpr column, BindingScope? scope)
    {
        if (scope is null) return new BoundColumnExpr(column.Name, null, column.Span);

        var parts = column.Name.Parts;
        if (parts.IsDefaultOrEmpty) throw new InvalidOperationException("Column identifier has no parts.");

        if (parts.Length == 1)
            return new BoundColumnExpr(column.Name, scope.TryResolveSingleVisibleSource(), column.Span);

        var qualifierParts = parts.Take(parts.Length - 1).ToArray();
        var qualifier = string.Join('.', qualifierParts.Select(p => p.Value));
        var resolved = scope.ResolveQualifier(qualifierParts);
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
        return subquery with { Query = BindStatement(subquery.Query, scope, visibleCtes, state) };
    }

    private static ExistsExpr BindExists(
        ExistsExpr exists,
        BindingScope? scope,
        ImmutableHashSet<string> visibleCtes,
        BindingState state)
    {
        state.ContainsSubquery = true;
        return exists with { Query = BindStatement(exists.Query, scope, visibleCtes, state) };
    }

    private static void RegisterAliasFact(TableSymbol symbol, int scopeId, BindingState state)
    {
        if (!string.IsNullOrWhiteSpace(symbol.Alias))
            state.AliasFacts.Add(new QueryAliasFact(symbol.Alias, symbol.Name, scopeId));
    }

    private static string Name(SqlIdentifier identifier) => string.Join('.', identifier.Parts.Select(part => part.Value));
    private static string? AliasValue(IdentifierPart? alias) =>
        alias is null || string.IsNullOrWhiteSpace(alias.Value) ? null : alias.Value.Trim();

    private sealed class BindingState
    {
        public BindingState(SqlAgentToolType sourceDialect)
        {
            SourceDialect = sourceDialect;
            IdentifierComparer = sourceDialect is SqlAgentToolType.Postgres or SqlAgentToolType.Oracle or SqlAgentToolType.Firebird
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
        }

        private SqlAgentToolType SourceDialect { get; }
        public StringComparer IdentifierComparer { get; }
        public HashSet<string> PhysicalTables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<QueryAliasFact> AliasFacts { get; } = [];
        public int NextScopeId { get; set; }
        public bool ContainsSubquery { get; set; }
        public bool ContainsCte { get; set; }

        public string IdentifierKey(SqlIdentifier identifier) => IdentifierKey(identifier.Parts);

        public string IdentifierKey(IEnumerable<IdentifierPart> parts)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                var value = CanonicalPart(part);
                builder.Append(value.Length).Append(':').Append(value).Append(';');
            }
            return builder.ToString();
        }

        private string CanonicalPart(IdentifierPart part)
        {
            if (part.WasQuoted) return part.Value;
            return SourceDialect switch
            {
                SqlAgentToolType.Postgres => part.Value.ToLowerInvariant(),
                SqlAgentToolType.Oracle or SqlAgentToolType.Firebird => part.Value.ToUpperInvariant(),
                _ => part.Value
            };
        }
    }

    private sealed class BindingScope(int id, BindingScope? parent, BindingState state)
    {
        private readonly List<TableSymbol> _sources = [];
        private readonly Dictionary<string, List<TableSymbol>> _qualifiers = new(state.IdentifierComparer);
        private readonly HashSet<string> _aliasKeys = new(state.IdentifierComparer);

        public int Id { get; } = id;
        public BindingScope? Parent { get; } = parent;

        public void AddNamed(TableSymbol symbol, SqlIdentifier name, IdentifierPart? alias)
        {
            RegisterAlias(alias, symbol);
            _sources.Add(symbol);
            AddQualifier(state.IdentifierKey(name), symbol);
            if (!name.Parts.IsDefaultOrEmpty)
                AddQualifier(state.IdentifierKey([name.Parts[^1]]), symbol);
            if (alias is not null)
                AddQualifier(state.IdentifierKey([alias]), symbol);
        }

        public void AddDerived(TableSymbol symbol, IdentifierPart alias)
        {
            RegisterAlias(alias, symbol);
            _sources.Add(symbol);
            AddQualifier(state.IdentifierKey([alias]), symbol);
        }

        public TableSymbol? ResolveQualifier(IEnumerable<IdentifierPart> qualifierParts)
        {
            var key = state.IdentifierKey(qualifierParts);
            if (_qualifiers.TryGetValue(key, out var matches))
            {
                if (matches.Count != 1)
                {
                    var qualifier = string.Join('.', qualifierParts.Select(part => part.Value));
                    throw new InvalidOperationException($"Ambiguous table/alias qualifier '{qualifier}' in SQL scope {Id}.");
                }
                return matches[0];
            }
            return Parent?.ResolveQualifier(qualifierParts);
        }

        public TableSymbol? TryResolveSingleVisibleSource()
        {
            if (_sources.Count == 1) return _sources[0];
            if (_sources.Count > 1) return null;
            return Parent?.TryResolveSingleVisibleSource();
        }

        private void RegisterAlias(IdentifierPart? alias, TableSymbol symbol)
        {
            if (alias is null) return;
            var key = state.IdentifierKey([alias]);
            if (!_aliasKeys.Add(key))
            {
                throw new InvalidOperationException(
                    $"Duplicate table alias '{symbol.Alias}' in SQL scope {Id}.");
            }
        }

        private void AddQualifier(string key, TableSymbol symbol)
        {
            if (!_qualifiers.TryGetValue(key, out var matches))
            {
                matches = [];
                _qualifiers[key] = matches;
            }
            if (!matches.Contains(symbol)) matches.Add(symbol);
        }
    }
}
