using HsSqlAgent.SqlCore.Core.Ast;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Host-only structural admission for a single exact INT target key and a
/// one-row, matched-DELETE-only SQL Server MERGE. This is not a physical
/// UNIQUE-key proof: native catalog attestation and a pinned transaction are
/// mandatory before any DML is approved or executed.
/// </summary>
internal static class SqlServerMergeMatchedDeleteSafety
{
    internal sealed record Shape(string Schema, string Table, string KeyColumn, int KeyValue)
    {
        internal string PhysicalTable => Schema + "." + Table;
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
            || merge.SourceColumns.Length != 1
            || merge.SourceColumns[0].Parts.Length != 1
            || merge.SourceValues.Length != 1
            || merge.Matched is not { Kind: MergeMatchedActionKind.Delete }
            || !merge.Matched.Assignments.IsDefaultOrEmpty
            || merge.NotMatched is not null)
            throw new NotSupportedException(
                "SQL Server MERGE Host approval permits only dbo.<table> AS t, one s(id) source key and WHEN MATCHED THEN DELETE.");

        var key = merge.SourceColumns[0].Parts[0].Value;
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("__", StringComparison.Ordinal))
            throw new NotSupportedException("SQL Server MERGE requires a direct, unambiguous physical key.");
        var value = merge.SourceValues[0] switch
        {
            LiteralExpr { Value: int number } => (int?)number,
            LiteralExpr { Value: long number } when number is >= int.MinValue and <= int.MaxValue => (int)number,
            LiteralExpr { Value: decimal number }
                when number == decimal.Truncate(number)
                    && number is >= int.MinValue and <= int.MaxValue => (int)number,
            _ => null
        };
        if (!value.HasValue)
            throw new NotSupportedException(
                "SQL Server MERGE source key must be a non-NULL exact signed INT literal; expressions and implicit casts are not approved.");

        if (merge.MatchPredicate is not BinaryExpr { Operator: "=" } comparison
            || !((IsExactKey(comparison.Left, "t", key) && IsExactKey(comparison.Right, "s", key))
                || (IsExactKey(comparison.Right, "t", key) && IsExactKey(comparison.Left, "s", key))))
            throw new NotSupportedException(
                "SQL Server MERGE approval requires exactly one direct t.<key> = s.<key> equality, no extra predicates or coercions.");

        return new Shape("dbo", merge.Target.Name.Parts[1].Value, key, value.Value);
    }

    private static bool IsExactKey(SqlExpr expression, string alias, string key) =>
        expression is ColumnExpr { Name.Parts.Length: 2 } column
        && string.Equals(column.Name.Parts[0].Value, alias, StringComparison.Ordinal)
        && string.Equals(column.Name.Parts[1].Value, key, StringComparison.Ordinal);
}
