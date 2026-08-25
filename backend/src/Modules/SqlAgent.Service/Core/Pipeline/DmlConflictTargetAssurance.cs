using System.Collections.Immutable;

namespace SqlAgent.Service.Core.Pipeline;

/// <summary>
/// Explicit metadata-backed assurance for provider lowerings whose native conflict semantics require
/// proof that the canonical conflict target identifies at most one existing row. Today Core can
/// prove this only from a resolved primary key; general UNIQUE-index metadata is intentionally not
/// inferred from column names.
/// </summary>
public sealed record DmlConflictTargetAssurance(
    ImmutableArray<string> PrimaryKeyColumns)
{
    public static DmlConflictTargetAssurance FromPrimaryKey(IEnumerable<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var normalized = columns
            .Select(column => string.IsNullOrWhiteSpace(column)
                ? throw new ArgumentException("Primary-key assurance columns cannot be empty.", nameof(columns))
                : column.Trim())
            .ToImmutableArray();
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("Primary-key assurance requires at least one column.", nameof(columns));
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new ArgumentException("Primary-key assurance columns cannot contain duplicates.", nameof(columns));
        return new DmlConflictTargetAssurance(normalized);
    }
}
