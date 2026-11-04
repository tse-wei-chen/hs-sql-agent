using System.Collections.Immutable;
using System.Globalization;
using HsSqlAgent.SqlCore.Core.Ast;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Shared Host-only structural gate for one-row SQL Server MERGE matched
/// DELETE and direct-column INT UPDATE. Native physical uniqueness, schema
/// attestation and transaction-held row evidence are still required.
/// </summary>
internal static class SqlServerMergeMatchedDeleteSafety
{
    internal sealed record UpdatedInt(string Column, int Value);

    internal sealed record Shape(
        string Schema, string Table, string KeyColumn, int KeyValue,
        ImmutableArray<UpdatedInt> UpdatedColumns,
        ImmutableArray<UpdatedInt> InsertedColumns)
    {
        internal string PhysicalTable => Schema + "." + Table;
        internal bool IsUpdate => !UpdatedColumns.IsDefaultOrEmpty;
        internal bool IsInsert => !InsertedColumns.IsDefaultOrEmpty;
    }

    internal static bool IsBoundedShape(MergeStatement merge)
    {
        try { RequireShape(merge, SqlAgentToolType.MsSqlServer, SqlAgentToolType.MsSqlServer, 1); return true; }
        catch (NotSupportedException) { return false; }
    }

    internal static Shape RequireShape(
        MergeStatement merge, SqlAgentToolType source, SqlAgentToolType target, int maxAffectedRows)
    {
        if (source != SqlAgentToolType.MsSqlServer
            || target != SqlAgentToolType.MsSqlServer
            || maxAffectedRows != 1
            || merge.Target.Name.Parts.Length != 2
            || !string.Equals(merge.Target.Name.Parts[0].Value, "dbo", StringComparison.Ordinal)
            || merge.Target.Alias is not { Value: "t" }
            || merge.SourceAlias.Value != "s"
            || merge.SourceColumns.Length is < 1 or > 4
            || merge.SourceValues.Length != merge.SourceColumns.Length
            || ((merge.Matched is null) == (merge.NotMatched is null)))
            throw new NotSupportedException(
                "SQL Server MERGE Host admits one dbo target and one bounded source VALUES row, with exactly one direct matched DELETE, UPDATE or not-matched INSERT action.");

        var key = RequireDirectField(merge.SourceColumns[0]);
        var sourceValues = new int[merge.SourceValues.Length];
        var sourceNames = new string[merge.SourceColumns.Length];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < merge.SourceValues.Length; i++)
        {
            var name = RequireDirectField(merge.SourceColumns[i]);
            if (!names.Add(name))
                throw new NotSupportedException(
                    "MERGE source columns must be distinct physical identifiers.");
            var number = ExactInt(merge.SourceValues[i]);
            if (!number.HasValue)
                throw new NotSupportedException(
                    "MERGE source values require exact native INT literals without implicit casts or expressions.");
            sourceValues[i] = number.Value;
            sourceNames[i] = name;
        }

        if (merge.MatchPredicate is not BinaryExpr { Operator: "=" } match
            || !((IsExactKey(match.Left, "t", key) && IsExactKey(match.Right, "s", key))
              || (IsExactKey(match.Right, "t", key) && IsExactKey(match.Left, "s", key))))
            throw new NotSupportedException(
                "MERGE requires precisely t.<key> = s.<key> without extra predicates.");

        // A NOT MATCHED INSERT must map every source field exactly once to
        // the same-named physical target, without expressions, defaults,
        // duplicate columns, computed values or an additional MATCHED branch.
        if (merge.Matched is null)
        {
            var insert = merge.NotMatched!;
            if (sourceNames.Length is < 2 or > 4
                || insert.Columns.Length != sourceNames.Length
                || insert.Values.Length != sourceNames.Length)
                throw new NotSupportedException(
                    "SQL Server MERGE INSERT requires one INT primary key plus 1–3 complete direct INT payload fields.");
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < insert.Columns.Length; index++)
            {
                var field = RequireDirectField(insert.Columns[index]);
                if (!assigned.Add(field)
                    || !sourceNames.Contains(field, StringComparer.Ordinal)
                    || !IsExactKey(insert.Values[index], "s", field))
                    throw new NotSupportedException(
                        "SQL Server MERGE INSERT requires each physical column exactly once from the same-named source field.");
            }
            if (assigned.Count != sourceNames.Length)
                throw new NotSupportedException(
                    "SQL Server MERGE INSERT cannot omit or duplicate source fields.");
            return new Shape("dbo", merge.Target.Name.Parts[1].Value, key,
                sourceValues[0], ImmutableArray<UpdatedInt>.Empty,
                sourceNames.Skip(1).Select((name, index) =>
                    new UpdatedInt(name, sourceValues[index + 1])).ToImmutableArray());
        }

        var updated = ImmutableArray.CreateBuilder<UpdatedInt>();
        switch (merge.Matched.Kind)
        {
            case MergeMatchedActionKind.Delete
                when merge.SourceColumns.Length == 1 && merge.Matched.Assignments.IsDefaultOrEmpty:
                break;
            case MergeMatchedActionKind.Update
                when merge.SourceColumns.Length is >= 2 and <= 4
                  && merge.Matched.Assignments.Length == merge.SourceColumns.Length - 1:
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var assignment in merge.Matched.Assignments)
                {
                    var column = RequireDirectField(assignment.Column);
                    if (string.Equals(column, key, StringComparison.Ordinal)
                        || !seen.Add(column))
                        throw new NotSupportedException(
                            "MERGE cannot assign the physical key or repeat an update column.");
                    var index = Array.FindIndex(sourceNames, 1, name =>
                        string.Equals(name, column, StringComparison.Ordinal));
                    if (index < 1 || !IsExactKey(assignment.Value, "s", column))
                        throw new NotSupportedException(
                            "MERGE UPDATE accepts only one direct s.<column> value per physically proven target column.");
                    updated.Add(new UpdatedInt(column, sourceValues[index]));
                }
                if (seen.Count != merge.SourceColumns.Length - 1)
                    throw new NotSupportedException(
                        "MERGE UPDATE requires every source payload column to be assigned exactly once.");
                break;
            }
            default:
                throw new NotSupportedException(
                    "MERGE matched approval permits DELETE or bounded direct-column UPDATE only, never INSERT or mixed actions.");
        }
        return new Shape("dbo", merge.Target.Name.Parts[1].Value, key,
            sourceValues[0], updated.ToImmutable(), ImmutableArray<UpdatedInt>.Empty);
    }

    internal static void RequireExactSourceLiterals(
        MergeStatement merge, Shape shape, string? rawSql)
    {
        if (string.IsNullOrWhiteSpace(rawSql))
            throw new NotSupportedException("MERGE requires the original verified SQL source text.");

        var allowed = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [shape.KeyColumn] = shape.KeyValue
        };
        foreach (var cell in shape.UpdatedColumns) allowed.Add(cell.Column, cell.Value);
        foreach (var cell in shape.InsertedColumns) allowed.Add(cell.Column, cell.Value);
        for (var i = 0; i < merge.SourceValues.Length; i++)
        {
            var column = RequireDirectField(merge.SourceColumns[i]);
            var source = merge.SourceValues[i].Span;
            if (!allowed.TryGetValue(column, out var expected)
                || source.Start < 0 || source.End <= source.Start || source.End > rawSql.Length)
                throw new NotSupportedException(
                    "MERGE literal source span or column identity is not proven.");
            var token = rawSql.AsSpan(source.Start, source.End - source.Start).Trim();
            if (!int.TryParse(token, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var actual)
                || actual != expected)
                throw new NotSupportedException(
                    "MERGE values must use plain exact INT source tokens; no fractional, scientific or implicit coercion.");
        }
    }

    private static string RequireDirectField(SqlIdentifier identifier)
    {
        if (identifier.Parts.Length != 1
            || string.IsNullOrWhiteSpace(identifier.Parts[0].Value)
            || identifier.Parts[0].Value.StartsWith("__", StringComparison.Ordinal))
            throw new NotSupportedException(
                "MERGE requires a single direct physical identifier without a reserved preview prefix.");
        return identifier.Parts[0].Value;
    }

    private static int? ExactInt(SqlExpr expression) => expression switch
    {
        LiteralExpr { Value: int number } => number,
        LiteralExpr { Value: long number } when number is >= int.MinValue and <= int.MaxValue => (int)number,
        LiteralExpr { Value: decimal number } when number == decimal.Truncate(number)
            && number is >= int.MinValue and <= int.MaxValue => (int)number,
        _ => null
    };

    private static bool IsExactKey(SqlExpr expression, string alias, string key) =>
        expression is ColumnExpr { Name.Parts.Length: 2 } column
        && string.Equals(column.Name.Parts[0].Value, alias, StringComparison.Ordinal)
        && string.Equals(column.Name.Parts[1].Value, key, StringComparison.Ordinal);
}
