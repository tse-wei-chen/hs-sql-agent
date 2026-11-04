using System.Collections.Immutable;
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
    internal sealed record Shape(
        string UniqueKeyColumn,
        long UniqueKeyValue,
        ImmutableArray<string> UpdatedColumns = default,
        ImmutableArray<string> BigintColumns = default);

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
                    || conflict.Assignments.Length is < 1 or > 4
                    || insert.Columns.Length != conflict.Assignments.Length + 1
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

        // The entire proposed row must be visible and every value column must
        // receive its identically named EXCLUDED value. No partial updates,
        // duplicate SET targets or implicit source values can be approved.
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        // The shared row-set fingerprint reader currently resolves column
        // keys without case sensitivity. Reject distinct PostgreSQL quoted
        // columns which it could otherwise confuse during revalidation.
        var fingerprintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        var updated = ImmutableArray.CreateBuilder<string>(conflict.Assignments.Length);
        var bigint = ImmutableArray.CreateBuilder<string>();
        foreach (var assignment in conflict.Assignments)
        {
            if (assignment.Column.Parts.Length != 1
                || assignment.ProposedColumn is not { Parts.Length: 1 } proposed
                || assignment.Value is not null)
                throw new NotSupportedException(
                    "Native PostgreSQL conflict update requires only SET column = EXCLUDED.column assignments.");
            var name = assignment.Column.Parts[0].Value;
            if (!string.Equals(name, proposed.Parts[0].Value, StringComparison.Ordinal)
                || !seen.Add(name)
                || !fingerprintNames.Add(name))
                throw new NotSupportedException(
                    "Native PostgreSQL conflict update requires distinct, exact-case matching EXCLUDED columns without case-folded row-fingerprint ambiguity.");
            var ordinal = -1;
            for (var index = 0; index < insert.Columns.Length; index++)
                if (insert.Columns[index].Parts.Length == 1
                    && string.Equals(insert.Columns[index].Parts[0].Value, name, StringComparison.Ordinal))
                {
                    if (ordinal >= 0)
                        throw new NotSupportedException("Duplicate PostgreSQL INSERT column cannot be approved.");
                    ordinal = index;
                }
            if (ordinal < 0)
                throw new NotSupportedException(
                    "Native PostgreSQL conflict update requires every updated column in the single VALUES row.");
            var value = values.Rows[0][ordinal];
            if (value is LiteralExpr { Value: string })
            {
                // Physical TEXT type is attested by the real PostgreSQL Catalog.
            }
            else if (IsExactInt64Literal(value))
            {
                bigint.Add(name);
            }
            else
                throw new NotSupportedException(
                    "Native PostgreSQL conflict update accepts only non-NULL TEXT or exact INT64 literals.");
            updated.Add(name);
        }
        if (seen.Count != insert.Columns.Length
            || insert.Columns.Any(col => !seen.Contains(col.Parts[0].Value)))
            throw new NotSupportedException(
                "Native PostgreSQL conflict update must update all and only explicitly inserted non-key columns.");

        // User-chosen physical identifiers must not override synthetic
        // approval display labels (notably __action) or other before/after
        // fields. An ambiguous preview must never mint an execution approval.
        var displayFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "__action" };
        if (!displayFields.Add(key))
            throw new NotSupportedException(
                "PostgreSQL conflict key collides with an approval preview field.");
        foreach (var name in updated)
            if (!displayFields.Add("__before_" + name)
                || !displayFields.Add("__after_" + name))
                throw new NotSupportedException(
                    "PostgreSQL physical identifiers collide with approval before/after preview fields.");
        return new Shape(key, exactInteger.Value,
            updated.MoveToImmutable(), bigint.ToImmutable());
    }

    private static bool IsExactInt64Literal(SqlExpr expression) =>
        expression switch
        {
            LiteralExpr { Value: int } => true,
            LiteralExpr { Value: long } => true,
            LiteralExpr { Value: decimal number }
                when number == decimal.Truncate(number)
                  && (decimal.GetBits(number)[3] & 0x00FF0000) == 0
                  && number >= long.MinValue && number <= long.MaxValue => true,
            _ => false
        };
}
