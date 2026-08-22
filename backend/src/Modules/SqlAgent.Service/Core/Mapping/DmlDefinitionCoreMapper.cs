using System.Collections.Immutable;
using SqlAgent.Service.Core.Ast;
using SqlAgent.Service.Enums;
using SqlAgent.Service.Models;

namespace SqlAgent.Service.Core.Mapping;

/// <summary>
/// Maps structured DML contracts into the same independent Core AST used by query compilation.
/// Invalid or ambiguous DML shapes fail closed before binding/lowering.
/// </summary>
public static class DmlDefinitionCoreMapper
{
    private static readonly SourceSpan Unknown = SourceSpan.Unknown;

    public static SqlStatement Map(DmlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.TableName);

        var target = new NamedTableSource(Identifier(definition.TableName), null, Unknown);
        return definition.Operation switch
        {
            DmlOperation.Update => new UpdateStatement(
                target,
                MapAssignments(definition.Values),
                MapPredicate(definition),
                Unknown),
            DmlOperation.Delete => new DeleteStatement(
                target,
                MapPredicate(definition),
                Unknown),
            DmlOperation.Insert => MapInsert(definition, target),
            _ => throw new ArgumentOutOfRangeException(
                nameof(definition.Operation), definition.Operation, "Unknown DML operation.")
        };
    }

    private static InsertStatement MapInsert(
        DmlDefinition definition,
        NamedTableSource target)
    {
        if (definition.WhereConditions is { Count: > 0 })
            throw new InvalidOperationException("INSERT cannot contain WHERE conditions on the target definition.");

        var hasValues = definition.Values is { Count: > 0 };
        var hasMultiValues = definition.MultiValues is { Count: > 0 };
        var hasQuery = definition.FromQuery is not null;
        var sourceCount = (hasValues ? 1 : 0) + (hasMultiValues ? 1 : 0) + (hasQuery ? 1 : 0);
        if (sourceCount != 1)
        {
            throw new InvalidOperationException(
                "INSERT requires exactly one source: Values, MultiValues, or FromQuery.");
        }

        if (hasValues)
        {
            if (definition.Columns is { Count: > 0 })
                throw new InvalidOperationException("Single-row INSERT Values must not also specify Columns; column order comes from the named values.");

            var values = definition.Values!;
            var columns = ValidateColumns(values.Select(pair => pair.FieldName));
            var row = values
                .Select(pair => (SqlExpr)new LiteralExpr(pair.Value, Unknown))
                .ToImmutableArray();
            return new InsertStatement(
                target,
                columns,
                new InsertValuesSource([row], Unknown),
                Unknown);
        }

        var declaredColumns = ValidateColumns(definition.Columns);
        if (hasMultiValues)
        {
            var rows = definition.MultiValues!
                .Select((row, index) =>
                {
                    if (row.Count != declaredColumns.Length)
                    {
                        throw new InvalidOperationException(
                            $"INSERT row {index + 1} has {row.Count} values but {declaredColumns.Length} columns were declared.");
                    }

                    return row.Select(value => (SqlExpr)new LiteralExpr(value, Unknown)).ToImmutableArray();
                })
                .ToImmutableArray();
            return new InsertStatement(
                target,
                declaredColumns,
                new InsertValuesSource(rows, Unknown),
                Unknown);
        }

        return new InsertStatement(
            target,
            declaredColumns,
            new InsertQuerySource(QueryDefinitionCoreMapper.Map(definition.FromQuery!), Unknown),
            Unknown);
    }

    private static ImmutableArray<SqlIdentifier> ValidateColumns(IEnumerable<string>? columns)
    {
        if (columns is null)
            throw new InvalidOperationException("INSERT requires an explicit ordered column list.");

        var values = columns.ToArray();
        if (values.Length == 0)
            throw new InvalidOperationException("INSERT requires at least one target column.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<SqlIdentifier>(values.Length);
        foreach (var column in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(column);
            var identifier = Identifier(column);
            if (identifier.Parts.Length != 1)
                throw new InvalidOperationException($"INSERT target column '{column}' must be unqualified.");
            if (!seen.Add(column))
                throw new InvalidOperationException($"INSERT target column '{column}' is declared more than once.");
            result.Add(identifier);
        }
        return result.ToImmutable();
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
