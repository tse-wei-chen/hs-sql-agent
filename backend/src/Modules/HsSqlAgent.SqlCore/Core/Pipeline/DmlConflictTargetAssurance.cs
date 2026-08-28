using System.Collections.Immutable;

namespace HsSqlAgent.SqlCore.Core.Pipeline;

/// <summary>
/// Explicit metadata-backed assurance for provider lowerings whose native conflict semantics need
/// stronger information than the canonical conflict-column list alone. Firebird consumes only the
/// resolved-primary-key channel. MySQL consumes only the matched-unique-key inventory channel, so a
/// unique-key assurance never implicitly authorizes Firebird MATCHING semantics. INSERT ... SELECT
/// conflict DO UPDATE additionally consumes an explicit source-row uniqueness channel.
/// </summary>
public sealed record DmlConflictTargetAssurance(
    ImmutableArray<string> PrimaryKeyColumns)
{
    public ImmutableArray<string> MatchedUniqueKeyColumns { get; init; } = ImmutableArray<string>.Empty;

    public string? MatchedUniqueKeyName { get; init; }

    public bool MatchedUniqueKeyIsPrimaryKey { get; init; }

    public int EnforcedUniqueKeyCount { get; init; }

    public bool HasUnsupportedEnforcedUniqueKeys { get; init; }

    /// <summary>
    /// INSERT target-column names whose projected source values are proven unique across the
    /// INSERT ... SELECT source rows for this statement. This is deliberately separate from target
    /// unique-key metadata: a unique target index says nothing about duplicate proposed rows.
    /// </summary>
    public ImmutableArray<string> SourceRowsUniqueByInsertColumns { get; init; } = ImmutableArray<string>.Empty;

    public bool IsSoleEnforcedUniqueKey =>
        !MatchedUniqueKeyColumns.IsDefaultOrEmpty
        && EnforcedUniqueKeyCount == 1
        && !HasUnsupportedEnforcedUniqueKeys;

    public DmlConflictTargetAssurance WithSourceRowsUniqueByInsertColumns(IEnumerable<string> columns) =>
        this with
        {
            SourceRowsUniqueByInsertColumns = NormalizeColumns(
                columns,
                "Source-row uniqueness assurance",
                nameof(columns))
        };

    public static DmlConflictTargetAssurance FromPrimaryKey(IEnumerable<string> columns)
    {
        var normalized = NormalizeColumns(columns, "Primary-key assurance", nameof(columns));
        return new DmlConflictTargetAssurance(normalized);
    }

    public static DmlConflictTargetAssurance FromUniqueKey(
        IEnumerable<string> columns,
        string keyName,
        bool isPrimaryKey,
        int enforcedUniqueKeyCount,
        bool hasUnsupportedEnforcedUniqueKeys)
    {
        var normalized = NormalizeColumns(columns, "Unique-key assurance", nameof(columns));
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        if (enforcedUniqueKeyCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(enforcedUniqueKeyCount),
                enforcedUniqueKeyCount,
                "Unique-key assurance requires at least one enforced unique key in the provider inventory.");
        }

        return new DmlConflictTargetAssurance(ImmutableArray<string>.Empty)
        {
            MatchedUniqueKeyColumns = normalized,
            MatchedUniqueKeyName = keyName.Trim(),
            MatchedUniqueKeyIsPrimaryKey = isPrimaryKey,
            EnforcedUniqueKeyCount = enforcedUniqueKeyCount,
            HasUnsupportedEnforcedUniqueKeys = hasUnsupportedEnforcedUniqueKeys
        };
    }

    private static ImmutableArray<string> NormalizeColumns(
        IEnumerable<string> columns,
        string assuranceName,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var normalized = columns
            .Select(column => string.IsNullOrWhiteSpace(column)
                ? throw new ArgumentException($"{assuranceName} columns cannot be empty.", parameterName)
                : column.Trim())
            .ToImmutableArray();
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException($"{assuranceName} requires at least one column.", parameterName);
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new ArgumentException($"{assuranceName} columns cannot contain duplicates.", parameterName);
        return normalized;
    }
}
