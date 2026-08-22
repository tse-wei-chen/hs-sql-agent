using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Mapping;

/// <summary>
/// Maps structured DML contracts into the same independent Core AST used by query compilation.
/// INSERT remains fail-closed until bulk and INSERT..SELECT semantics have canonical nodes.
/// </summary>
public static class DmlDefinitionCoreMapper
{
    private static readonly SourceSpan Unknown = SourceSpan.Unknown;

    public static SqlStatement Map(DmlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.TableName);

        var target = new NamedTableSource(Identifier(definition.TableName), null, Unknown);
        var predicate = MapPredicate(definition);

        return definition.Operation switch
        {
            DmlOperation.Update => new UpdateStatement(
                target,
                MapAssignments(definition.Values),
                predicate,
                Unknown),
            DmlOperation.Delete => new DeleteStatement(target, predicate, Unknown),
            DmlOperation.Insert => throw new InvalidOperationException(
                "INSERT is not yet represented by the Core DML AST; compilation was rejected."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(definition.Operation), definition.Operation, "Unknown DML operation.")
        };
    }

    private static ImmutableArray<Assignment> MapAssignments(IReadOnlyList<NameValuePair>? values)
    {
        if (values is not { Count: > 0 })
            throw new InvalidOperationException("UPDATE requires at least one assignment.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignments = ImmutableArray.CreateBuilder<Assignment>(values.Count);
        foreach (var pair in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.FieldName);
            if (!seen.Add(pair.FieldName))
                throw new InvalidOperationException($"UPDATE assigns column '{pair.FieldName}' more than once.");
            assignments.Add(new Assignment(
                Identifier(pair.FieldName),
                new LiteralExpr(pair.Value, Unknown),
                Unknown));
        }
        return assignments.ToImmutable();
    }

    private static SqlExpr? MapPredicate(DmlDefinition definition)
    {
        if (definition.WhereConditions is not { Count: > 0 }) return null;

        var carrier = new QueryDefinition
        {
            TableName = definition.TableName,
            SelectColumns = [new ConstantSelectCondition { Constant = 1 }],
            WhereColumnsAndValues = definition.WhereConditions
        };
        var select = QueryDefinitionCoreMapper.Map(carrier) as SelectStatement
            ?? throw new InvalidOperationException("DML predicate carrier did not map to a SELECT statement.");
        return select.Where;
    }

    private static SqlIdentifier Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("SQL identifier cannot be empty.");
        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Invalid SQL identifier '{value}'.");
        return new SqlIdentifier(
            parts.Select(part => new IdentifierPart(part, false, Unknown)).ToImmutableArray(),
            Unknown);
    }
}
