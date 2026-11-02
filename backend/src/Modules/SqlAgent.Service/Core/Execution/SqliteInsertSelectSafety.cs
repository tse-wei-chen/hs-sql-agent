using System.Collections.Immutable;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Ast;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// One bounded M09 source shape. SQLite's write transaction serializes source
/// changes between revalidation and mutation. The native Host separately
/// attests both physical source and physical insert target when different.
/// Neither caller-provided virtual metadata nor arbitrary INSERT SELECT joins
/// can promote themselves to this proof.
/// </summary>
internal static class SqliteInsertSelectSafety
{
    internal static (SqlStatement Query, ImmutableArray<string> OutputColumns, string PhysicalSource) RequireShape(
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
        if (insert.Conflict is not null || insert.Target.Alias is not null)
            throw new NotSupportedException(
                "SQLite INSERT SELECT source-row approval rejects conflict and target aliases.");

        // RETURNING produces rows only after the already approved mutation.
        // F# owns native dialect/version/type validation; Host deliberately
        // admits only a bounded list of distinct direct target columns.
        // Generated target values are not pretended to be previewed source rows.
        if (!insert.Returning.IsDefaultOrEmpty)
        {
            var seenReturning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in insert.Returning)
            {
                if (item is not DmlReturningColumnItem column
                    || column.Identifier.Parts.Length != 1
                    || column.Identifier.Parts[0].Value is "*" or ""
                    || !seenReturning.Add(column.Identifier.Parts[0].Value))
                    throw new NotSupportedException(
                        "SQLite INSERT SELECT RETURNING requires distinct unqualified direct target columns; wildcard, aliases and expressions remain fail-closed.");
            }
            if (seenReturning.Count > 32)
                throw new NotSupportedException(
                    "SQLite INSERT SELECT RETURNING is limited to 32 direct target columns.");
        }
        if (insert.Target.Name.Parts.Length != 2
            || !IsMain(insert.Target.Name.Parts[0].Value))
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires a schema-qualified main table target.");
        if (insert.Source is not InsertQuerySource sourceInput)
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires a direct bounded SELECT source.");

        // The compatibility adapter represents ordinary single-SELECT
        // statements as SelectStatement, not QueryStatement.
        var query = sourceInput.Query;
        SelectStatement head;
        if (query is SelectStatement simple)
            head = simple;
        else if (query is QueryStatement wrapped
            && wrapped.SetOperations.IsDefaultOrEmpty
            && wrapped.OrderBy.IsDefaultOrEmpty && !wrapped.Limit.HasValue
            && !wrapped.Offset.HasValue && !wrapped.FetchPercent.HasValue
            && !wrapped.FetchWithTies)
            head = wrapped.Head;
        else
            throw new NotSupportedException(
                "SQLite INSERT SELECT source approval rejects set operations and query-level modifiers.");

        if (!head.Ctes.IsDefaultOrEmpty || head.Distinct
            || !head.DistinctOn.IsDefaultOrEmpty || head.FetchPercent.HasValue
            || head.FetchWithTies || head.Limit.HasValue || head.Offset.HasValue
            || !head.Joins.IsDefaultOrEmpty || !head.GroupBy.IsDefaultOrEmpty
            || head.Having is not null || !head.OrderBy.IsDefaultOrEmpty)
            throw new NotSupportedException(
                "SQLite INSERT SELECT source approval rejects CTEs, JOINs, set operations, grouping, paging and ordering.");

        if (head.From is not NamedTableSource source || source.Alias is not null
            || source.Name.Parts.Length != 2
            || !IsMain(source.Name.Parts[0].Value)
            || string.IsNullOrWhiteSpace(source.Name.Parts[1].Value)
            || source.Name.Parts[1].Value.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "SQLite INSERT SELECT requires one explicit physical main source table.");

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
        return (query, columns.ToImmutable(), "main." + source.Name.Parts[1].Value);
    }

    internal static bool IsBoundedShape(InsertStatement insert)
    {
        try
        {
            RequireShape(insert, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite, 1);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static SqlStatement PreviewQuery(SqlStatement source, ImmutableArray<string> columns)
    {
        var head = source is SelectStatement simple
            ? simple
            : ((QueryStatement)source).Head;
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

        return preview;
    }

    private static bool IsMain(string value) =>
        string.Equals(value, "main", StringComparison.OrdinalIgnoreCase);
}
