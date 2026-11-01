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
        string SourceTable, ImmutableArray<string> SourceKeys,
        string? JoinedTable, ImmutableArray<string> JoinedKeys);

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
        // An existing immutable snapshot is validated by F# against the
        // physical unique-key catalog below. Only discover key families
        // when the Host must create the bounded snapshot: do not add
        // redundant metadata round trips to explicit-snapshot callers.
        var snapshot = context.SchemaSnapshot;
        if (snapshot is null)
        {
            var boundedTables = new List<SqlSchemaTable>
            {
                PhysicalKeyTable(metadataConnection, candidate.TargetTable, candidate.TargetKeys),
                PhysicalKeyTable(metadataConnection, candidate.SourceTable, candidate.SourceKeys)
            };
            if (candidate.JoinedTable is not null)
                boundedTables.Add(PhysicalKeyTable(
                    metadataConnection, candidate.JoinedTable, candidate.JoinedKeys));
            snapshot = new SqlSchemaSnapshot(
                SqlAgentToolType.MsSqlServer,
                "host-discovered-sqlserver-cte-exact-key-v3", boundedTables);
        }

        // This is the ONLY authority minting operation. It independently
        // verifies both physical, per-ordinal exact integer or fixed-decimal
        // precision/scale types and every key of the enabled, unfiltered
        // source UNIQUE index in the real SQL Server sys catalog.
        // Unsupported or invisible metadata throws: never downgrade to a
        // compile-time Catalog guess.
        var proof = candidate.JoinedTable is not null
            ? SqlServerCteUniqueKeyAssurance.VerifyLiveForKeyPreservingJoin(
                metadataConnection, snapshot, candidate.TargetTable, candidate.TargetKeys.ToArray(),
                candidate.SourceTable, candidate.SourceKeys.ToArray(),
                candidate.JoinedTable, candidate.JoinedKeys.ToArray())
            : candidate.SourceKeys.Length == 1
                ? SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    metadataConnection, snapshot, candidate.TargetTable, candidate.TargetKeys[0],
                    candidate.SourceTable, candidate.SourceKeys[0])
                : SqlServerCteUniqueKeyAssurance.VerifyLiveForJoinedMutation(
                    metadataConnection, snapshot, candidate.TargetTable, candidate.TargetKeys.ToArray(),
                    candidate.SourceTable, candidate.SourceKeys.ToArray());

        cancellationToken.ThrowIfCancellationRequested();
        return context.WithSqlServerCteUniqueKeyAssurance(proof);
    }

    // This query describes the *actual* physical value family of each bounded
    // source/target/JOIN key. It is only a typed snapshot witness; F# still
    // mints authority from the independent complete index/sys.* catalog.
    // Never fabricate Numeric metadata for UNIQUEIDENTIFIER.
    private static SqlSchemaTable PhysicalKeyTable(
        DbConnection connection, string table, ImmutableArray<string> keys)
    {
        var parts = table.Split('.');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                "SQL Server physical key discovery requires an exact schema.table.");
        var columns = keys.Select(key =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.name, typ.name, typ.is_user_defined,
                       typ.user_type_id, typ.system_type_id
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.columns c ON c.object_id = t.object_id
                JOIN sys.types typ ON typ.user_type_id = c.user_type_id
                WHERE s.name = @schema AND t.name = @table AND c.name = @column
                """;
            void Bind(string name, string value)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.DbType = DbType.String;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            Bind("@schema", parts[0]);
            Bind("@table", parts[1]);
            Bind("@column", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("SQL Server physical key column is unavailable.");
            var foundName = reader.GetString(0);
            var physicalType = reader.GetString(1);
            if (!StringComparer.Ordinal.Equals(foundName, key)
                || reader.GetBoolean(2)
                || reader.GetInt32(3) != reader.GetByte(4))
                throw new InvalidOperationException(
                    "SQL Server physical key has ambiguous name or custom alias type.");
            var kind = physicalType.ToLowerInvariant() switch
            {
                "tinyint" or "smallint" or "int" or "bigint"
                    or "decimal" or "numeric" => SqlSchemaValueKind.Numeric,
                "uniqueidentifier" => SqlSchemaValueKind.Uuid,
                _ => throw new InvalidOperationException(
                    "SQL Server physical key type family is not supported.")
            };
            if (reader.Read())
                throw new InvalidOperationException("SQL Server physical key column is ambiguous.");
            return new SqlSchemaColumn(key, kind);
        }).ToArray();
        return SqlSchemaTable.WithTypes(table, columns);
    }

    private static bool HasSqlServer2005Profile(SqlProviderCapabilityProfile? profile) =>
        profile is not null
        && profile.Provider == SqlAgentToolType.MsSqlServer
        && profile.ServerVersion is not null
        && profile.ServerVersion >= new Version(9, 0);

    // The Host extracts only bounded, direct column lineage. It does not
    // mint executable authority: F# independently proves the ordered key
    // lineage and the real database verifies physical exact typed-key uniqueness.
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
        // The Host's initial shape check cannot choose the key tuple.
        // Once the actual outer target equality establishes its ordered
        // components, independently require the correlated EXISTS filter
        // to contain EXACTLY those physical source keys.
        if (ctes[0].Query is SelectStatement filteredFirst
            && filteredFirst.From is NamedTableSource filteredRoot
            && filteredRoot.Alias is not null)
        {
            var semi = filteredFirst.Where switch
            {
                ExistsExpr e => e,
                UnaryExpr u when SameOperator(u.Operator, "NOT")
                                 && u.Operand is ExistsExpr e => e,
                _ => null
            };
            if (semi is not null
                && !ProvenCorrelatedKeyExists(
                    semi, filteredRoot.Alias.Value, sourceTable,
                    ordered.Select(m => m.Source.PhysicalColumn).ToArray()))
                return false;
        }
        string? joinedTable = null;
        var joinedKeys = ImmutableArray<string>.Empty;
        if (ctes[0].Query is SelectStatement first
            && first.From is NamedTableSource physical
            && first.Joins.Any(join => !ProvenSingletonJoin(join)))
        {
            var join = first.Joins[0];
            var rootKeys = ordered.Select(m => m.Source.PhysicalColumn).ToArray();
            if (!ProvenPhysicalSelfJoin(physical, join, sourceTable, rootKeys))
            {
                if (!TryDescribeDistinctPhysicalJoin(
                    physical, join, sourceTable, targetTable, rootKeys,
                    out joinedTable, out joinedKeys))
                    return false;
            }
        }
        candidate = new Candidate(targetTable,
            ordered.Select(m => m.Target).ToImmutableArray(),
            sourceTable,
            ordered.Select(m => m.Source.PhysicalColumn).ToImmutableArray(),
            joinedTable, joinedKeys);
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
                || select.Ctes.Length != 0
                || select.Joins.Length > 1

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

            // Capture an ordinary local, not the method's out parameter.
            var knownPhysicalTable = physicalTable;
            if (select.Joins.Any(join => !ProvenSingletonJoin(join)
                    && !(i == 0 && (HasPhysicalSelfJoinShape(input, join, knownPhysicalTable)
                        || HasDistinctPhysicalJoinShape(input, join, knownPhysicalTable)))))
                return false;

            if (select.Where is not null &&
                !SafeCteFilter(select.Where, input, i, knownPhysicalTable))
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

    // EXISTS and NOT EXISTS are row filters, never row multipliers. For
    // this first M07 predicate boundary, only a correlated, full-key
    // EXISTS over the already live-attested physical source is admitted.
    // Other query-bearing predicates cannot borrow its unique index.
    private static bool SafeCteFilter(
        SqlExpr predicate, NamedTableSource root, int hop,
        string physicalSource)
    {
        if (predicate is ExistsExpr exists)
        {
            if (hop != 0 || root.Alias is null
                || !SafeName(root.Alias.Value))
                return false;
            // The whole WHERE must be the semi-/anti-join predicate.
            // A separate SQL expression around EXISTS needs a distinct
            // proof of deterministic Boolean behavior.
            return ProvenCorrelatedKeyExists(exists, root.Alias.Value, physicalSource);
        }
        if (predicate is UnaryExpr unary && SameOperator(unary.Operator, "NOT")
            && unary.Operand is ExistsExpr negated)
            return hop == 0 && root.Alias is not null
                && ProvenCorrelatedKeyExists(negated, root.Alias.Value, physicalSource);
        return !ContainsQueryPredicate(predicate);
    }

    private static bool ContainsQueryPredicate(SqlExpr expr)
    {
        var todo = new Stack<SqlExpr>();
        todo.Push(expr);
        var budget = 256;
        while (todo.Count > 0)
        {
            if (--budget < 0) return true;
            switch (todo.Pop())
            {
                case ExistsExpr:
                case SubqueryExpr:
                    return true;
                case UnaryExpr unary:
                    todo.Push(unary.Operand);
                    break;
                case BinaryExpr binary:
                    todo.Push(binary.Right);
                    todo.Push(binary.Left);
                    break;
                case FunctionCallExpr function:
                    foreach (var arg in function.Arguments) todo.Push(arg);
                    foreach (var order in function.AggregateOrderBy)
                        todo.Push(order.Expression);
                    break;
                case RegexExpr regex:
                    todo.Push(regex.Value);
                    todo.Push(regex.Pattern);
                    break;
                case PostgresJsonAccessExpr json:
                    todo.Push(json.Value);
                    break;
                case FilterExpr filter:
                    todo.Push(filter.Expression);
                    todo.Push(filter.Predicate);
                    break;
                case WindowedExpr windowed:
                    todo.Push(windowed.Expression);
                    foreach (var part in windowed.Window.PartitionBy) todo.Push(part);
                    foreach (var order in windowed.Window.OrderBy)
                        todo.Push(order.Expression);
                    break;
                case CastExpr cast:
                    todo.Push(cast.Expression);
                    break;
                case ExtractExpr extract:
                    todo.Push(extract.Expression);
                    break;
                case CaseExpr @case:
                    foreach (var branch in @case.Branches)
                    {
                        todo.Push(branch.Condition);
                        todo.Push(branch.Value);
                    }
                    if (@case.ElseExpression is not null)
                        todo.Push(@case.ElseExpression);
                    break;
                case InExpr @in:
                    todo.Push(@in.Value);
                    foreach (var item in @in.Items) todo.Push(item);
                    break;
                case BetweenExpr between:
                    todo.Push(between.Value);
                    todo.Push(between.Lower);
                    todo.Push(between.Upper);
                    break;
                case IsNullExpr isNull:
                    todo.Push(isNull.Value);
                    break;
                case ColumnExpr:
                case HsSqlAgent.SqlCore.Core.Binding.BoundColumnExpr:
                case LiteralExpr:
                case IntervalExpr:
                    break;
                // Unknown CLR expression classes must not conceal a
                // correlated/subquery read under an attested key proof.
                default:
                    return true;
            }
        }
        return false;
    }

    private static bool ProvenCorrelatedKeyExists(
        ExistsExpr predicate, string outerAlias, string physicalSource,
        IReadOnlyList<string>? requiredKeys = null)
    {
        if (predicate.Query is not SelectStatement query
            || query.Ctes.Length != 0 || query.Joins.Length != 0
            || query.From is not NamedTableSource filtered
            || filtered.Alias is null || !SafeName(filtered.Alias.Value)
            || Same(outerAlias, filtered.Alias.Value)
            || !TryPhysicalTable(filtered.Name, out var table)
            || !Same(table, physicalSource)
            || query.Select.Length != 1
            || query.Select[0].Expression is not LiteralExpr
            || query.Distinct || query.DistinctOn.Length != 0
            || query.GroupBy.Length != 0 || query.Having is not null
            || query.OrderBy.Length != 0 || query.Limit.HasValue
            || query.Offset.HasValue || query.FetchPercent.HasValue
            || query.FetchWithTies || query.Where is null)
            return false;

        var pair = new HashSet<string>(StringComparer.Ordinal);
        var inner = filtered.Alias.Value;
        var pending = new Stack<SqlExpr>();
        pending.Push(query.Where);
        var budget = 256;
        while (pending.Count > 0)
        {
            if (--budget < 0) return false;
            if (pending.Pop() is not BinaryExpr binary)
                return false;
            if (SameOperator(binary.Operator, "AND"))
            {
                pending.Push(binary.Left);
                pending.Push(binary.Right);
                continue;
            }
            if (!SameOperator(binary.Operator, "="))
                return false;
            if (TryAliasKey(binary.Left, inner, out var innerName)
                && TryAliasKey(binary.Right, outerAlias, out var outerName)
                || TryAliasKey(binary.Right, inner, out innerName)
                && TryAliasKey(binary.Left, outerAlias, out outerName))
            {
                if (!Same(innerName, outerName) || !pair.Add(innerName))
                    return false;
            }
            else if (!((TryAliasKey(binary.Left, inner, out _)
                      && binary.Right is LiteralExpr)
                       || (TryAliasKey(binary.Right, inner, out _)
                           && binary.Left is LiteralExpr)))
                return false;
        }
        // Require the whole attested key equality; physical columns are
        // chosen by live index proof, never a caller-provided name list.
        return pair.Count is >= 1 and <= 2
               && pair.All(SafeName)
               && (requiredKeys is null
                   || (requiredKeys.Count == pair.Count
                       && requiredKeys.All(pair.Contains)));
    }

    // The second physical table is not trusted until an independent
    // enabled/unfiltered UNIQUE index is attested with exact integer kinds.
    private static bool HasDistinctPhysicalJoinShape(
        NamedTableSource primary, JoinSource join, string physicalSource) =>
        primary.Alias is not null && SafeName(primary.Alias.Value)
        && !join.IsNatural && join.UsingColumns.IsDefaultOrEmpty
        && (SameOperator(join.Kind, "INNER") || SameOperator(join.Kind, "LEFT"))
        && join.Predicate is not null
        && join.Source is NamedTableSource secondary
        && secondary.Alias is not null && SafeName(secondary.Alias.Value)
        && !Same(primary.Alias.Value, secondary.Alias.Value)
        && TryPhysicalTable(secondary.Name, out var joinedTable)
        && !Same(physicalSource, joinedTable);

    private static bool TryDescribeDistinctPhysicalJoin(
        NamedTableSource primary, JoinSource join,
        string physicalSource, string targetTable, string[] primaryKeys,
        out string? joinedTable, out ImmutableArray<string> joinedKeys)
    {
        joinedTable = null;
        joinedKeys = ImmutableArray<string>.Empty;
        if (!HasDistinctPhysicalJoinShape(primary, join, physicalSource)
            || join.Source is not NamedTableSource secondary
            || !TryPhysicalTable(secondary.Name, out var physicalJoined)
            || Same(physicalJoined, targetTable)
            || join.Predicate is null || primaryKeys.Length is < 1 or > 2)
            return false;

        var matched = new string?[primaryKeys.Length];
        var pending = new Stack<SqlExpr>();
        pending.Push(join.Predicate);
        var budget = 256;
        while (pending.Count > 0)
        {
            if (--budget < 0) return false;
            if (pending.Pop() is not BinaryExpr binary) continue;
            if (SameOperator(binary.Operator, "AND"))
            {
                pending.Push(binary.Left);
                pending.Push(binary.Right);
                continue;
            }
            if (!SameOperator(binary.Operator, "=")) continue;
            for (var i = 0; i < primaryKeys.Length; i++)
            {
                if ((IsCteKey(binary.Left, primary.Alias!.Value, primaryKeys[i])
                     && TryAliasKey(binary.Right, secondary.Alias!.Value, out var name))
                    || (IsCteKey(binary.Right, primary.Alias!.Value, primaryKeys[i])
                        && TryAliasKey(binary.Left, secondary.Alias!.Value, out name)))
                {
                    if (matched[i] is not null) return false;
                    matched[i] = name;
                }
            }
        }
        if (matched.Any(x => x is null)
            || matched.Distinct(StringComparer.Ordinal).Count() != primaryKeys.Length)
            return false;
        joinedTable = physicalJoined;
        joinedKeys = matched.Select(x => x!).ToImmutableArray();
        return true;
    }

    private static bool TryAliasKey(SqlExpr expression, string alias, out string key)
    {
        key = "";
        if (expression is not ColumnExpr column
            || column.Name.Parts.Length != 2
            || !Same(column.Name.Parts[0].Value, alias)
            || !SafeName(column.Name.Parts[1].Value))
            return false;
        key = column.Name.Parts[1].Value;
        return true;
    }

    // A self-JOIN on the same live-attested unique INT key cannot multiply
    // one source row if every key component participates in an ordinary
    // mandatory equality between two distinct table aliases.
    private static bool HasPhysicalSelfJoinShape(
        NamedTableSource primary, JoinSource join, string physicalTable) =>
        primary.Alias is not null && SafeName(primary.Alias.Value)
        && !join.IsNatural && join.UsingColumns.IsDefaultOrEmpty
        && (SameOperator(join.Kind, "INNER") || SameOperator(join.Kind, "LEFT"))
        && join.Predicate is not null
        && join.Source is NamedTableSource secondary
        && secondary.Alias is not null && SafeName(secondary.Alias.Value)
        && !Same(primary.Alias.Value, secondary.Alias.Value)
        && TryPhysicalTable(secondary.Name, out var secondTable)
        && Same(physicalTable, secondTable);

    private static bool ProvenPhysicalSelfJoin(
        NamedTableSource primary, JoinSource join,
        string physicalTable, string[] uniqueKeys)
    {
        if (!HasPhysicalSelfJoinShape(primary, join, physicalTable)
            || uniqueKeys.Length is < 1 or > 2
            || join.Source is not NamedTableSource secondary
            || join.Predicate is null)
            return false;

        var matched = new bool[uniqueKeys.Length];
        var pending = new Stack<SqlExpr>();
        pending.Push(join.Predicate);
        var budget = 256;
        while (pending.Count > 0)
        {
            if (--budget < 0) return false;
            if (pending.Pop() is not BinaryExpr binary) continue;
            if (SameOperator(binary.Operator, "AND"))
            {
                pending.Push(binary.Left);
                pending.Push(binary.Right);
                continue;
            }
            if (!SameOperator(binary.Operator, "=")) continue;
            for (var i = 0; i < uniqueKeys.Length; i++)
            {
                var key = uniqueKeys[i];
                if ((IsCteKey(binary.Left, primary.Alias!.Value, key)
                     && IsCteKey(binary.Right, secondary.Alias!.Value, key))
                    || (IsCteKey(binary.Right, primary.Alias!.Value, key)
                        && IsCteKey(binary.Left, secondary.Alias!.Value, key)))
                {
                    if (matched[i]) return false;
                    matched[i] = true;
                }
            }
        }
        return matched.All(identity => identity);
    }

    /// <summary>
    /// A closed, uncorrelated derived SELECT with no FROM and one literal
    /// expression always yields exactly one row. CROSS can therefore add
    /// exactly one row per source row; INNER/LEFT with a literal equality
    /// can yield at most one. This is a syntax proof, not an inferred
    /// uniqueness claim about another physical table.
    /// </summary>
    private static bool ProvenSingletonJoin(JoinSource join)
    {
        if (join.IsNatural || !join.UsingColumns.IsDefaultOrEmpty
            || join.Source is not DerivedTableSource { IsLateral: false } derived
            || !SafeName(derived.Alias.Value)
            || derived.Query is not SelectStatement singleton
            || singleton.Ctes.Length != 0 || singleton.From is not null
            || singleton.Joins.Length != 0
            || singleton.Select.Length != 1
            || singleton.Select[0].Expression is not LiteralExpr
            || singleton.Select[0].Alias is null
            || !SafeName(singleton.Select[0].Alias.Value)
            || singleton.Distinct || singleton.DistinctOn.Length != 0
            || singleton.Where is not null || singleton.GroupBy.Length != 0
            || singleton.Having is not null || singleton.OrderBy.Length != 0
            || singleton.Limit.HasValue || singleton.Offset.HasValue
            || singleton.FetchPercent.HasValue || singleton.FetchWithTies)
            return false;

        if (SameOperator(join.Kind, "CROSS"))
            return join.Predicate is null;
        if (!SameOperator(join.Kind, "INNER")
            && !SameOperator(join.Kind, "LEFT"))
            return false;

        return join.Predicate is BinaryExpr equality
               && SameOperator(equality.Operator, "=")
               && equality.Left is LiteralExpr
               && equality.Right is LiteralExpr;
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
