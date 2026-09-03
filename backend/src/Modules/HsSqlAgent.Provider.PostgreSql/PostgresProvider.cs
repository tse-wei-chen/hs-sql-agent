using System.Data.Common;
using Dapper;
using Npgsql;
using HsSqlAgent.Provider.Abstractions;

namespace HsSqlAgent.Provider.PostgreSql;

public class PostgresProvider : SqlProviderBase
{
    public override SqlAgentToolType DbType => SqlAgentToolType.Postgres;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = model.Host,
            Port = string.IsNullOrEmpty(model.Port) ? 5432 : int.Parse(model.Port),
            Username = model.Username,
            Password = model.Password,
            Database = model.Database
        };
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new NpgsqlConnection(connectionString);

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>(new CommandDefinition("SELECT schema_name FROM information_schema.schemata;", cancellationToken: cancellationToken))];
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting schemas: {ex.Message}, please try again !!");
        }
    }

    public override async Task<List<string>> GetTablesAsync(string connectionString, string schemaName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>(new CommandDefinition("SELECT table_name FROM information_schema.tables WHERE table_schema = @schemaName;", new { schemaName }, cancellationToken: cancellationToken))];
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting tables: {ex.Message}, please try again !!");
        }
    }

    public override async Task<IReadOnlyList<DatabaseTableMetadata>> FindTablesAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string sql = @"
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE lower(table_name) = lower(@tableName);";
        var rows = await connection.QueryAsync(new CommandDefinition(
            sql,
            new { tableName },
            cancellationToken: cancellationToken));
        return [.. rows.Select(row => new DatabaseTableMetadata(
            (string)row.table_schema,
            (string)row.table_name))];
    }

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT c.column_name, c.data_type,
                   (pk.ordinal_position IS NOT NULL) AS is_primary_key,
                   pk.ordinal_position AS primary_key_ordinal
            FROM information_schema.columns c
            LEFT JOIN (
                SELECT kcu.column_name, kcu.ordinal_position
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON tc.constraint_name = kcu.constraint_name
                 AND tc.constraint_schema = kcu.constraint_schema
                 AND tc.table_schema = kcu.table_schema
                 AND tc.table_name = kcu.table_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                  AND tc.table_schema = @schemaName
                  AND tc.table_name = @tableName
            ) pk ON pk.column_name = c.column_name
            WHERE c.table_schema = @schemaName AND c.table_name = @tableName
            ORDER BY c.ordinal_position;";
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken));
            return [.. rows.Select(r => new ColumnInfo((string)r.column_name, (string)r.data_type, (bool)r.is_primary_key, r.primary_key_ordinal is null ? null : Convert.ToInt32(r.primary_key_ordinal)))];
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting columns: {ex.Message}, please try again !!");
        }
    }

    public override async Task<List<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT idx.relname AS index_name, i.indisprimary AS is_primary_key,
                   (i.indisvalid AND i.indisready AND i.indislive) AS is_enforced,
                   (i.indpred IS NOT NULL) AS is_partial,
                   key_part.ordinality AS key_ordinal, key_part.attnum AS attribute_number,
                   a.attname AS column_name
            FROM pg_class tbl
            JOIN pg_namespace ns ON ns.oid = tbl.relnamespace
            JOIN pg_index i ON i.indrelid = tbl.oid
            JOIN pg_class idx ON idx.oid = i.indexrelid
            JOIN LATERAL unnest(i.indkey) WITH ORDINALITY AS key_part(attnum, ordinality)
              ON key_part.ordinality <= i.indnkeyatts
            LEFT JOIN pg_attribute a ON a.attrelid = tbl.oid AND a.attnum = key_part.attnum
            WHERE ns.nspname = @schemaName AND tbl.relname = @tableName AND i.indisunique
            ORDER BY idx.relname, key_part.ordinality;";
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            var rows = (await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken))).ToArray();
            return [.. rows.GroupBy(row => (string)row.index_name, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var ordered = group.OrderBy(row => Convert.ToInt32(row.key_ordinal)).ToArray();
                var columns = ordered.Where(row => row.column_name is not null).Select(row => (string)row.column_name).ToArray();
                var first = ordered[0];
                return new DatabaseUniqueKeyMetadata(schemaName, tableName, group.Key, (bool)first.is_primary_key, columns,
                    IsPartial: (bool)first.is_partial,
                    HasExpressions: ordered.Any(row => Convert.ToInt32(row.attribute_number) == 0 || row.column_name is null),
                    IsEnforced: (bool)first.is_enforced);
            })];
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting unique keys: {ex.Message}, please try again !!");
        }
    }
}
