using HsSqlAgent.SqlCore.Core.Pipeline;
using SqlAgent.Service.Core.Providers;

namespace SqlAgent.Service.Core.Execution;

public sealed record DmlUniqueKeyResolution(
    DatabaseUniqueKeyMetadata MatchedKey,
    IReadOnlyList<DatabaseUniqueKeyMetadata> EnforcedKeys)
{
    public bool IsSoleEnforcedUniqueKey => EnforcedKeys.Count == 1;

    public bool HasUnsupportedEnforcedUniqueKeys =>
        EnforcedKeys.Any(key => !key.IsSimpleEnforcedColumnKey);

    public DmlConflictTargetAssurance ToConflictTargetAssurance() =>
        DmlConflictTargetAssurance.FromUniqueKey(
            MatchedKey.Columns,
            MatchedKey.Name,
            MatchedKey.IsPrimaryKey,
            EnforcedKeys.Count,
            HasUnsupportedEnforcedUniqueKeys);
}

/// <summary>
/// Resolves an explicit canonical conflict target against provider-native uniqueness metadata. The
/// resolver never drops richer enforced unique keys from the inventory because providers such as
/// MySQL may react to any unique-key conflict even when Core cannot target that key structurally.
/// </summary>
public sealed class DmlUniqueKeyResolver(IProviderMetadataReader metadataReader)
{
    private readonly IProviderMetadataReader _metadataReader = metadataReader;

    public async Task<DmlUniqueKeyResolution> ResolveAsync(
        string connectionString,
        string schema,
        string table,
        IEnumerable<string> conflictTargetColumns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(conflictTargetColumns);

        var target = conflictTargetColumns
            .Select(column => string.IsNullOrWhiteSpace(column)
                ? throw new ArgumentException("Conflict-target columns cannot be empty.", nameof(conflictTargetColumns))
                : column.Trim())
            .ToArray();
        if (target.Length == 0)
            throw new ArgumentException("Conflict-target resolution requires at least one column.", nameof(conflictTargetColumns));
        if (target.Distinct(StringComparer.OrdinalIgnoreCase).Count() != target.Length)
            throw new ArgumentException("Conflict-target columns cannot contain duplicates.", nameof(conflictTargetColumns));

        var inventory = await _metadataReader.GetUniqueKeysAsync(
            connectionString,
            schema,
            table,
            cancellationToken);
        var enforced = inventory
            .Where(key => key.IsEnforced)
            .ToArray();
        var targetSet = target.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = enforced
            .Where(key => key.IsSimpleEnforcedColumnKey
                && key.Columns.Count == targetSet.Count
                && key.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(targetSet))
            .OrderByDescending(key => key.IsPrimaryKey)
            .ThenBy(key => key.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matches.Length == 0)
        {
            var unsupported = enforced.Count(key => !key.IsSimpleEnforcedColumnKey);
            throw new InvalidOperationException(
                $"Conflict target ({string.Join(", ", target)}) does not match a simple enforced unique key on '{schema}.{table}'. " +
                $"The provider reported {enforced.Length} enforced unique key(s), including {unsupported} richer key shape(s) that Core keeps fail-closed.");
        }

        return new DmlUniqueKeyResolution(matches[0], enforced);
    }
}
