using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// One bounded M09 source shape. SQLite's write transaction serializes source
/// changes between revalidation and mutation; the existing single-table schema
/// attestation covers both the read source and physical insert target.
/// Neither caller-provided virtual metadata nor arbitrary INSERT SELECT joins
/// can promote themselves to this proof.
/// </summary>
internal static class SqliteInsertSelectSafety
{
    internal static (QueryStatement Query, ImmutableArray<string> OutputColumns) RequireShape(
        InsertStatement insert,
        SqlAgentToolType sourceDialect,
        SqlAgentToolType targetProvider,
        int maxAffectedRows)
    {
        if (sourceDialect != SqlAgentToolType.Sqlite
            || targetProvider != SqlAgentToolType.Sqlite)
            throw new NotSupportedException(
                "INSERT SELECT source-row approval is proven only for native SQLite.");
        if (maxAffectedRows <= 0 || maxAffectedRows == int.MaxValue)
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires a positive finite maximum affected-row policy.");
        if (insert.Conflict is not null || !insert.Returning.IsDefaultOrEmpty
            || insert.Target.Alias is not null)
            throw new NotSupportedException(
                "SQLite INSERT SELECT source-row approval rejects conflict, RETURNING and target aliases.");
        if (insert.Target.Name.Parts.Length != 2
            || !IsMain(insert.Target.Name.Parts[0].Value))
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires a schema-qualified main table target.");
        if (insert.Source is not InsertQuerySource { Query: QueryStatement query })
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires a direct bounded SELECT source.");

        var head = query.Head;
        if (query.SetOperations.IsDefaultOrEmpty is false
            || !query.OrderBy.IsDefaultOrEmpty || query.Limit.HasValue
            || query.Offset.HasValue || query.FetchPercent.HasValue || query.FetchWithTies
            || !head.Ctes.IsDefaultOrEmpty || head.Distinct
            || !head.DistinctOn.IsDefaultOrEmpty || head.FetchPercent.HasValue
            || head.FetchWithTies || head.Limit.HasValue || head.Offset.HasValue
            || !head.Joins.IsDefaultOrEmpty || !head.GroupBy.IsDefaultOrEmpty
            || head.Having is not null || !head.OrderBy.IsDefaultOrEmpty)
            throw new NotSupportedException(
                "SQLite INSERT SELECT source approval rejects CTEs, JOINs, set operations, grouping, paging and ordering.");

        if (head.From is not NamedTableSource source || source.Alias is not null
            || source.Name.Parts.Length != 2
            || !IsMain(source.Name.Parts[0].Value)
            || !string.Equals(source.Name.Parts[1].Value,
                insert.Target.Name.Parts[1].Value, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires exactly the same main physical table as source and target.");

        if (head.Where is not BinaryExpr { Operator: "=", Left: ColumnExpr whereColumn, Right: LiteralExpr predicateValue }
            || predicateValue.Value is null || whereColumn.Name.Parts.Length != 1)
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires a deterministic direct-column = non-null literal predicate.");

        if (insert.Columns.IsDefaultOrEmpty || insert.Columns.Length != head.Select.Length)
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires one explicit target column per direct source projection.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = ImmutableArray.CreateBuilder<string>(insert.Columns.Length);
        for (var index = 0; index < insert.Columns.Length; index++)
        {
            var target = insert.Columns[index];
            var projection = head.Select[index];
            if (target.Parts.Length != 1 || !seen.Add(target.Parts[0].Value)
                || projection.Expression is not ColumnExpr { Name.Parts.Length: 1 }
                || projection.Alias is not null)
                throw new NotSupportedException(
                    "SQLite INSERT SELECT supports only unique target columns and direct unaliased column projections.");
            columns.Add(target.Parts[0].Value);
        }
        return (query, columns.ToImmutable());
    }

    internal static QueryStatement PreviewQuery(QueryStatement source, ImmutableArray<string> columns)
    {
        var head = source.Head;
        var select = ImmutableArray.CreateBuilder<SelectItem>(columns.Length);
        for (var index = 0; index < columns.Length; index++)
        {
            select.Add(new SelectItem(
                head.Select[index].Expression,
                new IdentifierPart(columns[index], true, SourceSpan.Unknown),
                SourceSpan.Unknown));
        }

        var preview = new SelectStatement(
            ImmutableArray<CteDefinition>.Empty,
            false,
            select.ToImmutable(),
            head.From,
            ImmutableArray<JoinSource>.Empty,
            head.Where,
            ImmutableArray<SqlExpr>.Empty,
            null,
            ImmutableArray<OrderByItem>.Empty,
            null,
            null,
            SourceSpan.Unknown);

        return new QueryStatement(
            preview,
            ImmutableArray<SetOperation>.Empty,
            ImmutableArray<OrderByItem>.Empty,
            null,
            null,
            SourceSpan.Unknown);
    }

    private static bool IsMain(string value) =>
        string.Equals(value, "main", StringComparison.OrdinalIgnoreCase);
}
