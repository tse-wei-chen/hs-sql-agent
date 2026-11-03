using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Core.Ast;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Closed input-shape admission only. The plan factory must additionally
/// attest the sole enforced physical UNIQUE key and the native schema on a real DB.
/// No raw SQL reconstruction, fake key metadata, update action or batch.
/// </summary>
internal static class NativeSqliteConflictDoNothingSafety
{
    internal sealed record Shape(string UniqueKeyColumn, long UniqueKeyValue);

    internal static bool IsBoundedShape(InsertStatement insert)
    {
        try { RequireShape(insert, SqlAgentToolType.Sqlite, SqlAgentToolType.Sqlite, 1); return true; }
        catch (NotSupportedException) { return false; }
    }

    internal static Shape RequireShape(
        InsertStatement insert,
        SqlAgentToolType source,
        SqlAgentToolType target,
        int maxAffectedRows)
    {
        if (source != SqlAgentToolType.Sqlite || target != SqlAgentToolType.Sqlite)
            throw new NotSupportedException("Native conflict skip requires identical SQLite source and target.");
        if (maxAffectedRows != 1)
            throw new NotSupportedException("Native SQLite conflict skip requires a strict one-row affected-row cap.");
        if (insert.Conflict is not { Action: InsertConflictActionKind.DoNothing } conflict
            || conflict.TargetColumns.Length != 1
            || !conflict.Assignments.IsDefaultOrEmpty
            || insert.Source is not InsertValuesSource values
            || values.Rows.Length != 1
            || !insert.Returning.IsDefaultOrEmpty
            || insert.Target.Alias is not null)
            throw new NotSupportedException(
                "Native SQLite conflict skip permits one explicit UNIQUE key target, one VALUES row and DO NOTHING without RETURNING.");
        var targetIdentifier = conflict.TargetColumns[0];
        if (targetIdentifier.Parts.Length != 1)
            throw new NotSupportedException("Native SQLite conflict target must be an unqualified UNIQUE key column.");
        var key = targetIdentifier.Parts[0].Value;
        var matches = insert.Columns.Select((col, index) => (col, index))
            .Where(item => item.col.Parts.Length == 1
                && string.Equals(item.col.Parts[0].Value, key, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || insert.Columns.Any(col => col.Parts.Length != 1)
            || values.Rows[0].Length != insert.Columns.Length)
            throw new NotSupportedException(
                "Native SQLite conflict key must occur exactly once in a complete explicit INSERT column list.");
        // The canonical F#→CLR compatibility adapter projects small integer
        // literals as Int32 and larger exact integers as Decimal. Do not
        // narrow via floating point, parse strings or admit SQL NULL.
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
                "Native SQLite conflict skip currently admits a non-null exact INT64 UNIQUE key literal only.");
        return new Shape(key, exactInteger.Value);
    }
}
