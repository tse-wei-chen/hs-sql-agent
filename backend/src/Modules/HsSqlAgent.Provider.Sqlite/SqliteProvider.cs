using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using HsSqlAgent.Provider.Abstractions;

namespace HsSqlAgent.Provider.Sqlite;

public class SqliteProvider : SqlProviderBase
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Sqlite;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = model.Database, Password = model.Password };
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new SqliteConnection(connectionString);
    public override Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default) => Task.FromResult(new List<string> { "main" });

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return [.. await connection.QueryAsync<string>(new CommandDefinition("SELECT name FROM sqlite_master WHERE type='table';", cancellationToken: cancellationToken))];
    }

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name = @tbl;";
        var verified = await connection.QueryFirstOrDefaultAsync<string>(checkSql, new { tbl = tableName });
        if (string.IsNullOrEmpty(verified)) return [];
        var escaped = verified.Replace("'", "''", StringComparison.Ordinal);
        var rows = await connection.QueryAsync(new CommandDefinition($"SELECT name AS COLUMN_NAME, type AS DATA_TYPE, pk AS PRIMARY_KEY_ORDINAL FROM pragma_table_info('{escaped}') ORDER BY cid", cancellationToken: cancellationToken));
        return [.. rows.Select(r => { var pk = Convert.ToInt32(r.PRIMARY_KEY_ORDINAL); return new ColumnInfo((string)r.COLUMN_NAME, (string)r.DATA_TYPE, pk > 0, pk > 0 ? pk : null); })];
    }

    public override async Task<List<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string checkSql = "SELECT name FROM sqlite_master WHERE type='table' AND name = @tbl;";
        var verified = await connection.QueryFirstOrDefaultAsync<string>(new CommandDefinition(checkSql, new { tbl = tableName }, cancellationToken: cancellationToken));
        if (string.IsNullOrEmpty(verified)) return [];
        var escaped = verified.Replace("'", "''", StringComparison.Ordinal);
        var rows = (await connection.QueryAsync(new CommandDefinition($@"
            SELECT il.name AS INDEX_NAME, il.origin AS ORIGIN, il.partial AS IS_PARTIAL,
                   ii.seqno AS KEY_ORDINAL, ii.cid AS COLUMN_ID, ii.name AS COLUMN_NAME
            FROM pragma_index_list('{escaped}') il
            LEFT JOIN pragma_index_info(il.name) ii
            WHERE il.[unique] = 1
            ORDER BY il.seq, ii.seqno", cancellationToken: cancellationToken))).ToArray();
        var keys = rows.GroupBy(row => (string)row.INDEX_NAME, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var ordered = group.OrderBy(row => row.KEY_ORDINAL is null ? int.MaxValue : Convert.ToInt32(row.KEY_ORDINAL)).ToArray();
            var first = ordered[0];
            return new DatabaseUniqueKeyMetadata(schemaName, tableName, group.Key,
                string.Equals((string)first.ORIGIN, "pk", StringComparison.OrdinalIgnoreCase),
                ordered.Where(row => row.COLUMN_NAME is not null).Select(row => (string)row.COLUMN_NAME).ToArray(),
                IsPartial: Convert.ToInt32(first.IS_PARTIAL) != 0,
                HasExpressions: ordered.Any(row => row.COLUMN_ID is null || Convert.ToInt32(row.COLUMN_ID) < 0 || row.COLUMN_NAME is null));
        }).ToList();
        if (!keys.Any(key => key.IsPrimaryKey))
        {
            var pkRows = (await connection.QueryAsync(new CommandDefinition($"SELECT name AS COLUMN_NAME, pk AS KEY_ORDINAL FROM pragma_table_info('{escaped}') WHERE pk > 0 ORDER BY pk", cancellationToken: cancellationToken))).ToArray();
            if (pkRows.Length > 0) keys.Add(new DatabaseUniqueKeyMetadata(schemaName, tableName, "PRIMARY", true, pkRows.Select(row => (string)row.COLUMN_NAME).ToArray()));
        }
        return keys;
    }
}
