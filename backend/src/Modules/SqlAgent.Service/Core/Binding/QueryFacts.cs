using System.Collections.Immutable;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Binding;

public sealed record QueryFacts(
    ImmutableHashSet<string> ReferencedTables,
    ImmutableDictionary<string, string> Aliases,
    bool ContainsSubquery,
    bool ContainsCte);

/// <summary>
/// Transitional binding/facts pass over the public DTO model. This centralizes table discovery
/// while the parser is migrated to the independent Core AST. Unknown node kinds fail closed.
/// </summary>
public static class QueryFactsBinder
{
    public static QueryFacts Bind(QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var state = new State();
        VisitQuery(definition, state, ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));
        return new QueryFacts(
            state.Tables.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            state.Aliases.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            state.ContainsSubquery,
            state.ContainsCte);
    }

    private static void VisitQuery(QueryDefinition query, State state, ImmutableHashSet<string> inheritedCtes)
    {
        var localCtes = inheritedCtes;
        if (query.CteConditions is { Count: > 0 })
        {
            state.ContainsCte = true;
            foreach (var cte in query.CteConditions)
            {
                if (!string.IsNullOrWhiteSpace(cte.CteAliasName))
                    localCtes = localCtes.Add(cte.CteAliasName);
            }
            foreach (var cte in query.CteConditions)
                if (cte.Query is not null)
                    VisitQuery(cte.Query, state, localCtes);
        }

        if (query.FromQuery is not null)
        {
            state.ContainsSubquery = true;
            RegisterAlias(query.Alias, "<subquery>", state);
            VisitQuery(query.FromQuery, state, localCtes);
        }
        else
        {
            RegisterPhysicalTable(query.TableName, query.Alias, localCtes, state);
        }

        if (query.Joins is not null)
        {
            foreach (var join in query.Joins)
            {
                if (join.SubQuery is not null)
                {
                    state.ContainsSubquery = true;
                    RegisterAlias(join.Alias, "<subquery>", state);
                    VisitQuery(join.SubQuery, state, localCtes);
                }
                else
                {
                    RegisterPhysicalTable(join.Table, join.Alias, localCtes, state);
                }
                VisitWhere(join.OnConditions, state, localCtes);
            }
        }

        VisitSelect(query.SelectColumns, state, localCtes);
        VisitWhere(query.WhereColumnsAndValues, state, localCtes);
        VisitHaving(query.HavingConditions, state, localCtes);

        if (query.CombineConditions is not null)
            foreach (var combine in query.CombineConditions)
                if (combine.Query is not null)
                    VisitQuery(combine.Query, state, inheritedCtes);
    }

    private static void VisitSelect(IEnumerable<SelectCondition>? nodes, State state, ImmutableHashSet<string> ctes)
    {
        if (nodes is null) return;
        foreach (var node in nodes) VisitSelect(node, state, ctes);
    }

    private static void VisitSelect(SelectCondition node, State state, ImmutableHashSet<string> ctes)
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
                // Internal semantic/template nodes are compiler implementation details and must
                // never silently affect authorization facts.
                throw new InvalidOperationException(
                    $"Unsupported SELECT node while binding query facts: {node.GetType().Name}");
        }
    }

    private static void VisitSubQuerySelect(SubQuerySelectCondition query, State state, ImmutableHashSet<string> ctes)
    {
        if (query.CteConditions is { Count: > 0 })
        {
            state.ContainsCte = true;
            foreach (var cte in query.CteConditions)
                if (cte.Query is not null)
                    VisitQuery(cte.Query, state, ctes);
        }
        if (query.FromQuery is not null) VisitQuery(query.FromQuery, state, ctes);
        else RegisterPhysicalTable(query.TableName, query.Alias, ctes, state);
        if (query.Joins is not null)
            foreach (var join in query.Joins)
            {
                if (join.SubQuery is not null) VisitQuery(join.SubQuery, state, ctes);
                else RegisterPhysicalTable(join.Table, join.Alias, ctes, state);
                VisitWhere(join.OnConditions, state, ctes);
            }
        VisitSelect(query.SelectColumns, state, ctes);
        VisitWhere(query.WhereColumnsAndValues, state, ctes);
        VisitHaving(query.HavingConditions, state, ctes);
        if (query.CombineConditions is not null)
            foreach (var combine in query.CombineConditions)
                if (combine.Query is not null) VisitQuery(combine.Query, state, ctes);
    }

    private static void VisitWhere(IEnumerable<WhereCondition>? nodes, State state, ImmutableHashSet<string> ctes)
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
                    if (expression.RightExpression is not null) VisitSelect(expression.RightExpression, state, ctes);
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

    private static void VisitHaving(IEnumerable<HavingCondition>? nodes, State state, ImmutableHashSet<string> ctes)
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
                    if (expression.RightExpression is not null) VisitSelect(expression.RightExpression, state, ctes);
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
        State state)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return;
        var normalized = tableName.Trim();
        if (!ctes.Contains(normalized)) state.Tables.Add(normalized);
        RegisterAlias(alias, normalized, state);
    }

    private static void RegisterAlias(string? alias, string target, State state)
    {
        if (string.IsNullOrWhiteSpace(alias)) return;
        if (!state.Aliases.TryAdd(alias.Trim(), target))
            throw new InvalidOperationException($"Duplicate table alias '{alias.Trim()}'.");
    }

    private sealed class State
    {
        public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ContainsSubquery { get; set; }
        public bool ContainsCte { get; set; }
    }
}
