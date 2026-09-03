using System.Collections.Immutable;
using System.Data.Common;
using HsSqlAgent.Provider.Abstractions;

namespace SqlAgent.Service.Core.Execution;

public sealed record DmlPhysicalTargetResolution(
    string Schema,
    string Table)
{
    public string QualifiedTableName => $"{Schema}.{Table}";
}

public sealed record DmlRowIdentityResolution(
    string Schema,
    string Table,
    ImmutableArray<string> Columns)
{
    public string QualifiedTableName => $"{Schema}.{Table}";
}

/// <summary>
/// Resolves a DML target to one physical table and determines its deterministic row identity.
/// Strict assurance never silently degrades to row-count-only matching.
/// </summary>
public sealed class DmlRowIdentityResolver(IProviderMetadataReader metadataReader)
{
    private readonly IProviderMetadataReader _metadataReader = metadataReader;

    public async Task<ImmutableArray<string>> ResolveAsync(
        string connectionString,
        string tableName,
        DmlRowIdentityAssurance assurance,
        CancellationToken cancellationToken = default) =>
        (await ResolveTargetAsync(
            connectionString,
            tableName,
            assurance,
            cancellationToken)).Columns;

    public Task<DmlRowIdentityResolution> ResolveTargetAsync(
        string connectionString,
        string tableName,
        DmlRowIdentityAssurance assurance,
        CancellationToken cancellationToken = default) =>
        ResolveTargetCoreAsync(
            metadataConnection: null,
            connectionString,
            tableName,
            assurance,
            cancellationToken);

    public Task<DmlRowIdentityResolution> ResolveTargetAsync(
        DbConnection metadataConnection,
        string connectionString,
        string tableName,
        DmlRowIdentityAssurance assurance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadataConnection);
        return ResolveTargetCoreAsync(
            metadataConnection,
            connectionString,
            tableName,
            assurance,
            cancellationToken);
    }

    public Task<DmlPhysicalTargetResolution> ResolvePhysicalTargetAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default) =>
        ResolvePhysicalTargetCoreAsync(
            metadataConnection: null,
            connectionString,
            tableName,
            cancellationToken);

    public Task<DmlPhysicalTargetResolution> ResolvePhysicalTargetAsync(
        DbConnection metadataConnection,
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadataConnection);
        return ResolvePhysicalTargetCoreAsync(
            metadataConnection,
            connectionString,
            tableName,
            cancellationToken);
    }

    private async Task<DmlRowIdentityResolution> ResolveTargetCoreAsync(
        DbConnection? metadataConnection,
        string connectionString,
        string tableName,
        DmlRowIdentityAssurance assurance,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var target = await ResolvePhysicalTargetCoreAsync(
            metadataConnection,
            connectionString,
            tableName,
            cancellationToken);
        var columns =
            metadataConnection is not null
            && _metadataReader is IProviderConnectionMetadataReader connectionMetadata
                ? await connectionMetadata.GetColumnsAsync(
                    metadataConnection,
                    target.Schema,
                    target.Table,
                    cancellationToken)
                : await _metadataReader.GetColumnsAsync(
                    connectionString,
                    target.Schema,
                    target.Table,
                    cancellationToken);

        var primaryKey = columns
            .Where(column => column.IsPrimaryKey)
            .OrderBy(column => column.PrimaryKeyOrdinal ?? int.MaxValue)
            .ThenBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .Select(column => column.Name)
            .ToImmutableArray();

        if (!primaryKey.IsDefaultOrEmpty)
            return new DmlRowIdentityResolution(target.Schema, target.Table, primaryKey);

        if (assurance == DmlRowIdentityAssurance.CountOnly)
            return new DmlRowIdentityResolution(target.Schema, target.Table, ImmutableArray<string>.Empty);

        throw new InvalidOperationException(
            $"Strict DML row-identity assurance requires a primary key on '{target.Schema}.{target.Table}'.");
    }

    private async Task<DmlPhysicalTargetResolution> ResolvePhysicalTargetCoreAsync(
        DbConnection? metadataConnection,
        string connectionString,
        string tableName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var parts = tableName
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
            return new DmlPhysicalTargetResolution(parts[0], parts[1]);

        if (parts.Length != 1)
        {
            throw new InvalidOperationException(
                $"DML target '{tableName}' must be <table> or <schema>.<table> for row-identity planning.");
        }

        var requestedTable = parts[0];
        IReadOnlyList<DatabaseTableMetadata> matches;
        if (metadataConnection is not null
            && _metadataReader is IProviderConnectionMetadataReader connectionMetadata)
        {
            matches = await connectionMetadata.FindTablesAsync(
                metadataConnection,
                requestedTable,
                cancellationToken);
        }
        else if (_metadataReader is IProviderTableLookup tableLookup)
        {
            matches = await tableLookup.FindTablesAsync(
                connectionString,
                requestedTable,
                cancellationToken);
        }
        else
        {
            var fallbackMatches = new List<DatabaseTableMetadata>();
            var schemas = await _metadataReader.GetSchemasAsync(connectionString, cancellationToken);
            foreach (var schema in schemas)
            {
                var tables = await _metadataReader.GetTablesAsync(
                    connectionString,
                    schema,
                    cancellationToken);
                foreach (var table in tables)
                {
                    if (string.Equals(table, requestedTable, StringComparison.OrdinalIgnoreCase))
                        fallbackMatches.Add(new DatabaseTableMetadata(schema, table));
                }
            }

            matches = fallbackMatches;
        }

        return matches.Count switch
        {
            1 => new DmlPhysicalTargetResolution(matches[0].Schema, matches[0].Table),
            0 => throw new InvalidOperationException(
                $"DML target '{tableName}' could not be resolved to a physical table. Schema-qualify the target explicitly."),
            _ => throw new InvalidOperationException(
                $"DML target '{tableName}' is ambiguous across schemas. Schema-qualify the target explicitly.")
        };
    }

}
