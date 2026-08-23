using System.Collections.Immutable;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Binding;

public sealed record QueryAliasFact(string Alias, string Target, int ScopeId);

public sealed record QueryFacts(
    ImmutableHashSet<string> ReferencedTables,
    ImmutableArray<QueryAliasFact> Aliases,
    bool ContainsSubquery,
    bool ContainsCte);

/// <summary>
/// Transitional facts pass over the public DTO model. This centralizes table discovery while the
/// parser is migrated to the independent Core AST. Alias uniqueness is enforced per SQL scope,
/// CTE visibility follows declaration order, and unknown node kinds fail closed.
/// </summary>
public static class QueryFactsBinder
{
    public static QueryFacts Bind(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var state = new State();
        VisitQuery(
            definition,
            state,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));
        return new QueryFacts(
            state.Tables.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            state.Aliases.ToImmutableArray(),
            state.ContainsSubquery,
            state.ContainsCte);
    }

    private static void VisitQuery(
        QueryDefinition query,
        State state,
        ImmutableHashSet<string> inheritedCtes)
    {
        var scopeId = state.NextScopeId++;
        var scopeAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localCtes = inheritedCtes;

        if (query.CteConditions is { Count: > 0 })
        {
            state.ContainsCte = true;
            foreach (var cte in query.CteConditions)
            {
                // Match SqlAstBinder: a CTE body can see inherited and previously declared CTEs,
                // but not itself or a later sibling. The legacy DTO does not retain WITH RECURSIVE,
                // so treating a self-reference as physical is the safer fail-closed behavior.
                VisitQuery(cte.Query, state, localCtes);
                if (!string.IsNullOrWhiteSpace(cte.CteAliasName))
                    localCtes = localCtes.Add(cte.CteAliasName.Trim());
            }
        }

        if (query.FromQuery is not null)
        {
            state.ContainsSubquery = true;
            RegisterAlias(query.Alias, "<subquery>", scopeId, scopeAliases, state);
            VisitQuery(query.FromQuery, state, localCtes);
        }
        else
        {
            RegisterPhysicalTable(
                query.TableName,
                query.Alias,
                localCtes,
                scopeId,
                scopeAliases,
                state);
        }

        if (query.Joins is not null)
        {
            foreach (var join in query.Joins)
            {
                if (join.SubQuery is not null)
                {
                    state.ContainsSubquery = true;
                    RegisterAlias(join.Alias, "<subquery>", scopeId, scopeAliases, state);
                    VisitQuery(join.SubQuery, state, localCtes);
                }
                else
                {
                    RegisterPhysicalTable(
                        join.Table,
                        join.Alias,
                        localCtes,
                        scopeId,
                        scopeAliases,
                        state);
                }
                VisitWhere(join.OnConditions, state, localCtes);
            }
        }

        VisitSelect(query.SelectColumns, state, localCtes);
        VisitWhere(query.WhereColumnsAndValues, state, localCtes);
        VisitHaving(query.HavingConditions, state, localCtes);

        if (query.CombineConditions is not null)
            foreach (var combine in query.CombineConditions)
                VisitQuery(combine.Query, state, localCtes);
    }

    private static void VisitSelect(
        IEnumerable<SelectCondition>? nodes,
        State state,
        ImmutableHashSet<string> ctes)
    {
        if (nodes is null) return;
        foreach (var node in nodes) VisitSelect(node, state, ctes);
    }

    private static void VisitSelect(
        SelectCondition node,
        State state,
        ImmutableHashSet<string> ctes)
    {
        switch (node)
        {
            case FieldSelectCondition:
            case ConstantSelectCondition:
            case IntervalSelectCondition:
                return;
            case OperationSelectCondition operation:
                VisitSelect(operation.Left, state, ctes);
                VisitSelect(operation.Right, state, ctes);
                return;
            case FunctionSelectCondition function:
                VisitSelect(function.Arguments, state, ctes);
                VisitWhere(function.FilterWhereConditions, state, ctes);
                return;
            case CastSelectCondition cast:
                VisitSelect(cast.Expression, state, ctes);
                return;
            case CaseWhenSelectCondition @case:
                foreach (var clause in @case.CaseWhen)
                    VisitWhere([clause.Condition], state, ctes);
                return;
            case SubQuerySelectCondition subquery:
                state.ContainsSubquery = true;
                VisitSubQuerySelect(subquery, state, ctes);
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported SELECT node while binding query facts: {node.GetType().Name}");
        }
    }

    private static void VisitSubQuerySelect(
        SubQuerySelectCondition query,
        State state,
        ImmutableHashSet<string> inheritedCtes)
    {
        var scopeId = state.NextScopeId++;
        var scopeAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localCtes = inheritedCtes;

        if (query.CteConditions is { Count: > 0 })
        {
            state.ContainsCte = true;
            foreach (var cte in query.CteConditions)
            {
                VisitQuery(cte.Query, state, localCtes);
                if (!string.IsNullOrWhiteSpace(cte.CteAliasName))
                    localCtes = localCtes.Add(cte.CteAliasName.Trim());
            }
        }

        if (query.FromQuery is not null)
        {
            RegisterAlias(query.Alias, "<subquery>", scopeId, scopeAliases, state);
            VisitQuery(query.FromQuery, state, localCtes);
        }
        else
        {
            RegisterPhysicalTable(
                query.TableName,
                query.Alias,
                localCtes,
                scopeId,
                scopeAliases,
                state);
        }

        if (query.Joins is not null)
        {
            foreach (var join in query.Joins)
            {
                if (join.SubQuery is not null)
                {
                    RegisterAlias(join.Alias, "<subquery>", scopeId, scopeAliases, state);
                    VisitQuery(join.SubQuery, state, localCtes);
                }
                else
                {
                    RegisterPhysicalTable(
                        join.Table,
                        join.Alias,
                        localCtes,
                        scopeId,
                        scopeAliases,
                        state);
                }
                VisitWhere(join.OnConditions, state, localCtes);
            }
        }

        VisitSelect(query.SelectColumns, state, localCtes);
        VisitWhere(query.WhereColumnsAndValues, state, localCtes);
        VisitHaving(query.HavingConditions, state, localCtes);
        if (query.CombineConditions is not null)
            foreach (var combine in query.CombineConditions)
                VisitQuery(combine.Query, state, localCtes);
    }

    private static void VisitWhere(
        IEnumerable<WhereCondition>? nodes,
        State state,
        ImmutableHashSet<string> ctes)
    {
        if (nodes is null) return;
        foreach (var node in nodes)
        {
            switch (node)
            {
                case BasicWhereCondition:
                case ColumnCompareWhereCondition:
                    break;
                case ExpressionWhereCondition expression:
                    VisitSelect(expression.LeftExpression, state, ctes);
                    if (expression.RightExpression is not null)
                        VisitSelect(expression.RightExpression, state, ctes);
                    break;
                case GroupWhereCondition group:
                    VisitWhere(group.Groups, state, ctes);
                    break;
                case SubQueryWhereCondition subquery:
                    state.ContainsSubquery = true;
                    VisitQuery(subquery.SubQuery, state, ctes);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported WHERE node while binding query facts: {node.GetType().Name}");
            }
        }
    }

    private static void VisitHaving(
        IEnumerable<HavingCondition>? nodes,
        State state,
        ImmutableHashSet<string> ctes)
    {
        if (nodes is null) return;
        foreach (var node in nodes)
        {
            switch (node)
            {
                case BasicHavingCondition:
                    break;
                case FunctionHavingCondition function:
                    VisitSelect(function.LeftFunction.Arguments, state, ctes);
                    VisitWhere(function.LeftFunction.FilterWhereConditions, state, ctes);
                    break;
                case ExpressionHavingCondition expression:
                    VisitSelect(expression.LeftExpression, state, ctes);
                    if (expression.RightExpression is not null)
                        VisitSelect(expression.RightExpression, state, ctes);
                    break;
                case GroupHavingCondition group:
                    VisitHaving(group.Groups, state, ctes);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported HAVING node while binding query facts: {node.GetType().Name}");
            }
        }
    }

    private static void RegisterPhysicalTable(
        string? tableName,
        string? alias,
        ImmutableHashSet<string> ctes,
        int scopeId,
        HashSet<string> scopeAliases,
        State state)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return;
        var normalized = tableName.Trim();
        if (!ctes.Contains(normalized)) state.Tables.Add(normalized);
        RegisterAlias(alias, normalized, scopeId, scopeAliases, state);
    }

    private static void RegisterAlias(
        string? alias,
        string target,
        int scopeId,
        HashSet<string> scopeAliases,
        State state)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;
        var normalized = alias.Trim();
        if (!scopeAliases.Add(normalized))
            throw new InvalidOperationException(
                $"Duplicate table alias '{normalized}' in SQL scope {scopeId}.");
        state.Aliases.Add(new QueryAliasFact(normalized, target, scopeId));
    }

    private sealed class State
    {
        public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<QueryAliasFact> Aliases { get; } = [];
        public int NextScopeId { get; set; }
        public bool ContainsSubquery { get; set; }
        public bool ContainsCte { get; set; }
    }
}
