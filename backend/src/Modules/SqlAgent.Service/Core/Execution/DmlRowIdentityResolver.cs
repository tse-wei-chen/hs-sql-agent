using System.Collections.Immutable;
using SqlAgent.Service.Core.Providers;

namespace SqlAgent.Service.Core.Execution;

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

    public async Task<DmlRowIdentityResolution> ResolveTargetAsync(
        string connectionString,
        string tableName,
        DmlRowIdentityAssurance assurance,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var (schema, table) = await ResolvePhysicalTargetAsync(
            connectionString,
            tableName,
            cancellationToken);
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
            return new DmlRowIdentityResolution(schema, table, primaryKey);

        if (assurance == DmlRowIdentityAssurance.CountOnly)
            return new DmlRowIdentityResolution(schema, table, ImmutableArray<string>.Empty);

        throw new InvalidOperationException(
            $"Strict DML row-identity assurance requires a primary key on '{schema}.{table}'.");
    }

    private async Task<(string Schema, string Table)> ResolvePhysicalTargetAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken)
    {
        var parts = tableName
            .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
            return (parts[0], parts[1]);

        if (parts.Length != 1)
        {
            throw new InvalidOperationException(
                $"DML target '{tableName}' must be <table> or <schema>.<table> for row-identity planning.");
        }

        var requestedTable = parts[0];
        var matches = new List<(string Schema, string Table)>();
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
                    matches.Add((schema, table));
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(
                $"DML target '{tableName}' could not be resolved to a physical table. Schema-qualify the target explicitly."),
            _ => throw new InvalidOperationException(
                $"DML target '{tableName}' is ambiguous across schemas. Schema-qualify the target explicitly.")
        };
    }
}
