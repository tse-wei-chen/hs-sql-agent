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
        string TargetTable, ImmutableArray<string> TargetKeys,
        string SourceTable, ImmutableArray<string> SourceKeys);

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
                    candidate.TargetKeys.Select(k => new SqlSchemaColumn(k, SqlSchemaValueKind.Numeric)).ToArray()),
                SqlSchemaTable.WithTypes(candidate.SourceTable,
                    candidate.SourceKeys.Select(k => new SqlSchemaColumn(k, SqlSchemaValueKind.Numeric)).ToArray())
            ]);

        // This is the ONLY authority minting operation. It independently
        // verifies both physical INT types and the enabled, unfiltered,
        // single-column source UNIQUE index in the real SQL Server sys catalog.
        // Unsupported or invisible metadata throws: never downgrade to a
        // compile-time Catalog guess.
        var proof = candidate.SourceKeys.Length == 1
            ? SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                metadataConnection, snapshot, candidate.TargetTable, candidate.TargetKeys[0],
                candidate.SourceTable, candidate.SourceKeys[0])
            : SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                metadataConnection, snapshot, candidate.TargetTable, candidate.TargetKeys.ToArray(),
                candidate.SourceTable, candidate.SourceKeys.ToArray());

        cancellationToken.ThrowIfCancellationRequested();
        return context.WithSqlServerCteUniqueKeyAssurance(proof);
    }

    private static bool HasSqlServer2005Profile(SqlProviderCapabilityProfile? profile) =>
        profile is not null
        && profile.Provider == SqlAgentToolType.MsSqlServer
        && profile.ServerVersion is not null
        && profile.ServerVersion >= new Version(9, 0);

    // The Host extracts only bounded, direct column lineage. It does not
    // mint executable authority: F# independently proves the ordered key
    // lineage and the real database verifies physical INT uniqueness.
    private readonly record struct ProjectionLineage(
        string OutputName, string PhysicalColumn, int FirstOrdinal);

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
            || !TryBuildDirectProjectionChain(
                ctes, out var cteName, out var outputs, out var sourceTable)
            || !Same(cteName, referencedName)
            || Same(targetTable, sourceTable))
            return false;

        var cteQualifier = cteReference.Alias?.Value ?? cteName;
        // Only mandatory AND-connected ordinary '=' edges contribute.
        // A projected payload may be selected but never substitutes for a
        // missing key equality, implicit cast, or unverifiable index.
        var matched = new List<(ProjectionLineage Source, string Target)>();
        var pending = new Stack<SqlExpr>();
        if (predicate is not null) pending.Push(predicate);
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
            foreach (var output in outputs)
            {
                if ((TryTargetKey(binary.Left, targetTable, out var key)
                     && IsCteKey(binary.Right, cteQualifier, output.OutputName))
                    || (TryTargetKey(binary.Right, targetTable, out key)
                        && IsCteKey(binary.Left, cteQualifier, output.OutputName)))
                {
                    if (matched.Any(m => Same(m.Source.PhysicalColumn, output.PhysicalColumn)
                                         || Same(m.Source.OutputName, output.OutputName)
                                         || Same(m.Target, key)))
                        return false;
                    matched.Add((output, key));
                }
            }
        }

        if (matched.Count is < 1 or > 2) return false;
        // The original physical projection order is preserved in the
        // attested tuple; sys.index_columns.key_ordinal verifies that exact
        // order. Payload positions do not become index components.
        var ordered = matched.OrderBy(m => m.Source.FirstOrdinal).ToArray();
        candidate = new Candidate(targetTable,
            ordered.Select(m => m.Target).ToImmutableArray(),
            sourceTable,
            ordered.Select(m => m.Source.PhysicalColumn).ToImmutableArray());
        return true;
    }

    /// <summary>
    /// Closed one-to-three-hop CTE projection chain. Extra direct columns
    /// may appear beside the keys, but cannot introduce a join, grouping,
    /// duplicate alias, expression or another physical row source.
    /// Physical column lineage is preserved even when payload is omitted
    /// by a later hop. All output names remain unambiguous.
    /// </summary>
    private static bool TryBuildDirectProjectionChain(
        ImmutableArray<CteDefinition> ctes,
        out string finalCte, out ImmutableArray<ProjectionLineage> finalColumns,
        out string physicalTable)
    {
        finalCte = "";
        finalColumns = ImmutableArray<ProjectionLineage>.Empty;
        physicalTable = "";
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
                || select.GroupBy.Length != 0
                || select.Having is not null || select.OrderBy.Length != 0
                || select.Limit.HasValue || select.Offset.HasValue
                || select.FetchPercent.HasValue || select.FetchWithTies
                || select.Select.Length is < 1 or > 32
                || select.From is not NamedTableSource input
                || (!cte.ColumnAliases.IsDefaultOrEmpty
                    && cte.ColumnAliases.Length != select.Select.Length))
                return false;

            if (i == 0)
            {
                if (!TryPhysicalTable(input.Name, out physicalTable))
                    return false;
            }
            else if (!TrySingleName(input.Name, out var namedCte)
                     || !Same(namedCte, finalCte))
                return false;

            var next = ImmutableArray.CreateBuilder<ProjectionLineage>(select.Select.Length);
            var usedOutputs = new HashSet<string>(StringComparer.Ordinal);
            var usedInputs = new HashSet<string>(StringComparer.Ordinal);
            var qualifier = input.Alias?.Value ?? (i == 0
                ? physicalTable.Split('.')[1]
                : finalCte);
            foreach (var (item, ordinal) in select.Select.Select((item, ordinal) => (item, ordinal)))
            {
                if (item.Expression is not ColumnExpr column)
                    return false;

                string physicalColumn;
                int firstOrdinal;
                string inputName;
                if (i == 0)
                {
                    if (!TryPhysicalKey(column.Name, input, out inputName))
                        return false;
                    physicalColumn = inputName;
                    firstOrdinal = ordinal;
                }
                else
                {
                    var previous = finalColumns.FirstOrDefault(p =>
                        TrySelectedKey(column.Name, p.OutputName, qualifier));
                    if (string.IsNullOrEmpty(previous.OutputName))
                        return false;
                    inputName = previous.OutputName;
                    physicalColumn = previous.PhysicalColumn;
                    firstOrdinal = previous.FirstOrdinal;
                }

                var output = cte.ColumnAliases.IsDefaultOrEmpty
                    ? item.Alias?.Value ?? inputName
                    : TrySingleName(cte.ColumnAliases[ordinal], out var columnAlias)
                        ? columnAlias : "";
                if (!SafeName(output) || !usedOutputs.Add(output)
                    || !usedInputs.Add(inputName))
                    return false;
                next.Add(new ProjectionLineage(output, physicalColumn, firstOrdinal));
            }
            finalCte = cteName;
            finalColumns = next.MoveToImmutable();
        }
        return !string.IsNullOrEmpty(physicalTable) && !finalColumns.IsDefaultOrEmpty;
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
