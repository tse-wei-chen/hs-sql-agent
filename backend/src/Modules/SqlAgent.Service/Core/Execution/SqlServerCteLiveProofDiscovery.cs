using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using HsSqlAgent.SqlCore.Core.Ast;
using HsSqlAgent.SqlCore.Core.Compilation;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;
using HsSqlAgent.SqlCore.SqlParsing;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Host-only, opt-in source/target proof discovery over a parsed CLR AST and
/// the already-open provider metadata connection. This does not authorize a
/// mutation: the closed F# compiler independently validates the exact CTE,
/// and commit still requires the transaction-bound live IsCurrent check.
/// </summary>
internal static class SqlServerCteLiveProofDiscovery
{
    private sealed record Candidate(
        string TargetTable, string TargetKey, string SourceTable, string SourceKey);

    internal static SqlPlanValidationContext AttachIfProven(
        DbConnection? metadataConnection,
        ParsedStatement parsed,
        SqlStatement resolvedMutation,
        SqlAgentToolType provider,
        SqlPlanValidationContext context,
        SqlProviderCapabilityProfile? targetProfile,
        DmlRowIdentityAssurance rowIdentity,
        int maxAffectedRows,
        CancellationToken cancellationToken)
    {
        // A separate metadata connection is never silently created from a
        // string, and no compatibility compiler is allowed to use this gate.
        if (metadataConnection is null
            || provider != SqlAgentToolType.MsSqlServer
            || parsed.SourceDialect != SqlAgentToolType.MsSqlServer
            || context.SqlServerCteUniqueKeyAssurance is not null
            || rowIdentity != DmlRowIdentityAssurance.Strict
            || maxAffectedRows <= 0
            || !HasSqlServer2005Profile(parsed.SourceProfile)
            || !HasSqlServer2005Profile(targetProfile)
            || !TryDescribe(resolvedMutation, out var candidate))
        {
            return context;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (metadataConnection.State != ConnectionState.Open)
            throw new InvalidOperationException(
                "Automatic SQL Server CTE proof requires the already-open, verified metadata connection.");

        // The proof's snapshot is only a tightly scoped identity/type
        // witness. This never becomes an invented full-table schema snapshot,
        // and never suppresses the ordinary optional live schema verifier.
        var snapshot = context.SchemaSnapshot ?? new SqlSchemaSnapshot(
            SqlAgentToolType.MsSqlServer,
            "host-discovered-sqlserver-cte-int-v1",
            [
                SqlSchemaTable.WithTypes(candidate.TargetTable,
                    [new SqlSchemaColumn(candidate.TargetKey, SqlSchemaValueKind.Numeric)]),
                SqlSchemaTable.WithTypes(candidate.SourceTable,
                    [new SqlSchemaColumn(candidate.SourceKey, SqlSchemaValueKind.Numeric)])
            ]);

        // This is the ONLY authority minting operation. It independently
        // verifies both physical INT types and the enabled, unfiltered,
        // single-column source UNIQUE index in the real SQL Server sys catalog.
        // Unsupported or invisible metadata throws: never downgrade to a
        // compile-time Catalog guess.
        var proof = SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
            metadataConnection, snapshot,
            candidate.TargetTable, candidate.TargetKey,
            candidate.SourceTable, candidate.SourceKey);

        cancellationToken.ThrowIfCancellationRequested();
        return context.WithSqlServerCteUniqueKeyAssurance(proof);
    }

    private static bool HasSqlServer2005Profile(SqlProviderCapabilityProfile? profile) =>
        profile is not null
        && profile.Provider == SqlAgentToolType.MsSqlServer
        && profile.ServerVersion is not null
        && profile.ServerVersion >= new Version(9, 0);

    // The semantic owner is still the F# closed AST. This prefilter intentionally
    // accepts fewer shapes than the compiler, never more; any ambiguous CLR
    // form simply declines automatic proof discovery.
    private static bool TryDescribe(SqlStatement mutation, out Candidate candidate)
    {
        candidate = null!;
        var (ctes, sources, target, predicate) = mutation switch
        {
            UpdateStatement update =>
                (update.Ctes, GetSources(update.FromSources, update.From),
                 update.Target, update.Predicate),
            DeleteStatement delete =>
                (delete.Ctes, GetSources(delete.UsingSources, delete.Using),
                 delete.Target, delete.Predicate),
            _ => (ImmutableArray<CteDefinition>.Empty,
                  ImmutableArray<TableSource>.Empty, null!, null!)
        };

        if (target is null || target.Alias is not null
            || ctes.IsDefaultOrEmpty || ctes.Length > 3
            || sources.Length != 1
            || !TryPhysicalTable(target.Name, out var targetTable)
            || sources[0] is not NamedTableSource cteReference
            || !TrySingleName(cteReference.Name, out var referencedName)
            || !TryBuildDirectKeyChain(
                ctes, out var cteName, out var outputName,
                out var sourceTable, out var sourceKey)
            || !Same(cteName, referencedName)
            || Same(targetTable, sourceTable))
        {
            return false;
        }

        var cteQualifier = cteReference.Alias?.Value ?? cteName;
        // The only accepted source-to-target match is a mandatory direct
        // ordinary '=' predicate reachable through AND. Arbitrary derived
        // columns, OR or CAST are not sources of unique INT evidence.
        var pending = new Stack<SqlExpr>();
        if (predicate is not null) pending.Push(predicate);
        string? matchedTargetKey = null;
        var budget = 256;
        while (pending.Count != 0)
        {
            if (--budget < 0) return false;
            if (pending.Pop() is not BinaryExpr binary) continue;
            if (SameOperator(binary.Operator, "AND"))
            {
                pending.Push(binary.Right);
                pending.Push(binary.Left);
                continue;
            }
            if (!SameOperator(binary.Operator, "=")) continue;
            if (TryTargetKey(binary.Left, targetTable, out var key)
                && IsCteKey(binary.Right, cteQualifier, outputName)
                || TryTargetKey(binary.Right, targetTable, out key)
                   && IsCteKey(binary.Left, cteQualifier, outputName))
            {
                if (matchedTargetKey is not null && !Same(matchedTargetKey, key))
                    return false;
                matchedTargetKey = key;
            }
        }
        if (matchedTargetKey is null) return false;
        candidate = new Candidate(targetTable, matchedTargetKey, sourceTable, sourceKey);
        return true;
    }

    /// <summary>
    /// Derive exactly one INT-key lineage through up to three lexically ordered
    /// CTEs. A later CTE must read the *immediately preceding* CTE; no side
    /// branches, recursive references, duplicate scope or cardinality-
    /// multiplying features may acquire physical source key evidence.
    /// </summary>
    private static bool TryBuildDirectKeyChain(
        ImmutableArray<CteDefinition> ctes,
        out string finalCte, out string finalKey,
        out string physicalTable, out string physicalKey)
    {
        finalCte = "";
        finalKey = "";
        physicalTable = "";
        physicalKey = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < ctes.Length; i++)
        {
            var cte = ctes[i];
            if (!TrySingleName(cte.Name, out var cteName)
                || !seen.Add(cteName)
                || cte.RecursiveScope
                || cte.Query is not SelectStatement select
                || select.Ctes.Length != 0 || select.Joins.Length != 0
                || select.Distinct || select.DistinctOn.Length != 0
                || select.Select.Length != 1 || select.GroupBy.Length != 0
                || select.Having is not null || select.OrderBy.Length != 0
                || select.Limit.HasValue || select.Offset.HasValue
                || select.FetchPercent.HasValue || select.FetchWithTies
                || select.Select[0].Expression is not ColumnExpr keyColumn
                || select.From is not NamedTableSource input)
                return false;

            string inputKey;
            if (i == 0)
            {
                if (!TryPhysicalTable(input.Name, out physicalTable)
                    || !TryPhysicalKey(keyColumn.Name, input, out physicalKey))
                    return false;
                inputKey = physicalKey;
            }
            else
            {
                if (!TrySingleName(input.Name, out var namedCte)
                    || !Same(namedCte, finalCte))
                    return false;
                var qualifier = input.Alias?.Value ?? finalCte;
                if (!TrySelectedKey(keyColumn.Name, finalKey, qualifier))
                    return false;
                inputKey = finalKey;
            }

            string outputKey;
            if (cte.ColumnAliases.IsDefaultOrEmpty)
                outputKey = select.Select[0].Alias?.Value ?? inputKey;
            else if (cte.ColumnAliases.Length == 1
                     && TrySingleName(cte.ColumnAliases[0], out var columnAlias))
                outputKey = columnAlias;
            else
                return false;

            if (!SafeName(outputKey))
                return false;
            finalCte = cteName;
            finalKey = outputKey;
        }
        return !string.IsNullOrEmpty(physicalTable)
            && !string.IsNullOrEmpty(physicalKey);
    }

    private static bool TrySelectedKey(
        SqlIdentifier identifier, string expectedKey, string qualifier) =>
        identifier.Parts.Length switch
        {
            1 => Same(identifier.Parts[0].Value, expectedKey),
            2 => Same(identifier.Parts[0].Value, qualifier)
                 && Same(identifier.Parts[1].Value, expectedKey),
            _ => false
        };

    private static ImmutableArray<TableSource> GetSources(
        ImmutableArray<TableSource> primary,
        ImmutableArray<NamedTableSource> legacy) =>
        !primary.IsDefaultOrEmpty
            ? primary
            : legacy.Cast<TableSource>().ToImmutableArray();

    private static bool TryPhysicalTable(SqlIdentifier identifier, out string table)
    {
        table = "";
        if (identifier.Parts.Length != 2) return false;
        var schema = identifier.Parts[0].Value;
        var name = identifier.Parts[1].Value;
        if (!SafeName(schema) || !SafeName(name)) return false;
        table = schema + "." + name;
        return true;
    }

    private static bool TrySingleName(SqlIdentifier identifier, out string name)
    {
        name = identifier.Parts.Length == 1 ? identifier.Parts[0].Value : "";
        return SafeName(name);
    }

    private static bool TryPhysicalKey(
        SqlIdentifier identifier, NamedTableSource source, out string key)
    {
        key = "";
        if (identifier.Parts.Length == 1)
        {
            key = identifier.Parts[0].Value;
            return SafeName(key);
        }
        if (identifier.Parts.Length == 2
            && Same(identifier.Parts[0].Value,
                source.Alias?.Value ?? source.Name.Parts[1].Value))
        {
            key = identifier.Parts[1].Value;
            return SafeName(key);
        }
        return false;
    }

    private static bool TryTargetKey(
        SqlExpr expression, string targetTable, out string key)
    {
        key = "";
        if (expression is not ColumnExpr column || column.Name.Parts.Length != 3)
            return false;
        var parts = targetTable.Split('.');
        if (!Same(column.Name.Parts[0].Value, parts[0])
            || !Same(column.Name.Parts[1].Value, parts[1]))
            return false;
        key = column.Name.Parts[2].Value;
        return SafeName(key);
    }

    private static bool IsCteKey(SqlExpr expression, string qualifier, string key) =>
        expression is ColumnExpr column
        && column.Name.Parts.Length == 2
        && Same(column.Name.Parts[0].Value, qualifier)
        && Same(column.Name.Parts[1].Value, key);

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static bool SameOperator(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!char.IsAsciiLetter(name[0]) && name[0] != '_') return false;
        return name.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }
}
