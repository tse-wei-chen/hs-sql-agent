using System.Collections.Immutable;
using System.Globalization;
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
    internal enum ProposedKind { Text, ExactInteger, SqlNull }

    internal sealed record ProposedValue(string Column, ProposedKind Kind, long Integer = 0);

    internal sealed record Shape(
        string UniqueKeyColumn,
        long UniqueKeyValue,
        ImmutableArray<string> UpdatedColumns = default,
        ImmutableArray<ProposedValue> ProposedValues = default);

    // The F# -> CLR AST sometimes expresses a signed integer as UnaryExpr.
    // Admit unary +/- ONLY around a single exact integral literal. Never
    // evaluate arithmetic, floating point, implicit conversions or functions.
    internal static long? ExactIntegerLiteral(SqlExpr expression)
    {
        static decimal? DecimalLiteral(SqlExpr value) => value switch
        {
            LiteralExpr { Value: int n } => n,
            LiteralExpr { Value: long n } => n,
            LiteralExpr { Value: decimal n } => n,
            _ => null
        };
        decimal? candidate = expression switch
        {
            UnaryExpr { Operator: "-", Operand: var operand }
                when DecimalLiteral(operand) is decimal magnitude => -magnitude,
            UnaryExpr { Operator: "+", Operand: var operand }
                when DecimalLiteral(operand) is decimal magnitude => magnitude,
            _ => DecimalLiteral(expression)
        };
        return candidate is decimal n
            && n == decimal.Truncate(n)
            && n >= long.MinValue && n <= long.MaxValue
            ? (long)n : null;
    }

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
            || (target == SqlAgentToolType.Sqlite && !insert.Returning.IsDefaultOrEmpty)
            || insert.Target.Alias is not null)
            throw new NotSupportedException(
                "Native conflict approval requires one explicit UNIQUE key and one VALUES row; SQLite DO NOTHING forbids RETURNING, while PostgreSQL permits only independently proven direct-column result projections.");

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
        {
            // Conflict-skip attests one physical key, not other INSERT fields,
            // so the only safe returned projection is that exact key. A skip
            // returns zero actual rows; no preview data is disclosed instead.
            if (!insert.Returning.IsDefaultOrEmpty
                && (target != SqlAgentToolType.Postgres
                    || insert.Returning.Length != 1
                    || insert.Returning[0] is not DmlReturningColumnItem
                        { Identifier.Parts.Length: 1 } returnedKey
                    || !string.Equals(returnedKey.Identifier.Parts[0].Value,
                        key, StringComparison.Ordinal)))
                throw new NotSupportedException(
                    "Native PostgreSQL DO NOTHING RETURNING permits only its single, exact, unqualified physical UNIQUE key.");
            return new Shape(key, exactInteger.Value);
        }

        // The entire proposed row must be visible and every value column must
        // receive its identically named EXCLUDED value. No partial updates,
        // duplicate SET targets or implicit source values can be approved.
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        // Fingerprint field lookup folds case, unlike PostgreSQL quoted names.
        var fingerprintNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        var updated = ImmutableArray.CreateBuilder<string>(conflict.Assignments.Length);
        var proposedValues = ImmutableArray.CreateBuilder<ProposedValue>(conflict.Assignments.Length);
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
                    "PostgreSQL conflict UPDATE rejects case-folded row-fingerprint column ambiguity.");
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
                    "Native PostgreSQL conflict update requires an explicit literal value for each updated column.");
            var expression = values.Rows[0][ordinal];
            var kind = expression switch
            {
                LiteralExpr { Value: string } => ProposedKind.Text,
                LiteralExpr { Value: null } => ProposedKind.SqlNull,
                _ when ExactIntegerLiteral(expression) is not null => ProposedKind.ExactInteger,
                _ => throw new NotSupportedException(
                    "Native PostgreSQL conflict update requires exact TEXT, signed INT64 or SQL NULL literals; no casts or expressions.")
            };
            updated.Add(name);
            proposedValues.Add(new ProposedValue(name, kind,
                kind == ProposedKind.ExactInteger ? ExactIntegerLiteral(expression)!.Value : 0));
        }
        if (seen.Count != insert.Columns.Length
            || insert.Columns.Any(col => !seen.Contains(col.Parts[0].Value)))
            throw new NotSupportedException(
                "Native PostgreSQL conflict update must update all and only explicitly inserted non-key columns.");

        // RETURNING is only the post-commit projection of columns already
        // covered by the sealed physical before/after plan. Never permit an
        // expression, wildcard, system field, alias or ambiguous identifier
        // to expand the native approval's row or effect scope.
        if (!insert.Returning.IsDefaultOrEmpty)
        {
            if (insert.Returning.Length > insert.Columns.Length
                || insert.Returning.Length > 5)
                throw new NotSupportedException(
                    "Native PostgreSQL conflict UPDATE RETURNING is bounded to at most five approved physical fields.");
            var returningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in insert.Returning)
            {
                if (item is not DmlReturningColumnItem { Identifier.Parts.Length: 1 } direct
                    || !seen.Contains(direct.Identifier.Parts[0].Value)
                    || !returningNames.Add(direct.Identifier.Parts[0].Value))
                    throw new NotSupportedException(
                        "Native PostgreSQL conflict UPDATE RETURNING requires distinct unqualified direct approved columns.");
            }
        }

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
            updated.MoveToImmutable(), proposedValues.MoveToImmutable());
    }

    /// <summary>
    /// F# can normalize exponent/decimal tokens such as 1e10 and 42.0 into
    /// integral numeric values. The Host requires their original source token
    /// to be a plain decimal integer, verified against the closed AST value.
    /// </summary>
    internal static void RequireExactIntegerSourceTokens(
        InsertStatement insert, Shape shape, string? rawSql)
    {
        if (shape.ProposedValues.IsDefaultOrEmpty
            || !shape.ProposedValues.Any(value => value.Kind == ProposedKind.ExactInteger))
            return;
        if (string.IsNullOrEmpty(rawSql)
            || insert.Source is not InsertValuesSource { Rows.Length: 1 } values)
            throw new NotSupportedException(
                "PostgreSQL typed UPDATE requires original SQL for integer-source proof.");

        foreach (var proof in shape.ProposedValues)
        {
            if (proof.Kind != ProposedKind.ExactInteger)
                continue;
            var ordinal = -1;
            for (var i = 0; i < insert.Columns.Length; i++)
                if (insert.Columns[i].Parts.Length == 1
                    && string.Equals(insert.Columns[i].Parts[0].Value,
                        proof.Column, StringComparison.Ordinal))
                {
                    if (ordinal >= 0)
                        throw new NotSupportedException("Duplicate integer-source column.");
                    ordinal = i;
                }
            if (ordinal < 0)
                throw new NotSupportedException("Missing integer-source column.");
            var expression = values.Rows[0][ordinal];
            var span = expression.Span;
            if (span.Start < 0 || span.End <= span.Start || span.End > rawSql.Length)
                throw new NotSupportedException(
                    "PostgreSQL typed UPDATE needs valid F# integer source spans.");
            var source = rawSql.AsSpan(span.Start, span.End - span.Start).Trim();
            if (!long.TryParse(source, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out var sourceValue)
                || sourceValue != proof.Integer
                || ExactIntegerLiteral(expression) != sourceValue)
                throw new NotSupportedException(
                    "PostgreSQL typed UPDATE requires a plain decimal integer token " +
                    "equal to its F# AST value; exponents and fractional spellings are denied.");
        }
    }
}
