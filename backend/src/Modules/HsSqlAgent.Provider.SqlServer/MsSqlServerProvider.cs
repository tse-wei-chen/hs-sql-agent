using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using HsSqlAgent.Provider.Abstractions;
using HsSqlAgent.SqlCore.Enums;
using HsSqlAgent.SqlCore.Models;

namespace HsSqlAgent.Provider.SqlServer;

public class MsSqlServerProvider : SqlProviderBase
{
    public override SqlAgentToolType DbType => SqlAgentToolType.MsSqlServer;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = model.Host,
            UserID = model.Username,
            Password = model.Password,
            InitialCatalog = model.Database
        };
        if (!string.IsNullOrEmpty(model.Port)) builder.DataSource += $",{model.Port}";
        if (!string.IsNullOrEmpty(model.ExtraSettings))
        {
            try
            {
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(model.ExtraSettings);
                if (settings != null)
                {
                    if (settings.TryGetValue("TrustServerCertificate", out var trust) && bool.TryParse(trust.ToString(), out var trustValue))
                        builder.TrustServerCertificate = trustValue;
                    if (settings.TryGetValue("Encrypt", out var encrypt) && bool.TryParse(encrypt.ToString(), out var encryptValue))
                        builder.Encrypt = encryptValue;
                }
            }
            catch { }
        }
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new SqlConnection(connectionString);

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT name FROM sys.schemas WHERE principal_id = 1 OR name = 'dbo';";
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: cancellationToken))];
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = @schemaName
              AND TABLE_TYPE = 'BASE TABLE';";
        return [.. await connection.QueryAsync<string>(new CommandDefinition(sql, new { schemaName }, cancellationToken: cancellationToken))];
    }

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT c.COLUMN_NAME, c.DATA_TYPE,
                   CASE WHEN ic.column_id IS NULL THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS IS_PRIMARY_KEY,
                   ic.key_ordinal AS PRIMARY_KEY_ORDINAL
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN sys.schemas s ON s.name = c.TABLE_SCHEMA
            LEFT JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = c.TABLE_NAME
            LEFT JOIN sys.indexes i ON i.object_id = t.object_id AND i.is_primary_key = 1
            LEFT JOIN sys.columns sc ON sc.object_id = t.object_id AND sc.name = c.COLUMN_NAME
            LEFT JOIN sys.index_columns ic ON ic.object_id = t.object_id AND ic.index_id = i.index_id AND ic.column_id = sc.column_id
            WHERE c.TABLE_SCHEMA = @schemaName AND c.TABLE_NAME = @tableName
            ORDER BY c.ORDINAL_POSITION";
        var rows = await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new ColumnInfo((string)r.COLUMN_NAME, (string)r.DATA_TYPE, (bool)r.IS_PRIMARY_KEY,
            r.PRIMARY_KEY_ORDINAL is null ? null : Convert.ToInt32(r.PRIMARY_KEY_ORDINAL)))];
    }

    public override async Task<List<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT i.name AS INDEX_NAME, i.is_primary_key AS IS_PRIMARY_KEY, i.has_filter AS IS_PARTIAL,
                   i.is_disabled AS IS_DISABLED, i.is_hypothetical AS IS_HYPOTHETICAL,
                   ic.key_ordinal AS KEY_ORDINAL, c.name AS COLUMN_NAME, c.is_computed AS IS_COMPUTED
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.indexes i ON i.object_id = t.object_id AND i.is_unique = 1
            LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = @schemaName AND t.name = @tableName
            ORDER BY i.name, ic.key_ordinal";
        var rows = (await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken))).ToArray();
        return [.. rows.GroupBy(row => (string)row.INDEX_NAME, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var ordered = group.OrderBy(row => row.KEY_ORDINAL is null ? int.MaxValue : Convert.ToInt32(row.KEY_ORDINAL)).ToArray();
            var first = ordered[0];
            return new DatabaseUniqueKeyMetadata(schemaName, tableName, group.Key, (bool)first.IS_PRIMARY_KEY,
                ordered.Where(row => row.COLUMN_NAME is not null).Select(row => (string)row.COLUMN_NAME).ToArray(),
                IsPartial: (bool)first.IS_PARTIAL,
                HasExpressions: ordered.Any(row => row.IS_COMPUTED is not null && (bool)row.IS_COMPUTED),
                IsEnforced: !(bool)first.IS_DISABLED && !(bool)first.IS_HYPOTHETICAL);
        })];
    }
}
