using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Core.Ast;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Bounded structural admission, shared between native SQLite and PostgreSQL.
/// A real provider-minted, transaction-revalidated physical UNIQUE key proof is
/// still required. Never authorizes direct SQL execution or cross-provider reuse.
/// </summary>
internal static class NativeSingleRowConflictSafety
{
    internal sealed record Shape(string UniqueKeyColumn, long UniqueKeyValue, string? UpdatedColumn = null);

    internal static bool IsBoundedShape(InsertStatement insert) =>
        IsBoundedShape(insert, SqlAgentToolType.Sqlite)
        || IsBoundedShape(insert, SqlAgentToolType.Postgres)
        || IsBoundedUpdateShape(insert);

    internal static bool IsBoundedUpdateShape(InsertStatement insert)
    {
        try { RequireShape(insert, SqlAgentToolType.Postgres, SqlAgentToolType.Postgres, 1, update: true); return true; }
        catch (NotSupportedException) { return false; }
    }

    private static bool IsBoundedShape(InsertStatement insert, SqlAgentToolType dialect)
    {
        try { RequireShape(insert, dialect, dialect, 1); return true; }
        catch (NotSupportedException) { return false; }
    }

    internal static Shape RequireShape(
        InsertStatement insert,
        SqlAgentToolType source,
        SqlAgentToolType target,
        int maxAffectedRows,
        bool update = false)
    {
        if (source != target || target is not (SqlAgentToolType.Sqlite or SqlAgentToolType.Postgres))
            throw new NotSupportedException(
                "Native conflict skip requires identical SQLite or PostgreSQL source and target.");
        if (maxAffectedRows != 1)
            throw new NotSupportedException(
                "Native conflict skip requires a strict one-row affected-row cap.");
        if (insert.Conflict is not { } conflict
            || (update
                ? target != SqlAgentToolType.Postgres
                    || conflict.Action != InsertConflictActionKind.UpdateProposedValues
                    || conflict.Assignments.Length != 1
                    || insert.Columns.Length != 2
                : conflict.Action != InsertConflictActionKind.DoNothing
                    || !conflict.Assignments.IsDefaultOrEmpty)
            || conflict.TargetColumns.Length != 1
            || insert.Source is not InsertValuesSource values
            || values.Rows.Length != 1
            || !insert.Returning.IsDefaultOrEmpty
            || insert.Target.Alias is not null)
            throw new NotSupportedException(
                "Native conflict skip permits one explicit UNIQUE key target, one VALUES row and DO NOTHING without RETURNING.");

        var targetIdentifier = conflict.TargetColumns[0];
        if (targetIdentifier.Parts.Length != 1)
            throw new NotSupportedException(
                "Native conflict target must be an unqualified UNIQUE key column.");
        var key = targetIdentifier.Parts[0].Value;
        var comparison = target == SqlAgentToolType.Postgres
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = insert.Columns.Select((col, index) => (col, index))
            .Where(item => item.col.Parts.Length == 1
                && string.Equals(item.col.Parts[0].Value, key, comparison))
            .ToArray();
        if (matches.Length != 1 || insert.Columns.Any(col => col.Parts.Length != 1)
            || values.Rows[0].Length != insert.Columns.Length)
            throw new NotSupportedException(
                "Native conflict key must occur exactly once in a complete explicit INSERT column list.");

        // The closed F# -> CLR adapter exposes exact small integers as Int32,
        // and larger integers as Decimal. Never coerce floats, strings or NULL.
        var exactInteger = values.Rows[0][matches[0].index] switch
        {
            LiteralExpr { Value: int number } => (long?)number,
            LiteralExpr { Value: long number } => number,
            LiteralExpr { Value: decimal number }
                when number == decimal.Truncate(number)
                  && number >= long.MinValue && number <= long.MaxValue =>
                (long)number,
            _ => null
        };
        if (exactInteger is null)
            throw new NotSupportedException(
                "Native conflict skip requires a non-null exact INT64 UNIQUE key literal.");
        if (!update)
            return new Shape(key, exactInteger.Value);

        var assignment = conflict.Assignments[0];
        if (assignment.Column.Parts.Length != 1
            || assignment.ProposedColumn is not { Parts.Length: 2 } proposed
            || assignment.Value is not null
            || !string.Equals(proposed.Parts[0].Value, "excluded", StringComparison.Ordinal)
            || !string.Equals(assignment.Column.Parts[0].Value, proposed.Parts[1].Value,
                StringComparison.Ordinal))
            throw new NotSupportedException(
                "Native PostgreSQL conflict update requires a single SET column = EXCLUDED.column assignment.");
        var updated = assignment.Column.Parts[0].Value;
        if (string.Equals(updated, key, StringComparison.Ordinal)
            || insert.Columns.Count(col => col.Parts.Length == 1
                && string.Equals(col.Parts[0].Value, updated, StringComparison.Ordinal)) != 1
            || values.Rows[0][Array.FindIndex(insert.Columns.ToArray(), col =>
                col.Parts.Length == 1 && string.Equals(col.Parts[0].Value, updated,
                    StringComparison.Ordinal))] is not LiteralExpr { Value: string })
            throw new NotSupportedException(
                "Native PostgreSQL conflict update requires a distinct physical TEXT column and one literal proposed value.");
        return new Shape(key, exactInteger.Value, updated);
    }
}
