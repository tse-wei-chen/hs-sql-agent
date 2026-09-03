using System.Data.Common;
using Dapper;
using MySql.Data.MySqlClient;
using HsSqlAgent.Provider.Abstractions;

namespace HsSqlAgent.Provider.MySql;

public class MySqlProvider : SqlProviderBase, IProviderConnectionMetadataReader, IProviderConnectionDmlPlanningMetadataReader
{
    public override SqlAgentToolType DbType => SqlAgentToolType.MySQL;

    public override string BuildConnectionString(BuildDbConnectionModelBase model)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = model.Host,
            Port = string.IsNullOrEmpty(model.Port) ? 3306 : uint.Parse(model.Port),
            UserID = model.Username,
            Password = model.Password,
            Database = model.Database
        };
        return builder.ConnectionString;
    }

    public override DbConnection CreateConnection(string? connectionString) => new MySqlConnection(connectionString);

    public override async Task<List<string>> GetSchemasAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return [.. await connection.QueryAsync<string>(new CommandDefinition("SHOW DATABASES;", cancellationToken: cancellationToken))];
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
            const string sql = @"
                SELECT TABLE_NAME
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = @schemaName
                  AND TABLE_TYPE = 'BASE TABLE';";
            return [.. await connection.QueryAsync<string>(new CommandDefinition(sql, new { schemaName }, cancellationToken: cancellationToken))];
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
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM information_schema.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
              AND LOWER(TABLE_NAME) = LOWER(@tableName);";
        var rows = await connection.QueryAsync(new CommandDefinition(
            sql,
            new { tableName },
            cancellationToken: cancellationToken));
        return [.. rows.Select(row => new DatabaseTableMetadata(
            (string)row.TABLE_SCHEMA,
            (string)row.TABLE_NAME))];
    }

    public async Task<IReadOnlyList<DatabaseTableMetadata>> FindTablesAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        const string sql = @"
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM information_schema.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
              AND LOWER(TABLE_NAME) = LOWER(@tableName);";
        var rows = await connection.QueryAsync(new CommandDefinition(
            sql,
            new { tableName },
            cancellationToken: cancellationToken));
        return [.. rows.Select(row => new DatabaseTableMetadata(
            (string)row.TABLE_SCHEMA,
            (string)row.TABLE_NAME))];
    }

    public override async Task<List<ColumnInfo>> GetColumnsAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = @"
                SELECT c.COLUMN_NAME, c.DATA_TYPE,
                       CASE WHEN kcu.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IS_PRIMARY_KEY,
                       kcu.ORDINAL_POSITION AS PRIMARY_KEY_ORDINAL
                FROM INFORMATION_SCHEMA.COLUMNS c
                LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                  ON tc.CONSTRAINT_SCHEMA = c.TABLE_SCHEMA
                 AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                 AND tc.TABLE_NAME = c.TABLE_NAME
                 AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                  ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
                 AND kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA
                 AND kcu.TABLE_NAME = tc.TABLE_NAME
                 AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                 AND kcu.COLUMN_NAME = c.COLUMN_NAME
                WHERE c.TABLE_SCHEMA = @schemaName AND c.TABLE_NAME = @tableName
                ORDER BY c.ORDINAL_POSITION";
            var rows = await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken));
            return [.. rows.Select(r => new ColumnInfo((string)r.COLUMN_NAME, (string)r.DATA_TYPE,
                Convert.ToInt32(r.IS_PRIMARY_KEY) != 0,
                r.PRIMARY_KEY_ORDINAL is null ? null : Convert.ToInt32(r.PRIMARY_KEY_ORDINAL)))];
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting columns: {ex.Message}, please try again !!");
        }
    }

    public async Task<IReadOnlyList<DatabaseColumnMetadata>> GetColumnsAsync(
        DbConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        const string sql = @"
            SELECT c.COLUMN_NAME, c.DATA_TYPE,
                   CASE WHEN kcu.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IS_PRIMARY_KEY,
                   kcu.ORDINAL_POSITION AS PRIMARY_KEY_ORDINAL
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
              ON tc.CONSTRAINT_SCHEMA = c.TABLE_SCHEMA
             AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
             AND tc.TABLE_NAME = c.TABLE_NAME
             AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
             AND kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA
             AND kcu.TABLE_NAME = tc.TABLE_NAME
             AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
             AND kcu.COLUMN_NAME = c.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = @schemaName AND c.TABLE_NAME = @tableName
            ORDER BY c.ORDINAL_POSITION";
        var rows = await connection.QueryAsync(new CommandDefinition(
            sql,
            new { schemaName, tableName },
            cancellationToken: cancellationToken));
        return [.. rows.Select(r => new DatabaseColumnMetadata(
            schemaName,
            tableName,
            (string)r.COLUMN_NAME,
            (string)r.DATA_TYPE,
            Convert.ToInt32(r.IS_PRIMARY_KEY) != 0,
            r.PRIMARY_KEY_ORDINAL is null ? null : Convert.ToInt32(r.PRIMARY_KEY_ORDINAL)))];
    }

    public async Task<IReadOnlyList<DatabaseDmlPlanningMetadata>> GetDmlPlanningMetadataAsync(
        DbConnection connection,
        string? schemaName,
        string tableName,
        bool includeColumns,
        DmlOperation? triggerOperation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (triggerOperation.HasValue)
            throw new NotSupportedException("MySQL DML planning does not expose SQL Server trigger assurance.");

        var schemaPredicate = schemaName is null
            ? string.Empty
            : " AND LOWER(t.TABLE_SCHEMA) = LOWER(@schemaName)";

        if (!includeColumns)
        {
            var targetSql = $@"
                SELECT t.TABLE_SCHEMA, t.TABLE_NAME
                FROM information_schema.TABLES t
                WHERE t.TABLE_TYPE = 'BASE TABLE'
                  AND LOWER(t.TABLE_NAME) = LOWER(@tableName){schemaPredicate}
                ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME;";
            var targets = await connection.QueryAsync(new CommandDefinition(
                targetSql,
                new { schemaName, tableName },
                cancellationToken: cancellationToken));
            return [.. targets.Select(row => new DatabaseDmlPlanningMetadata(
                (string)row.TABLE_SCHEMA,
                (string)row.TABLE_NAME,
                []))];
        }

        var sql = $@"
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME,
                   c.COLUMN_NAME, c.DATA_TYPE,
                   CASE WHEN kcu.COLUMN_NAME IS NULL THEN 0 ELSE 1 END AS IS_PRIMARY_KEY,
                   kcu.ORDINAL_POSITION AS PRIMARY_KEY_ORDINAL
            FROM information_schema.TABLES t
            LEFT JOIN INFORMATION_SCHEMA.COLUMNS c
              ON c.TABLE_SCHEMA = t.TABLE_SCHEMA
             AND c.TABLE_NAME = t.TABLE_NAME
            LEFT JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
              ON tc.CONSTRAINT_SCHEMA = c.TABLE_SCHEMA
             AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
             AND tc.TABLE_NAME = c.TABLE_NAME
             AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
              ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
             AND kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA
             AND kcu.TABLE_NAME = tc.TABLE_NAME
             AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
             AND kcu.COLUMN_NAME = c.COLUMN_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE'
              AND LOWER(t.TABLE_NAME) = LOWER(@tableName){schemaPredicate}
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME, c.ORDINAL_POSITION;";
        var rows = (await connection.QueryAsync(new CommandDefinition(
            sql,
            new { schemaName, tableName },
            cancellationToken: cancellationToken))).ToArray();

        return [.. rows
            .GroupBy(row => ((string)row.TABLE_SCHEMA, (string)row.TABLE_NAME))
            .Select(group => new DatabaseDmlPlanningMetadata(
                group.Key.Item1,
                group.Key.Item2,
                [.. group
                    .Where(row => row.COLUMN_NAME is not null)
                    .Select(row => new DatabaseColumnMetadata(
                        group.Key.Item1,
                        group.Key.Item2,
                        (string)row.COLUMN_NAME,
                        (string)row.DATA_TYPE,
                        Convert.ToInt32(row.IS_PRIMARY_KEY) != 0,
                        row.PRIMARY_KEY_ORDINAL is null
                            ? null
                            : Convert.ToInt32(row.PRIMARY_KEY_ORDINAL))) ]))];
    }

    public override async Task<List<DatabaseUniqueKeyMetadata>> GetUniqueKeysAsync(string connectionString, string schemaName, string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = @"
                SELECT INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, SUB_PART
                FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = @schemaName
                  AND TABLE_NAME = @tableName
                  AND NON_UNIQUE = 0
                ORDER BY INDEX_NAME, SEQ_IN_INDEX";
            var rows = (await connection.QueryAsync(new CommandDefinition(sql, new { schemaName, tableName }, cancellationToken: cancellationToken))).ToArray();
            return [.. rows.GroupBy(row => (string)row.INDEX_NAME, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var ordered = group.OrderBy(row => Convert.ToInt32(row.SEQ_IN_INDEX)).ToArray();
                var columns = ordered.Where(row => row.COLUMN_NAME is not null).Select(row => (string)row.COLUMN_NAME).ToArray();
                return new DatabaseUniqueKeyMetadata(schemaName, tableName, group.Key,
                    string.Equals(group.Key, "PRIMARY", StringComparison.OrdinalIgnoreCase), columns,
                    HasExpressions: ordered.Any(row => row.COLUMN_NAME is null),
                    HasPrefixKeyParts: ordered.Any(row => row.SUB_PART is not null));
            })];
        }
        catch (Exception ex)
        {
            throw new Exception($"Error getting unique keys: {ex.Message}, please try again !!");
        }
    }
}
