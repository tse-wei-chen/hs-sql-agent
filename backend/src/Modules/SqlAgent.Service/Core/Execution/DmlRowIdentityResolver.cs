using System.Collections.Immutable;
using SqlAgent.Service.Core.Providers;

namespace SqlAgent.Service.Core.Execution;

/// <summary>
/// Resolves deterministic row identity for DML revalidation from provider metadata.
/// Strict assurance never silently degrades to row-count-only matching.
/// </summary>
public sealed class DmlRowIdentityResolver(IProviderMetadataReader metadataReader)
{
    private readonly IProviderMetadataReader _metadataReader = metadataReader;

    public async Task<ImmutableArray<string>> ResolveAsync(
        string connectionString,
        string tableName,
        DmlRowIdentityAssurance assurance,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var (schema, table) = SplitQualifiedTableName(tableName);
        var columns = await _metadataReader.GetColumnsAsync(
            connectionString,
            schema,
            table,
            cancellationToken);

        var primaryKey = columns
            .Where(column => column.IsPrimaryKey)
            .OrderBy(column => column.PrimaryKeyOrdinal ?? int.MaxValue)
            .ThenBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .Select(column => column.Name)
            .ToImmutableArray();

        if (!primaryKey.IsDefaultOrEmpty)
            return primaryKey;

        if (assurance == DmlRowIdentityAssurance.CountOnly)
            return ImmutableArray<string>.Empty;

        throw new InvalidOperationException(
            $"Strict DML row-identity assurance requires a primary key on '{tableName}'.");
    }

    internal static (string Schema, string Table) SplitQualifiedTableName(string tableName)
    {
        var parts = tableName
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new InvalidOperationException(
                $"DML target '{tableName}' must be schema-qualified as <schema>.<table> for row-identity planning.");
        }

        return (parts[0], parts[1]);
    }
}
